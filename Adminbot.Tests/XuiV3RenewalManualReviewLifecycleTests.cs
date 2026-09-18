using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

/// <summary>
/// Regression coverage for the durable XUI v3 renewal manual-review lifecycle.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these tests protect against.</b> Bounded automatic reconciliation escalates an inconclusive renewal to
/// <c>manual_review</c> and correctly keeps its account lock, but nothing could release that lock: the customer was told
/// to wait for an automatic process that would never resolve it, the operation kept blocking every new renewal forever,
/// and historical rows written before the recovery protocol could not even be classified. The lifecycle under test adds
/// state-aware customer wording, administrator resolution, and exactly-once alerting without weakening the existing
/// exactly-once mutation and settlement guarantees.
/// </para>
/// <para>
/// <b>Invariants asserted throughout.</b> No path may replay <c>POST /UpdateClient</c>; only one executor may ever enter
/// settlement for one operation; an operation with a durable financial artifact may never be unlocked; and a
/// recovery-ineligible historical row may only be released by an explicit super-admin override.
/// </para>
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>One binary gigabyte in bytes, matching the renewal policy's traffic unit.</summary>
    private const long ManualReviewGib = 1024L * 1024L * 1024L;

    /// <summary>Payer Telegram id used by the manual-review fixtures.</summary>
    private const long ManualReviewPayerId = 5150;

    /// <summary>Configured super-admin Telegram id used by the manual-review administrator tests.</summary>
    private const long ManualReviewAdminId = 85758085;

    /// <summary>Client email used by the panel-backed manual-review comparisons.</summary>
    private const string ManualReviewPanelEmail = "manual-review-account";

    /// <summary>
    /// A customer-facing manual-review notice must ask for support and must never promise automatic review.
    /// </summary>
    /// <returns>A task completing after the mapping assertions.</returns>
    /// <remarks>
    /// Protects the exact wording contract: the previous single sentence told every blocked customer that an automatic
    /// process was working on the account, which was false for a manual review and for an already-applied renewal.
    /// </remarks>
    [Fact]
    public void Manual_review_notice_requires_support_and_never_promises_automatic_review()
    {
        var operation = new XuiV3RenewalOperation
        {
            OperationId = "renew-secret-operation-id",
            Status = XuiV3RenewalOperationStatuses.ManualReview,
            SettlementStatus = XuiV3RenewalSettlementStatuses.Pending,
            RecoveryEligible = true,
            TargetEmail = "leak@example.test",
            TargetUuid = "2f1b2c3d-4e5f-4a6b-8c9d-0e1f2a3b4c5d",
            LastError = "internal-panel-error-text"
        };

        var notice = XuiV3RenewalBlockingNoticeBuilder.Build(operation);

        Assert.Equal(XuiV3RenewalBlockingNoticeKind.ManualReviewRequired, notice.Kind);
        Assert.Contains("پشتیبانی", notice.Text);
        Assert.DoesNotContain("بررسی خودکار", notice.Text);
        Assert.DoesNotContain("بررسی خودکار", notice.ShortText);
        // No operation id, account email, UUID, or internal error text may reach the customer.
        Assert.DoesNotContain(operation.OperationId, notice.Text);
        Assert.DoesNotContain(operation.TargetEmail, notice.Text);
        Assert.DoesNotContain(operation.TargetUuid, notice.Text);
        Assert.DoesNotContain(operation.LastError, notice.Text);
    }

    /// <summary>
    /// Each blocking state maps to its own truthful notice, including the applied and historical cases.
    /// </summary>
    /// <returns>A task completing after the mapping assertions.</returns>
    /// <remarks>
    /// The applied-but-unsettled case is the second most misleading one in production: the renewal already succeeded, so
    /// telling the customer to wait for "review" hides that only settlement remains.
    /// </remarks>
    [Fact]
    public void Applied_and_historical_blocking_states_have_distinct_notices()
    {
        var applied = XuiV3RenewalBlockingNoticeBuilder.Build(new XuiV3RenewalOperation
        {
            Status = XuiV3RenewalOperationStatuses.Applied,
            SettlementStatus = XuiV3RenewalSettlementStatuses.Pending,
            RecoveryEligible = true
        });
        Assert.Equal(XuiV3RenewalBlockingNoticeKind.AppliedSettlementInProgress, applied.Kind);
        Assert.Contains("اعمال شده", applied.Text);

        var automatic = XuiV3RenewalBlockingNoticeBuilder.Build(new XuiV3RenewalOperation
        {
            Status = XuiV3RenewalOperationStatuses.Ambiguous,
            RecoveryEligible = true
        });
        Assert.Equal(XuiV3RenewalBlockingNoticeKind.AutomaticReview, automatic.Kind);

        var historical = XuiV3RenewalBlockingNoticeBuilder.Build(new XuiV3RenewalOperation
        {
            Status = XuiV3RenewalOperationStatuses.ManualReview,
            RecoveryEligible = false
        });
        Assert.Equal(XuiV3RenewalBlockingNoticeKind.HistoricalManualReview, historical.Kind);
        Assert.Contains("پشتیبانی", historical.Text);
        Assert.DoesNotContain("بررسی خودکار", historical.Text);

        // An unknown lock is reported conservatively as automatic verification rather than claiming a human is involved.
        Assert.Equal(
            XuiV3RenewalBlockingNoticeKind.AutomaticReview,
            XuiV3RenewalBlockingNoticeBuilder.Build(null).Kind);
    }

    /// <summary>
    /// Pending recovery-ineligible historical rows are surfaced to administrators alongside eligible ones.
    /// </summary>
    /// <returns>A task completing after the pending-list assertions.</returns>
    /// <remarks>
    /// A historical row holds a real account lock even though no automatic comparison can ever classify it. If it were
    /// hidden from operators it would keep blocking the customer with no possible remedy.
    /// </remarks>
    [Fact]
    public async Task Legacy_recovery_ineligible_manual_review_is_surfaced_to_administrators()
    {
        using var databases = new Databases();
        var store = ManualReviewStore(databases);
        var eligibleId = await SeedManualReviewAsync(databases, "eligible", recoveryEligible: true, holdLock: true);
        var legacyId = await SeedManualReviewAsync(databases, "legacy", recoveryEligible: false, holdLock: true);
        var resolvedId = await SeedManualReviewAsync(databases, "resolved", recoveryEligible: true, holdLock: false,
            resolvedAtUtc: DateTime.UtcNow);

        var pending = await store.ListPendingManualReviewsAsync(10);
        var ids = pending.Select(x => x.Id).ToList();

        Assert.Contains(eligibleId, ids);
        Assert.Contains(legacyId, ids);
        Assert.DoesNotContain(resolvedId, ids);
        Assert.False(pending.Single(x => x.Id == legacyId).RecoveryEligible);
        // No account identity or payload is needed to render the operator list.
        Assert.All(pending, x => Assert.Null(x.MutationPayloadJson));
    }

    /// <summary>
    /// One manual-review escalation produces exactly one durable operator notification.
    /// </summary>
    /// <returns>A task completing after the repeated-claim assertions.</returns>
    /// <remarks>
    /// Every reconciliation scan sees the same locked row, so without a durable claim marker the operator channel would
    /// receive the same alert again and again until it stopped being readable as an incident.
    /// </remarks>
    [Fact]
    public async Task Manual_review_notification_is_claimed_exactly_once()
    {
        using var databases = new Databases();
        var store = ManualReviewStore(databases);
        var operationId = await SeedManualReviewAsync(databases, "notify", recoveryEligible: true, holdLock: true);
        var operation = await store.GetByIdAsync(operationId);

        Assert.True(await store.TryClaimManualReviewNotificationAsync(operation));
        Assert.False(await store.TryClaimManualReviewNotificationAsync(operation));

        // The sweep is the crash-window safety net and observes the same durable marker.
        Assert.Empty(await store.ClaimUnnotifiedManualReviewsAsync(10));

        var persisted = await store.GetByIdAsync(operationId);
        Assert.NotNull(persisted.ManualReviewNotifiedAtUtc);
    }

    /// <summary>
    /// The notification sweep announces a pre-existing locked row exactly once, which is how historical locks surface.
    /// </summary>
    /// <returns>A task completing after the sweep assertions.</returns>
    /// <remarks>
    /// This covers a row that was already in manual review before the feature shipped, or whose process stopped between
    /// the escalation and its alert.
    /// </remarks>
    [Fact]
    public async Task Unnotified_manual_reviews_are_announced_once_by_the_sweep()
    {
        using var databases = new Databases();
        var store = ManualReviewStore(databases);
        var operationId = await SeedManualReviewAsync(databases, "sweep", recoveryEligible: false, holdLock: true);

        var firstSweep = await store.ClaimUnnotifiedManualReviewsAsync(10);
        Assert.Single(firstSweep);
        Assert.Equal(operationId, firstSweep[0].Id);

        Assert.Empty(await store.ClaimUnnotifiedManualReviewsAsync(10));
    }

    /// <summary>
    /// A re-check records evidence and changes no durable state.
    /// </summary>
    /// <returns>A task completing after the read-only assertions.</returns>
    /// <remarks>
    /// Reprobe must stay safe to press repeatedly, so it may not transition the operation, touch settlement, or release
    /// the account lock.
    /// </remarks>
    [Fact]
    public async Task Reprobe_records_evidence_without_resolving_the_operation()
    {
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        var configuration = ManualReviewConfiguration(panel);
        await SeedPanelClientAsync(panel, configuration, ManualReviewTargetTotalBytes(), ManualReviewTargetExpiryMs());
        var (store, service, _) = ManualReviewService(databases, configuration);
        var operationId = await SeedPanelManualReviewAsync(databases, recoveryEligible: true, holdLock: true);
        var before = await store.GetByIdAsync(operationId);

        var result = await service.ReprobeAsync(operationId);

        Assert.Equal(XuiV3RenewalManualReviewService.ManualReviewOutcome.ReprobeCompared, result.Outcome);
        Assert.Equal("Applied", result.ComparisonOutcome);

        var after = await store.GetByIdAsync(operationId);
        Assert.Equal(XuiV3RenewalOperationStatuses.ManualReview, after.Status);
        Assert.Equal(XuiV3RenewalSettlementStatuses.Pending, after.SettlementStatus);
        Assert.Equal(before.AccountLockKey, after.AccountLockKey);
        Assert.Equal("Applied", after.LastComparisonOutcome);
        Assert.Null(after.ManualReviewResolvedAtUtc);
    }

    /// <summary>
    /// Confirming an applied operation continues the shared exactly-once settlement path exactly once.
    /// </summary>
    /// <returns>A task completing after the settlement-entry assertions.</returns>
    /// <remarks>
    /// The count is asserted on the shared settlement router, which is the same type the background worker uses, so a
    /// confirmation cannot reach a second debit implementation.
    /// </remarks>
    [Fact]
    public async Task Confirm_applied_uses_the_shared_settlement_path_exactly_once()
    {
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        var configuration = ManualReviewConfiguration(panel);
        await SeedPanelClientAsync(panel, configuration, ManualReviewTargetTotalBytes(), ManualReviewTargetExpiryMs());
        var (store, service, router) = ManualReviewService(databases, configuration);
        var operationId = await SeedPanelManualReviewAsync(databases, recoveryEligible: true, holdLock: true);

        var first = await service.ConfirmAppliedAsync(operationId, ManualReviewAdminId);

        Assert.Equal(XuiV3RenewalManualReviewService.ManualReviewOutcome.ConfirmedApplied, first.Outcome);
        Assert.Equal(1, router.SettlementEntries);
        var applied = await store.GetByIdAsync(operationId);
        Assert.Equal(XuiV3RenewalOperationStatuses.Applied, applied.Status);
        Assert.Equal(XuiV3RenewalManualReviewResolutions.ConfirmedApplied, applied.ManualReviewResolution);
        Assert.Equal(ManualReviewAdminId, applied.ManualReviewResolvedByTelegramUserId);

        // A repeated confirmation must not reach settlement a second time.
        var second = await service.ConfirmAppliedAsync(operationId, ManualReviewAdminId);
        Assert.Equal(XuiV3RenewalManualReviewService.ManualReviewOutcome.NotUnderManualReview, second.Outcome);
        Assert.Equal(1, router.SettlementEntries);
    }

    /// <summary>
    /// Two concurrent confirmations can never both continue into settlement.
    /// </summary>
    /// <returns>A task completing after the race assertions.</returns>
    /// <remarks>
    /// The single <c>manual_review to applied</c> conditional UPDATE is the concurrency boundary; the loser must observe
    /// the already-resolved state instead of settling. This protects against a double debit from a double click.
    /// </remarks>
    [Fact]
    public async Task Concurrent_confirmations_settle_at_most_once()
    {
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        var configuration = ManualReviewConfiguration(panel);
        await SeedPanelClientAsync(panel, configuration, ManualReviewTargetTotalBytes(), ManualReviewTargetExpiryMs());
        var (_, service, router) = ManualReviewService(databases, configuration);
        var operationId = await SeedPanelManualReviewAsync(databases, recoveryEligible: true, holdLock: true);

        var results = await Task.WhenAll(
            service.ConfirmAppliedAsync(operationId, ManualReviewAdminId),
            service.ConfirmAppliedAsync(operationId, ManualReviewAdminId));

        Assert.Equal(1, results.Count(x =>
            x.Outcome == XuiV3RenewalManualReviewService.ManualReviewOutcome.ConfirmedApplied));
        Assert.Equal(1, results.Count(x =>
            x.Outcome is XuiV3RenewalManualReviewService.ManualReviewOutcome.AlreadyResolved
                or XuiV3RenewalManualReviewService.ManualReviewOutcome.NotUnderManualReview
                or XuiV3RenewalManualReviewService.ManualReviewOutcome.ComparisonNotApplied));
        Assert.Equal(1, router.SettlementEntries);
    }

    /// <summary>
    /// Confirmation is refused when a fresh panel comparison does not prove the renewal was applied.
    /// </summary>
    /// <returns>A task completing after the refusal assertions.</returns>
    /// <remarks>
    /// A customer-paid renewal must never be marked applied from an assumption or from a previously stored result.
    /// </remarks>
    [Fact]
    public async Task Confirm_applied_is_refused_when_the_panel_does_not_hold_the_target()
    {
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        var configuration = ManualReviewConfiguration(panel);
        await SeedPanelClientAsync(panel, configuration, ManualReviewPreTotalBytes(), ManualReviewPreExpiryMs());
        var (store, service, router) = ManualReviewService(databases, configuration);
        var operationId = await SeedPanelManualReviewAsync(databases, recoveryEligible: true, holdLock: true);

        var result = await service.ConfirmAppliedAsync(operationId, ManualReviewAdminId);

        Assert.Equal(XuiV3RenewalManualReviewService.ManualReviewOutcome.ComparisonNotApplied, result.Outcome);
        Assert.Equal("DefinitelyPreMutation", result.ComparisonOutcome);
        Assert.Equal(0, router.SettlementEntries);
        var operation = await store.GetByIdAsync(operationId);
        Assert.Equal(XuiV3RenewalOperationStatuses.ManualReview, operation.Status);
        Assert.NotNull(operation.AccountLockKey);
    }

    /// <summary>
    /// Abandonment is refused for every durable financial artifact of the operation.
    /// </summary>
    /// <param name="artifact">Which durable financial record is seeded before the attempt.</param>
    /// <returns>A task completing after the refusal assertions.</returns>
    /// <remarks>
    /// Releasing the lock lets the customer renew again. If money already moved for the abandoned operation, that would
    /// charge twice for one panel account, so each artifact type must independently refuse the unlock.
    /// </remarks>
    [Theory]
    [InlineData("wallet-ledger")]
    [InlineData("wallet-receipt")]
    [InlineData("site-wallet-receipt")]
    public async Task Abandon_is_refused_when_a_durable_financial_artifact_exists(string artifact)
    {
        using var databases = new Databases();
        var configuration = ManualReviewConfiguration(null);
        var (store, service, _) = ManualReviewService(databases, configuration);
        var operationId = await SeedManualReviewAsync(databases, "artifact-" + artifact, recoveryEligible: true, holdLock: true);
        var operation = await store.GetByIdAsync(operationId);
        var ledgerKey = XuiV3RenewalOperationStore.BuildSettlementLedgerKey(operation);

        await SeedFinancialArtifactAsync(databases, artifact, ledgerKey, operation);

        var result = await service.AbandonAsNotAppliedAsync(operationId, ManualReviewAdminId, legacyOverrideConfirmed: false);

        Assert.Equal(XuiV3RenewalManualReviewService.ManualReviewOutcome.SettlementArtifactExists, result.Outcome);
        var unchanged = await store.GetByIdAsync(operationId);
        Assert.Equal(XuiV3RenewalOperationStatuses.ManualReview, unchanged.Status);
        Assert.NotNull(unchanged.AccountLockKey);
        // A refused abandonment must leave the row blocking, so no new renewal can start behind a possible charge.
        Assert.NotNull(await store.FindBlockingOperationAsync(unchanged.TargetUuid, unchanged.TargetEmail));
    }

    /// <summary>
    /// A recovery-eligible operation is only abandoned after a fresh definitely-pre-mutation comparison, and the
    /// successful abandonment releases the account lock.
    /// </summary>
    /// <returns>A task completing after the refusal and success assertions.</returns>
    /// <remarks>
    /// This is the core exactly-once guarantee for abandonment: the panel must prove nothing was changed, and only then
    /// does the customer get the lock back.
    /// </remarks>
    [Fact]
    public async Task Recovery_eligible_abandon_requires_a_fresh_pre_mutation_comparison()
    {
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        var configuration = ManualReviewConfiguration(panel);
        // The panel already holds the target, which is an Applied state and therefore forbids abandonment.
        await SeedPanelClientAsync(panel, configuration, ManualReviewTargetTotalBytes(), ManualReviewTargetExpiryMs());
        var (store, service, _) = ManualReviewService(databases, configuration);
        var operationId = await SeedPanelManualReviewAsync(databases, recoveryEligible: true, holdLock: true);

        var refused = await service.AbandonAsNotAppliedAsync(operationId, ManualReviewAdminId, legacyOverrideConfirmed: true);

        Assert.Equal(XuiV3RenewalManualReviewService.ManualReviewOutcome.ComparisonNotPreMutation, refused.Outcome);
        Assert.Equal(XuiV3RenewalOperationStatuses.ManualReview, (await store.GetByIdAsync(operationId)).Status);

        // Now the panel is restored to the exact pre-mutation state, so the abandonment is proven safe.
        await SeedPanelClientAsync(panel, configuration, ManualReviewPreTotalBytes(), ManualReviewPreExpiryMs());
        var abandoned = await service.AbandonAsNotAppliedAsync(operationId, ManualReviewAdminId, legacyOverrideConfirmed: false);

        Assert.Equal(XuiV3RenewalManualReviewService.ManualReviewOutcome.Abandoned, abandoned.Outcome);
        var failed = await store.GetByIdAsync(operationId);
        Assert.Equal(XuiV3RenewalOperationStatuses.Failed, failed.Status);
        Assert.Null(failed.AccountLockKey);
        Assert.Null(failed.NextReconcileAtUtc);
        Assert.Null(failed.RecoveryClaimToken);
        Assert.Null(failed.RecoveryLeaseUntilUtc);
        Assert.Equal(XuiV3RenewalManualReviewResolutions.AbandonedNotApplied, failed.ManualReviewResolution);
        Assert.Equal(ManualReviewAdminId, failed.ManualReviewResolvedByTelegramUserId);
        // The audit row is retained; only the lock is released.
        Assert.NotNull(failed.OperationId);

        // A failed row must no longer block a new renewal for the same account.
        Assert.Null(await store.FindBlockingOperationAsync(failed.TargetUuid, failed.TargetEmail));
    }

    /// <summary>
    /// A historical recovery-ineligible row can only be released by an explicit super-admin override.
    /// </summary>
    /// <returns>A task completing after the override assertions.</returns>
    /// <remarks>
    /// No read-only comparison can classify a row written before the pre-mutation snapshot existed, so the operator must
    /// accept that uncertainty deliberately, and that acceptance is persisted as the resolution category.
    /// </remarks>
    [Fact]
    public async Task Legacy_abandon_requires_an_explicit_super_admin_override()
    {
        using var databases = new Databases();
        var configuration = ManualReviewConfiguration(null);
        var (store, service, _) = ManualReviewService(databases, configuration);
        var operationId = await SeedManualReviewAsync(databases, "legacy-override", recoveryEligible: false, holdLock: true);

        var refused = await service.AbandonAsNotAppliedAsync(operationId, ManualReviewAdminId, legacyOverrideConfirmed: false);

        Assert.Equal(XuiV3RenewalManualReviewService.ManualReviewOutcome.LegacyOverrideRequired, refused.Outcome);
        Assert.Equal(XuiV3RenewalOperationStatuses.ManualReview, (await store.GetByIdAsync(operationId)).Status);

        var abandoned = await service.AbandonAsNotAppliedAsync(operationId, ManualReviewAdminId, legacyOverrideConfirmed: true);

        Assert.Equal(XuiV3RenewalManualReviewService.ManualReviewOutcome.Abandoned, abandoned.Outcome);
        var failed = await store.GetByIdAsync(operationId);
        Assert.Equal(XuiV3RenewalOperationStatuses.Failed, failed.Status);
        Assert.Null(failed.AccountLockKey);
        Assert.Equal(XuiV3RenewalManualReviewResolutions.AbandonedNotAppliedLegacyOverride, failed.ManualReviewResolution);
        Assert.Equal(ManualReviewAdminId, failed.ManualReviewResolvedByTelegramUserId);
        Assert.NotNull(failed.ManualReviewResolvedAtUtc);
    }

    /// <summary>
    /// Abandonment is refused while settlement is not pending, because a wallet debit may already exist.
    /// </summary>
    /// <returns>A task completing after the refusal assertions.</returns>
    /// <remarks>
    /// A parked settlement means the previous executor may have debited the wallet before crashing. Unlocking in that
    /// state is the exact double-charge scenario the settlement guard exists to prevent.
    /// </remarks>
    [Fact]
    public async Task Abandon_is_refused_when_settlement_is_not_pending()
    {
        using var databases = new Databases();
        var configuration = ManualReviewConfiguration(null);
        var (store, service, _) = ManualReviewService(databases, configuration);
        var operationId = await SeedManualReviewAsync(databases, "settling", recoveryEligible: true, holdLock: true,
            settlementStatus: XuiV3RenewalSettlementStatuses.ManualReview);

        var result = await service.AbandonAsNotAppliedAsync(operationId, ManualReviewAdminId, legacyOverrideConfirmed: true);

        Assert.Equal(XuiV3RenewalManualReviewService.ManualReviewOutcome.SettlementNotPending, result.Outcome);
        var unchanged = await store.GetByIdAsync(operationId);
        Assert.Equal(XuiV3RenewalOperationStatuses.ManualReview, unchanged.Status);
        Assert.Equal(XuiV3RenewalSettlementStatuses.ManualReview, unchanged.SettlementStatus);
        Assert.NotNull(unchanged.AccountLockKey);
    }

    /// <summary>
    /// The administrator screen refuses a non-super-admin actor and requires a second confirmation to abandon.
    /// </summary>
    /// <returns>A task completing after the authorization and double-confirmation assertions.</returns>
    /// <remarks>
    /// Callback data is client-supplied, so authorization is rechecked inside the handler rather than trusted from the
    /// dispatch point, and the destructive action cannot complete from a single press.
    /// </remarks>
    [Fact]
    public async Task Administrator_screen_refuses_non_admins_and_double_confirms_abandonment()
    {
        using var databases = new Databases();
        var configuration = ManualReviewConfiguration(null);
        var (store, service, _) = ManualReviewService(databases, configuration);
        var operationId = await SeedManualReviewAsync(databases, "admin-screen", recoveryEligible: false, holdLock: true);
        var client = new ManualReviewTelegramProbe();
        var adminService = new XuiV3RenewalManualReviewAdminService(
            service,
            TelegramInteractionTimeouts.Production,
            configuration,
            NullLogger<XuiV3RenewalManualReviewAdminService>.Instance);

        // A non-admin actor is refused and changes nothing.
        var intruder = ManualReviewCallback("x3mr:a:" + operationId, actor: 4242);
        Assert.True(await adminService.TryHandleCallbackAsync(client, intruder, CancellationToken.None));
        Assert.Equal(XuiV3RenewalOperationStatuses.ManualReview, (await store.GetByIdAsync(operationId)).Status);
        Assert.Contains("اجازه", client.LastAnswerText ?? string.Empty);

        // The first abandonment press only renders the second confirmation, and the submitted payload is the override one.
        var firstPress = ManualReviewCallback("x3mr:a:" + operationId, ManualReviewAdminId);
        Assert.True(await adminService.TryHandleCallbackAsync(client, firstPress, CancellationToken.None));
        Assert.Equal(XuiV3RenewalOperationStatuses.ManualReview, (await store.GetByIdAsync(operationId)).Status);
        Assert.Contains("x3mr:a3:" + operationId, client.LastEditKeyboardData);

        // Only the second, explicitly confirmed press releases the lock.
        var secondPress = ManualReviewCallback("x3mr:a3:" + operationId, ManualReviewAdminId);
        Assert.True(await adminService.TryHandleCallbackAsync(client, secondPress, CancellationToken.None));
        var failed = await store.GetByIdAsync(operationId);
        Assert.Equal(XuiV3RenewalOperationStatuses.Failed, failed.Status);
        Assert.Null(failed.AccountLockKey);
    }

    /// <summary>
    /// The pending-review screen can be rendered for a super-admin without any account or panel identity.
    /// </summary>
    /// <returns>A task completing after the rendered-content assertions.</returns>
    /// <remarks>
    /// The rendered text is checked for absence of the account email, UUID, operation id string, and stored error text,
    /// because the screen is delivered into a chat.
    /// </remarks>
    [Fact]
    public async Task Administrator_screen_renders_no_account_or_panel_secrets()
    {
        using var databases = new Databases();
        var configuration = ManualReviewConfiguration(null);
        var (store, service, _) = ManualReviewService(databases, configuration);
        var operationId = await SeedManualReviewAsync(databases, "render", recoveryEligible: true, holdLock: true);
        var operation = await store.GetByIdAsync(operationId);
        var client = new ManualReviewTelegramProbe();
        var adminService = new XuiV3RenewalManualReviewAdminService(
            service,
            TelegramInteractionTimeouts.Production,
            configuration,
            NullLogger<XuiV3RenewalManualReviewAdminService>.Instance);

        await adminService.ShowPendingListAsync(client, 900700, CancellationToken.None);

        var text = Assert.Single(client.Sends);
        Assert.Contains("#" + operationId, text);
        Assert.DoesNotContain(operation.TargetEmail, text);
        Assert.DoesNotContain(operation.OperationId, text);
        Assert.DoesNotContain("internal-panel-error-text", text);
        // No email-shaped value of any kind may appear on the operator screen.
        Assert.DoesNotContain("@", text);
        Assert.Contains("x3mr:a:" + operationId, client.LastSentKeyboardData);
    }

    /// <summary>
    /// No manual-review code path can replay the panel mutation.
    /// </summary>
    /// <returns>A task completing after the source-level assertions.</returns>
    /// <remarks>
    /// This is a structural guard on the newest code: an ambiguous or manual-review operation must be resolved by reads
    /// only, so any future edit that introduces a client update into these files fails here before it can ship.
    /// </remarks>
    [Fact]
    public void Manual_review_code_never_replays_the_panel_mutation()
    {
        var forbidden = new[]
        {
            "UpdateClientAsync", "ResetClientTrafficAsync", "UpdateClientTrafficAsync", "AddClientAsync",
            "DeleteClientAsync", "ApiServicev3.Update"
        };
        foreach (var file in new[]
        {
            "Services/XuiV3RenewalManualReviewService.cs",
            "Services/XuiV3RenewalManualReviewAdminService.cs",
            "Services/XuiV3RenewalAppliedSettlementRouter.cs",
            "Domain/XuiV3RenewalBlockingNotice.cs"
        })
        {
            var source = ReadRepositoryFile(file);
            foreach (var token in forbidden)
                Assert.DoesNotContain(token, source);
        }

        // Exactly one settlement entry point exists for an administrator confirmation.
        var manualReviewSource = ReadRepositoryFile("Services/XuiV3RenewalManualReviewService.cs");
        Assert.Equal(1, CountOccurrences(manualReviewSource, "_settlementRouter.SettleAppliedAsync("));
    }

    /// <summary>
    /// The bounded automatic reconciliation limits are unchanged by the manual-review feature.
    /// </summary>
    /// <returns>A task completing after the limit assertions.</returns>
    /// <remarks>
    /// Loosening these would hide stuck states instead of resolving them, so the values are pinned deliberately.
    /// </remarks>
    [Fact]
    public void Automatic_reconciliation_limits_are_unchanged()
    {
        Assert.Equal(12, XuiV3RenewalOperationStore.MaximumAutomaticReconcileAttempts);
        Assert.Equal(TimeSpan.FromHours(24), XuiV3RenewalOperationStore.MaximumAutomaticReconcileAge);
        Assert.Equal(TimeSpan.FromMinutes(5), XuiV3RenewalOperationStore.ClaimLease);
    }

    /// <summary>Builds the durable operation store over one isolated users.db fixture.</summary>
    /// <param name="databases">Temporary database fixture.</param>
    /// <returns>A store using a null logger, so reconciliation diagnostics stay out of the test output.</returns>
    private static XuiV3RenewalOperationStore ManualReviewStore(Databases databases) =>
        new(databases.Users, NullLogger<XuiV3RenewalOperationStore>.Instance);

    /// <summary>Builds the manual-review service, its store, and a counting settlement router.</summary>
    /// <param name="databases">Temporary database fixture.</param>
    /// <param name="configuration">Runtime configuration whose XUI base URL may point at a fake panel.</param>
    /// <returns>The store, the service, and the settlement router that records settlement entries.</returns>
    private static (XuiV3RenewalOperationStore Store, XuiV3RenewalManualReviewService Service, CountingSettlementRouter Router)
        ManualReviewService(Databases databases, IConfiguration configuration)
    {
        var store = ManualReviewStore(databases);
        var router = new CountingSettlementRouter();
        var credentials = new CredentialsStore(databases.Credentials);
        var service = new XuiV3RenewalManualReviewService(
            store,
            router,
            credentials,
            new WalletLedgerService(databases.Users, credentials),
            configuration,
            NullLogger<XuiV3RenewalManualReviewService>.Instance);
        return (store, service, router);
    }

    /// <summary>Builds in-memory configuration that optionally points the XUI client at a fake panel.</summary>
    /// <param name="panel">Started fake panel, or null when the test performs no panel call.</param>
    /// <returns>Configuration with retries disabled and no live credentials.</returns>
    private static IConfiguration ManualReviewConfiguration(FakeUpstreamPanel panel)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["XuiV3ApiBaseUrl"] = panel?.Url ?? "http://127.0.0.1:1",
            ["XuiV3ApiToken"] = "test-only",
            ["xuiV3TransientRetryCount"] = "0",
            ["xuiV3RequestTimeoutSeconds"] = "5",
            ["AdminsUserIds:0"] = ManualReviewAdminId.ToString()
        }).Build();

    /// <summary>Seeds one manual-review operation row directly, without any panel interaction.</summary>
    /// <param name="databases">Temporary database fixture.</param>
    /// <param name="suffix">Suffix making the operation key unique inside the fixture.</param>
    /// <param name="recoveryEligible">Whether the row carries the post-migration recovery evidence flag.</param>
    /// <param name="holdLock">Whether the row still holds an active account lock.</param>
    /// <param name="resolvedAtUtc">Optional resolution timestamp marking the row as already decided.</param>
    /// <param name="settlementStatus">Settlement status to persist; defaults to a fresh pending settlement.</param>
    /// <returns>The internal users.db key of the seeded row.</returns>
    /// <remarks>
    /// Seeding writes the row directly because these tests target transitions, not creation. The identity columns are
    /// derived from the suffix so several rows can coexist in one fixture without sharing an account lock.
    /// </remarks>
    private static async Task<int> SeedManualReviewAsync(
        Databases databases,
        string suffix,
        bool recoveryEligible,
        bool holdLock,
        DateTime? resolvedAtUtc = null,
        string settlementStatus = XuiV3RenewalSettlementStatuses.Pending)
    {
        var email = "review-" + suffix + "@example.test";
        var row = new XuiV3RenewalOperation
        {
            OperationKey = "review-" + suffix,
            OperationId = "renew-review-" + suffix,
            BotId = "vpnetiranbot",
            TelegramUserId = ManualReviewPayerId,
            TargetEmail = email,
            TargetUuid = string.Empty,
            NormalizedTargetEmail = email,
            NormalizedTargetUuid = string.Empty,
            AccountLockKey = holdLock ? "email:" + email : null,
            RecoveryEligible = recoveryEligible,
            ServiceKey = "normal",
            AddedTrafficGb = 50,
            AddedTrafficBytes = 50 * ManualReviewGib,
            AddedDurationDays = 30,
            PriceToman = 150000,
            PaymentMethod = "credit",
            Status = XuiV3RenewalOperationStatuses.ManualReview,
            SettlementStatus = settlementStatus,
            ReconcileAttemptCount = XuiV3RenewalOperationStore.MaximumAutomaticReconcileAttempts,
            ManualReviewAtUtc = DateTime.UtcNow.AddHours(-2),
            ManualReviewResolvedAtUtc = resolvedAtUtc,
            LastComparisonOutcome = "Drifted",
            LastMismatchSummary = "identity=target;quota=other;expiry=other",
            LastError = "internal-panel-error-text",
            CreatedAtUtc = DateTime.UtcNow.AddHours(-3),
            LeaseUntilUtc = DateTime.UtcNow.AddHours(-3),
            UpdatedAtUtc = DateTime.UtcNow.AddHours(-2)
        };

        await using var context = databases.Users.CreateDbContext();
        context.XuiV3RenewalOperations.Add(row);
        await context.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>Seeds a panel-backed manual-review operation carrying real pre-mutation and target evidence.</summary>
    /// <param name="databases">Temporary database fixture.</param>
    /// <param name="recoveryEligible">Whether the row is eligible for read-only comparison.</param>
    /// <param name="holdLock">Whether the row still holds an active account lock.</param>
    /// <returns>The internal users.db key of the seeded row.</returns>
    /// <remarks>
    /// The pre-mutation snapshot and the immutable target are produced the same way production does, so
    /// <c>CompareRenewalState</c> can genuinely return Applied or DefinitelyPreMutation for these rows.
    /// </remarks>
    private static async Task<int> SeedPanelManualReviewAsync(
        Databases databases,
        bool recoveryEligible,
        bool holdLock)
    {
        var preSnapshot = new XuiV3ClientPayload
        {
            Email = ManualReviewPanelEmail,
            Uuid = string.Empty,
            TotalGB = ManualReviewPreTotalBytes(),
            ExpiryTime = ManualReviewPreExpiryMs(),
            Enable = true
        };
        var target = new XuiV3ClientPayload
        {
            Email = ManualReviewPanelEmail,
            Uuid = string.Empty,
            TotalGB = ManualReviewTargetTotalBytes(),
            ExpiryTime = ManualReviewTargetExpiryMs(),
            Enable = true
        };

        var row = new XuiV3RenewalOperation
        {
            OperationKey = "review-panel",
            OperationId = "renew-review-panel",
            BotId = "vpnetiranbot",
            TelegramUserId = ManualReviewPayerId,
            TargetEmail = ManualReviewPanelEmail,
            TargetUuid = string.Empty,
            NormalizedTargetEmail = ManualReviewPanelEmail,
            NormalizedTargetUuid = string.Empty,
            AccountLockKey = holdLock ? "email:" + ManualReviewPanelEmail : null,
            RecoveryEligible = recoveryEligible,
            ServiceKey = "normal",
            AddedTrafficGb = 50,
            AddedTrafficBytes = 50 * ManualReviewGib,
            AddedDurationDays = 30,
            PriceToman = 150000,
            PaymentMethod = "credit",
            ExpectedTotalBytesBefore = ManualReviewPreTotalBytes(),
            ExpectedExpiryTimeBefore = ManualReviewPreExpiryMs(),
            TargetTotalBytes = ManualReviewTargetTotalBytes(),
            TargetExpiryTime = ManualReviewTargetExpiryMs(),
            MutationPayloadJson = JsonConvert.SerializeObject(target),
            PreMutationSnapshotJson = JsonConvert.SerializeObject(new
            {
                TotalBytes = preSnapshot.TotalGB,
                ExpiryTime = preSnapshot.ExpiryTime,
                Enable = preSnapshot.Enable,
                TgId = 0L,
                Comment = (string)null
            }),
            Status = XuiV3RenewalOperationStatuses.ManualReview,
            SettlementStatus = XuiV3RenewalSettlementStatuses.Pending,
            ReconcileAttemptCount = XuiV3RenewalOperationStore.MaximumAutomaticReconcileAttempts,
            ManualReviewAtUtc = DateTime.UtcNow.AddHours(-2),
            CreatedAtUtc = DateTime.UtcNow.AddHours(-3),
            LeaseUntilUtc = DateTime.UtcNow.AddHours(-3),
            UpdatedAtUtc = DateTime.UtcNow.AddHours(-2)
        };

        await using var context = databases.Users.CreateDbContext();
        context.XuiV3RenewalOperations.Add(row);
        await context.SaveChangesAsync();
        return row.Id;
    }

    /// <summary>Seeds one durable financial artifact for the abandonment refusal scenarios.</summary>
    /// <param name="databases">Temporary database fixture.</param>
    /// <param name="artifact">Artifact kind from the theory data.</param>
    /// <param name="ledgerKey">Settlement idempotency key of the operation.</param>
    /// <param name="operation">Operation the artifact belongs to.</param>
    /// <returns>A task completing after the artifact is committed.</returns>
    /// <remarks>
    /// The wallet receipt is created through the real credentials store so the seeded artifact matches the exact shape a
    /// crashed settlement leaves behind.
    /// </remarks>
    private static async Task SeedFinancialArtifactAsync(
        Databases databases,
        string artifact,
        string ledgerKey,
        XuiV3RenewalOperation operation)
    {
        if (artifact == "wallet-ledger")
        {
            await using var context = databases.Users.CreateDbContext();
            context.WalletLedgerEntries.Add(new WalletLedgerEntry
            {
                TelegramUserId = operation.TelegramUserId,
                Direction = WalletLedgerDirections.Debit,
                AmountToman = operation.PriceToman,
                BalanceBefore = 500000,
                BalanceAfter = 350000,
                Reason = WalletLedgerReasons.AccountRenew,
                Provider = "wallet",
                ReferenceType = "xui-v3-client",
                ReferenceId = operation.TargetEmail,
                IdempotencyKey = ledgerKey,
                CreatedAtUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
            return;
        }

        if (artifact == "wallet-receipt")
        {
            var credentials = new CredentialsStore(databases.Credentials);
            await credentials.AddEmptyUser(operation.TelegramUserId);
            await credentials.MutateWalletAsync(operation.TelegramUserId, -operation.PriceToman, ledgerKey);
            return;
        }

        await using var siteContext = databases.Users.CreateDbContext();
        siteContext.Add(new SiteWalletDebitOperation
        {
            Id = "site:" + operation.TelegramUserId + ":xui-v3-client:" + operation.OperationId,
            OwnerTelegramUserId = operation.TelegramUserId,
            AmountToman = operation.PriceToman,
            Status = "applied",
            BeforeBalance = 500000,
            AfterBalance = 350000,
            CreatedAtUtc = DateTime.UtcNow
        });
        await siteContext.SaveChangesAsync();
    }

    /// <summary>Total quota in bytes the panel carried before the renewal.</summary>
    /// <returns>A quota of thirty binary gigabytes.</returns>
    private static long ManualReviewPreTotalBytes() => 30 * ManualReviewGib;

    /// <summary>Total quota in bytes the renewal targets.</summary>
    /// <returns>A quota of eighty binary gigabytes.</returns>
    private static long ManualReviewTargetTotalBytes() => 80 * ManualReviewGib;

    /// <summary>
    /// Absolute expiry in panel milliseconds before the renewal.
    /// </summary>
    /// <remarks>
    /// The value is a fixed constant rather than a timestamp derived from the current clock. The comparison uses exact
    /// equality for pre-state evidence, so a value recomputed a few milliseconds later would look like drift and make
    /// the test flaky instead of testing the lifecycle.
    /// </remarks>
    private static readonly long ManualReviewPreExpiry = new DateTimeOffset(
        new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>Absolute expiry in panel milliseconds the renewal targets, later than the pre-state value.</summary>
    private static readonly long ManualReviewTargetExpiry = new DateTimeOffset(
        new DateTime(2027, 2, 1, 0, 0, 0, DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    /// <summary>Absolute expiry in panel milliseconds before the renewal.</summary>
    /// <returns>The fixed pre-mutation expiry timestamp.</returns>
    private static long ManualReviewPreExpiryMs() => ManualReviewPreExpiry;

    /// <summary>Absolute expiry in panel milliseconds the renewal targets.</summary>
    /// <returns>The fixed target expiry timestamp.</returns>
    private static long ManualReviewTargetExpiryMs() => ManualReviewTargetExpiry;

    /// <summary>Seeds one panel client through the panel's own update contract.</summary>
    /// <param name="panel">Started fake panel.</param>
    /// <param name="configuration">Configuration passed to the API layer.</param>
    /// <param name="totalBytes">Exact quota in bytes the client should carry.</param>
    /// <param name="expiryTimeMs">Absolute expiry in panel milliseconds.</param>
    /// <returns>A task completing after the seed write is accepted.</returns>
    /// <remarks>
    /// Seeding through the API contract keeps the fixture honest about the stored payload shape, including the metadata
    /// comment the comparison reads.
    /// </remarks>
    private static async Task SeedPanelClientAsync(
        FakeUpstreamPanel panel,
        IConfiguration configuration,
        long totalBytes,
        long expiryTimeMs)
    {
        panel.SeedClient(ManualReviewPanelEmail, up: 0, down: 0, enable: true);
        panel.SetClientIdentity(ManualReviewPanelEmail, "uuid-manual-review", "sub-manual-review");

        var payload = new XuiV3ClientPayload
        {
            Email = ManualReviewPanelEmail,
            Uuid = "uuid-manual-review",
            SubId = "sub-manual-review",
            TotalGB = totalBytes,
            ExpiryTime = expiryTimeMs,
            Enable = true
        };

        var response = await ApiServicev3.UpdateClientAsync(panel.ServerInfo, configuration, ManualReviewPanelEmail, payload);
        Assert.True(response.Success, response.Msg);
    }

    /// <summary>Builds one callback query for the manual-review administrator surface.</summary>
    /// <param name="data">Callback payload.</param>
    /// <param name="actor">Telegram user id issuing the callback.</param>
    /// <returns>A callback query targeting a private administrator chat.</returns>
    private static CallbackQuery ManualReviewCallback(string data, long actor) => new()
    {
        Id = "callback-" + data,
        Data = data,
        // Fully qualified because this class also imports the bot-scoped Adminbot.Domain.User state model.
        From = new Telegram.Bot.Types.User { Id = actor, FirstName = "operator" },
        Message = new Message { Id = 77, Chat = new Chat { Id = 900700 } }
    };

    /// <summary>Counts the exact occurrences of one token inside a source file.</summary>
    /// <param name="source">Full file content.</param>
    /// <param name="token">Token to count.</param>
    /// <returns>The number of non-overlapping occurrences.</returns>
    /// <remarks>
    /// The tracked-file reader is the shared <c>ReadRepositoryFile</c> helper already defined by the other partial
    /// parts of this class, so these guards resolve paths exactly like the existing source-level tests.
    /// </remarks>
    private static int CountOccurrences(string source, string token)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += token.Length;
        }

        return count;
    }

    /// <summary>Settlement router test seam that records how often settlement was entered.</summary>
    /// <remarks>
    /// It counts entries instead of building a live wallet graph, so the tests can prove that a repeated or concurrent
    /// administrator confirmation cannot reach settlement twice.
    /// </remarks>
    private sealed class CountingSettlementRouter : XuiV3RenewalAppliedSettlementRouter
    {
        /// <summary>Creates the seam without the production dependencies, which these tests never exercise.</summary>
        public CountingSettlementRouter()
            : base(null!, null!, null!, null!)
        {
        }

        /// <summary>Number of settlement entries observed by this seam.</summary>
        public int SettlementEntries { get; private set; }

        /// <summary>Records one settlement entry and reports the operation as settled.</summary>
        /// <param name="operation">Operation the service is settling.</param>
        /// <param name="cancellationToken">Unused cancellation token.</param>
        /// <returns><c>true</c>, meaning settlement completed for the confirmed operation.</returns>
        public override Task<bool> SettleAppliedAsync(XuiV3RenewalOperation operation, CancellationToken cancellationToken)
        {
            SettlementEntries++;
            return Task.FromResult(operation.Status == XuiV3RenewalOperationStatuses.Applied);
        }
    }

    /// <summary>Scripted Telegram transport that records sends, edits, and callback answers.</summary>
    /// <remarks>
    /// No test contacts real Telegram; every response is produced in process.
    /// </remarks>
    private sealed class ManualReviewTelegramProbe : ITelegramBotClient
    {
        /// <summary>HTML texts sent through the transport, in order.</summary>
        public List<string> Sends { get; } = new();

        /// <summary>Text of the most recent answered callback, or null when none was answered.</summary>
        public string? LastAnswerText { get; private set; }

        /// <summary>Callback payloads of the most recent inline keyboard, or null when none was rendered.</summary>
        public string? LastEditKeyboardData { get; private set; }

        /// <summary>Callback payloads of the most recent sent inline keyboard, or null when none was rendered.</summary>
        public string? LastSentKeyboardData { get; private set; }

        /// <inheritdoc />
        public bool LocalBotServer => false;
        /// <inheritdoc />
        public long BotId => 1;
        /// <inheritdoc />
        public TimeSpan Timeout { get; set; }
        /// <inheritdoc />
        public IExceptionParser ExceptionsParser { get; set; } = null!;
        /// <inheritdoc />
        public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest { add { } remove { } }
        /// <inheritdoc />
        public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived { add { } remove { } }
        /// <inheritdoc />
        public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);
        /// <inheritdoc />
        public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        /// <inheritdoc />
        public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        /// <summary>Records one outbound request and answers it with a synthetic response.</summary>
        /// <typeparam name="TResponse">Requested response type.</typeparam>
        /// <param name="request">Request produced by the service under test.</param>
        /// <param name="cancellationToken">Caller cancellation.</param>
        /// <returns>A successful synthetic response.</returns>
        public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is SendMessageRequest send)
            {
                Sends.Add(send.Text ?? string.Empty);
                LastSentKeyboardData = Flatten(send.ReplyMarkup as InlineKeyboardMarkup);
            }
            else if (request is EditMessageTextRequest edit)
            {
                LastEditKeyboardData = Flatten(edit.ReplyMarkup as InlineKeyboardMarkup);
            }
            else if (request is AnswerCallbackQueryRequest answer)
            {
                LastAnswerText = answer.Text;
            }

            object result = typeof(TResponse) == typeof(bool)
                ? true
                : new Message { Id = 77, Chat = new Chat { Id = 900700 } };
            return Task.FromResult((TResponse)result);
        }

        /// <summary>Joins every callback payload of one inline keyboard.</summary>
        /// <param name="markup">Inline keyboard that may be null.</param>
        /// <returns>A single searchable string, or null when no keyboard was supplied.</returns>
        private static string? Flatten(InlineKeyboardMarkup? markup) =>
            markup == null
                ? null
                : string.Join(",", markup.InlineKeyboard.SelectMany(row => row).Select(button => button.CallbackData));
    }
}
