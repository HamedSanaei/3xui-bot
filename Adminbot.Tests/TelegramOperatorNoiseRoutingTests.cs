using System.Reflection;
using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Adminbot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

/// <summary>
/// Regression coverage for the operator-channel noise policy and the mandatory-join membership cache.
/// </summary>
/// <remarks>
/// <para>
/// The private Telegram logger channel is an actionable incident stream. Production evidence showed it carrying four
/// success/telemetry families that are not incidents (<c>Tenant fulfillment timing.</c>, a <c>delivered</c> post-commit
/// notification, <c>Pruned expired missing XUI volume reminder state.</c>, and <c>XUI v3 renewal applied exactly
/// once.</c>), every callback-acknowledgement timeout, every isolated mandatory-join timeout, and every repeated
/// foreground-budget or long-handler warning.
/// </para>
/// <para>
/// These tests never contact Telegram, XUI, Gozargah, or a payment provider. Every client is an in-process fake, every
/// scheduled threshold is a millisecond value, and the rate-limited families are evaluated through the deterministic
/// timestamp overload so nothing waits for real windows to elapse.
/// </para>
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>
    /// Successful telemetry and routine bookkeeping families are withheld from the operator channel.
    /// </summary>
    /// <remarks>
    /// The message shapes are copied verbatim from the production log review so a future change to a worker's rendering
    /// fails this contract instead of silently reopening the channel flood. Each family stays fully visible in the daily
    /// diagnostic file, the console/structured logger, and the metrics instruments.
    /// </remarks>
    [Fact]
    public void Successful_fulfillment_renewal_and_housekeeping_telemetry_stays_out_of_the_operator_channel()
    {
        Assert.True(TelegramLogSuppression.ShouldSuppress(
            "Tenant fulfillment timing. orderId=ORD-1 panelApiMs=812 coreFulfillmentMs=2450 notificationQueueMs=37 callbackTotalMs=3390", null));
        Assert.True(TelegramLogSuppression.ShouldSuppress(
            "Tenant fulfillment post-commit notification completed. orderId=ORD-1 kind=owner_sale_notification elapsedMs=310 outcome=delivered", null));
        Assert.True(TelegramLogSuppression.ShouldSuppress(
            "Pruned expired missing XUI volume reminder state. deleted=4 retentionDays=30 batchLimit=500", null));
        Assert.True(TelegramLogSuppression.ShouldSuppress(
            "XUI v3 renewal applied exactly once. renewalOperationId=op-1, accountEmail=customer@example.test, targetTotalBytes=107374182400, targetExpiryTime=1", null));
    }

    /// <summary>
    /// A tenant post-commit notification is telemetry only when the message proves the customer was reached.
    /// </summary>
    /// <param name="outcome">Outcome value rendered by the real durable notification worker.</param>
    /// <remarks>
    /// The <c>delivered</c> outcome is the only success. Every other outcome means the worker had to defer, retry,
    /// escalate to manual review, or could not prove delivery, so it must stay visible to operators.
    /// </remarks>
    [Theory]
    [InlineData("deferred")]
    [InlineData("delivery_uncertain")]
    [InlineData("failed")]
    [InlineData("manual_review")]
    [InlineData("pre_send_route_unavailable")]
    public void Undelivered_tenant_notification_outcomes_remain_operator_visible(string outcome)
        => Assert.False(TelegramLogSuppression.ShouldSuppress(
            $"Tenant fulfillment post-commit notification completed. orderId=ORD-1 kind=owner_sale_notification elapsedMs=310 outcome={outcome}", null));

    /// <summary>
    /// A post-commit notification without a readable outcome is never treated as a success.
    /// </summary>
    /// <remarks>
    /// Channel suppression must fail open: a missing outcome is unreadable evidence, so the entry is delivered instead
    /// of being hidden by an accidental match.
    /// </remarks>
    [Fact]
    public void Post_commit_notification_without_a_readable_outcome_fails_open()
    {
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Tenant fulfillment post-commit notification completed. orderId=ORD-1 kind=owner_sale_notification elapsedMs=310", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Tenant fulfillment notification outcome became uncertain. orderId=ORD-1 kind=owner_sale_notification ErrorType=TimeoutException", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Tenant fulfillment notification delivery entered ManualReview after exhausting attempts. orderId=ORD-1", null));
    }

    /// <summary>
    /// Every callback-acknowledgement timeout stays local while real acknowledgement failures remain visible.
    /// </summary>
    /// <remarks>
    /// A callback acknowledgement is a pure UX operation whose only cost is a spinner on the tapped button, and the
    /// business handler always continues. Transport, HTTP, and Telegram API failures of the same guard are real
    /// signals and must never be hidden.
    /// </remarks>
    [Fact]
    public void Callback_acknowledgement_timeouts_are_withheld_while_real_failures_remain_visible()
    {
        static string Message(string outcome, string errorType) =>
            "Slow Telegram operation. BotId=noise-ack TelegramUserId=5 Operation=telegram_callback_ack " +
            $"ElapsedMs=2001 Outcome={outcome} ErrorType={errorType}";

        // UX-only noise: withheld however often it repeats.
        Assert.True(TelegramLogSuppression.ShouldSuppress(Message("local_timeout", "OperationCanceledException"), null));
        Assert.True(TelegramLogSuppression.ShouldSuppress(Message("local_timeout", "OperationCanceledException"), null));

        // Real acknowledgement failures are never suppressed.
        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("telegram_transport_error", "RequestException"), null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("telegram_http_error", "HttpRequestException"), null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("telegram_timeout", "TimeoutException"), null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("telegram_api_403", "ApiRequestException"), null));
    }

    /// <summary>
    /// Real mandatory-join probe failures remain visible for both the owned and the storefront guard.
    /// </summary>
    /// <remarks>
    /// Only the controlled <c>local_timeout</c> outcome is routed away from the channel. A transport failure, a
    /// Telegram API rejection, and an inaccessible channel are the signals that tell an operator the bot must be added
    /// to the channel or that Telegram itself is degraded.
    /// </remarks>
    [Fact]
    public void Real_mandatory_join_probe_failures_remain_operator_visible()
    {
        static string Message(string outcome, string errorType) =>
            "Slow Telegram operation. BotId=noise-join TelegramUserId=5 Operation=telegram_mandatory_join " +
            $"ElapsedMs=5001 Outcome={outcome} ErrorType={errorType}";

        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("transport_error", "RequestException"), null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("telegram_api_error", "ApiRequestException"), null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("channel_access_error", "ApiRequestException"), null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("telegram_timeout", "TimeoutException"), null));

        // A finished guard is a success, not an incident.
        Assert.True(TelegramLogSuppression.ShouldSuppress(Message("completed", ""), null));
    }

    /// <summary>
    /// Foreground-budget notifications are rate-limited per bot and per closed-vocabulary request kind.
    /// </summary>
    /// <remarks>
    /// One customer interaction whose reply was abandoned is worth one bounded incident. Repeats of the same bot and
    /// request kind describe the same unresolved condition and are withheld until the window elapses, while a different
    /// request kind or a different bot still reports. The ambiguous send itself is never retried.
    /// </remarks>
    [Fact]
    public void Foreground_budget_warnings_are_rate_limited_per_bot_and_request_kind()
    {
        static string Message(string botId, string requestKind) =>
            "Telegram foreground delivery exceeded its interactive budget; the update was released without resending. " +
            $"botId={botId}, userId=5, requestKind={requestKind}, budgetSeconds=8";

        var windowMilliseconds = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("noise-fg-a", "SendMessage"), null, 1_000));
        Assert.True(TelegramLogSuppression.ShouldSuppress(Message("noise-fg-a", "SendMessage"), null, 60_000));
        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("noise-fg-a", "EditMessageText"), null, 60_000));
        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("noise-fg-b", "SendMessage"), null, 60_000));
        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("noise-fg-a", "SendMessage"), null, 1_000 + windowMilliseconds));
    }

    /// <summary>
    /// Repeated live long-handler warnings are rate-limited per bot and per execution stage.
    /// </summary>
    /// <remarks>
    /// The live watchdog is the actionable root-handler alert, so its first occurrence must stay visible. A handler that
    /// is genuinely stuck in a different closed-vocabulary stage reports again, while the same stuck stage does not
    /// repeat once per watchdog interval. The stage value is an enumeration member name or <c>none</c>, never customer
    /// data.
    /// </remarks>
    [Fact]
    public void Long_handler_warnings_are_rate_limited_per_bot_and_stage()
    {
        static string Message(string botId, string stage) =>
            "Telegram update handler running unusually long. BotId=" + botId +
            " Sequence=1 UpdateId=1 UpdateType=Message HandlerElapsedMs=10000 Stage=" + stage +
            " ActiveHandlers=1 MaxConcurrency=16";

        var windowMilliseconds = (long)TimeSpan.FromMinutes(10).TotalMilliseconds;

        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("noise-lh-a", "TelegramMembership"), null, 1_000));
        Assert.True(TelegramLogSuppression.ShouldSuppress(Message("noise-lh-a", "TelegramMembership"), null, 2_000));
        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("noise-lh-a", "XuiRead"), null, 2_000));
        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("noise-lh-b", "TelegramMembership"), null, 2_000));
        Assert.False(TelegramLogSuppression.ShouldSuppress(Message("noise-lh-a", "TelegramMembership"), null, 1_000 + windowMilliseconds));
    }

    /// <summary>
    /// The live long-handler watchdog names the closed-vocabulary stage the handler is blocked in.
    /// </summary>
    /// <returns>A task completing after the real scheduler records the warning and the stage measurement.</returns>
    /// <remarks>
    /// This is the attribution the production incident lacked: a slow probe and a slow panel read were
    /// indistinguishable. The scheduler publishes the stage through <see cref="TelegramUpdateLatencyScope.CurrentStage"/>
    /// while a handler is inside an instrumented stage, and the watchdog reads it from the scope captured at throttling
    /// time because the timer callback does not inherit the handler's ambient scope. The test also proves no customer
    /// payload can leak through the stage field.
    /// </remarks>
    [Fact]
    public async Task Live_long_handler_warning_names_the_closed_vocabulary_stage()
    {
        using var databases = new Databases();
        var logs = new DiagnosticLogger<TelegramUpdateScheduler>();
        var executor = new Executor(async (item, token) =>
        {
            // The handler blocks inside one instrumented stage long enough for the live watchdog to fire while it runs.
            var scope = TelegramUpdateLatencyScope.Current!;
            using (scope.Measure(TelegramUpdateStage.TelegramMembership))
                await Task.Delay(TimeSpan.FromMilliseconds(1400), token);
        });
        using var scheduler = new TelegramUpdateScheduler(
            databases.Inbox,
            executor,
            new AppConfig { TelegramUpdateMaxConcurrency = 1, TelegramUpdateQueueCapacity = 4, TelegramUpdateShutdownDrainSeconds = 5 },
            logs)
        {
            InteractiveHandlerThreshold = TimeSpan.FromMilliseconds(10),
            LongHandlerWarningThreshold = TimeSpan.FromMilliseconds(200),
            SlowStageThreshold = TimeSpan.FromMilliseconds(10)
        };

        await scheduler.StartAsync(default);
        try
        {
            await scheduler.EnqueueAsync("owned-stage", Update(1, 8101), default);
            await Until(() => logs.Count(LogLevel.Warning, "handler running unusually long") == 1 &&
                              logs.Count(LogLevel.Information, "slow update stage") == 1);
        }
        finally { await scheduler.StopAsync(default); }

        var warning = logs.Messages(LogLevel.Warning).Single(message => message.Contains("running unusually long", StringComparison.Ordinal));
        Assert.Contains("Stage=TelegramMembership", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("@", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("t.me", warning, StringComparison.Ordinal);

        var slowStage = logs.Messages(LogLevel.Information).Single(message => message.Contains("slow update stage", StringComparison.Ordinal));
        Assert.Contains("Stage=TelegramMembership", slowStage, StringComparison.Ordinal);
    }

    /// <summary>
    /// Payment, XUI, manual-review, and delivery-uncertainty failures remain fully operator visible.
    /// </summary>
    /// <remarks>
    /// The channel cleanup only removes success telemetry and repeated copies of one condition. Financial and business
    /// failures, and the ambiguous-send outcome that must never be retried, keep their own messages.
    /// </remarks>
    [Fact]
    public void Financial_business_and_delivery_uncertainty_failures_are_never_suppressed()
    {
        Assert.False(TelegramLogSuppression.ShouldSuppress("Tenant payment settlement failed. orderId=ORD-9", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress("Tenant XUI creation failed. orderId=ORD-9", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress("Tenant settlement manual review required. orderId=ORD-9", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Tenant fulfillment notification outcome became uncertain. orderId=ORD-9 kind=customer_delivery ErrorType=TimeoutException", null));
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "ManualReview required for tenant card order 241", null));
        // A first foreground-budget event for a bot no other test exercises is delivered as one bounded incident and
        // never retried, which is the routing this test protects. The timestamp is explicit so the assertion cannot
        // depend on how long the rest of the suite has been running.
        Assert.False(TelegramLogSuppression.ShouldSuppress(
            "Telegram foreground delivery exceeded its interactive budget; the update was released without resending. " +
            "botId=noise-business-fg, userId=5, requestKind=SendMessage, budgetSeconds=8", null, 7_000));
    }

    /// <summary>
    /// The positive membership cache removes repeated Telegram probes inside its lifetime.
    /// </summary>
    /// <remarks>
    /// This is a direct unit test of the cache contract: one verified evaluation for an exact bot, customer, and channel
    /// set satisfies later checks, and channel reordering, casing, and duplication do not create a second entry.
    /// </remarks>
    [Fact]
    public void Positive_membership_cache_serves_repeated_checks_and_normalizes_the_channel_set()
    {
        var cache = new TelegramMandatoryJoinMembershipCache();
        var channels = new[] { "@alpha", "@beta" };
        cache.RememberPositive("owned-ttl", 9201, channels, 1_000);

        Assert.True(cache.TryGetPositive("owned-ttl", 9201, channels, 1_000));
        // Order, casing, and an extra duplicate entry still describe the same verified channel set.
        Assert.True(cache.TryGetPositive("owned-ttl", 9201, new[] { " @BETA ", "@ALPHA", "@alpha" }, 29_999));
        Assert.Equal(1, cache.Count);

        // A different channel set, customer, or bot is a different key and must not be answered from this entry.
        Assert.False(cache.TryGetPositive("owned-ttl", 9201, new[] { "@alpha" }, 1_001));
        Assert.False(cache.TryGetPositive("owned-ttl", 9202, channels, 1_001));
        Assert.False(cache.TryGetPositive("owned-other", 9201, channels, 1_001));
        // A blank bot id and a non-positive customer id can never match a stored entry.
        Assert.False(cache.TryGetPositive("  ", 9201, channels, 1_001));
        Assert.False(cache.TryGetPositive("owned-ttl", 0, channels, 1_001));

        // Expiry is exact, and a refresh restores service.
        var expired = 1_000 + (long)TelegramMandatoryJoinMembershipCache.PositiveLifetime.TotalMilliseconds;
        Assert.False(cache.TryGetPositive("owned-ttl", 9201, channels, expired));
        Assert.Equal(0, cache.Count);
        cache.RememberPositive("owned-ttl", 9201, channels, 100_000);
        Assert.True(cache.TryGetPositive("owned-ttl", 9201, channels, 100_001));
    }

    /// <summary>
    /// The owned mandatory-join gate reuses one verified membership and isolates bot, customer, and channel set.
    /// </summary>
    /// <remarks>
    /// A positive cache hit means the same Telegram check would succeed again right now, so the probe is skipped. Every
    /// other dimension of the key must still perform a real Telegram check, which is what keeps the gate fail-closed for
    /// a different bot, a different customer, or a changed channel configuration.
    /// </remarks>
    [Fact]
    public async Task Owned_mandatory_join_reuses_a_verified_membership_and_isolates_every_key_component()
    {
        using var databases = new Databases();
        var client = new MembershipProbeClient();
        var cache = new TelegramMandatoryJoinMembershipCache();
        var service = BuildBareTelegramService(databases, client, out var accessor, membershipCache: cache);
        var channels = new[] { "@alpha", "@beta" };

        using var context = accessor.Push(OwnedContext("owned-cache-a", client));

        Assert.True(await InvokeMandatoryJoinAsync(service, channels, 9001, CancellationToken.None));
        Assert.Equal(2, client.GetChatMemberCalls);

        // The second evaluation is satisfied from the positive cache: no Telegram call at all.
        Assert.True(await InvokeMandatoryJoinAsync(service, channels, 9001, CancellationToken.None));
        Assert.Equal(2, client.GetChatMemberCalls);

        // A different customer is a different key.
        Assert.True(await InvokeMandatoryJoinAsync(service, channels, 9002, CancellationToken.None));
        Assert.Equal(4, client.GetChatMemberCalls);

        // A changed channel set is a different key.
        Assert.True(await InvokeMandatoryJoinAsync(service, new[] { "@alpha" }, 9001, CancellationToken.None));
        Assert.Equal(5, client.GetChatMemberCalls);

        // The original two-channel set is still cached.
        Assert.True(await InvokeMandatoryJoinAsync(service, channels, 9001, CancellationToken.None));
        Assert.Equal(5, client.GetChatMemberCalls);

        // A different bot never shares the entry, even for the same customer and channels.
        using (accessor.Push(OwnedContext("owned-cache-b", client)))
        {
            Assert.True(await InvokeMandatoryJoinAsync(service, channels, 9001, CancellationToken.None));
            Assert.Equal(7, client.GetChatMemberCalls);
        }

        Assert.Equal(4, cache.Count);
    }

    /// <summary>
    /// Non-members and failures are never cached, so the gate keeps failing closed and re-probing.
    /// </summary>
    /// <remarks>
    /// Only a complete successful evaluation may be stored. A rejected or unverifiable outcome must be re-checked
    /// against Telegram every time, otherwise a stale negative would be invisible and an error could be mistaken for a
    /// verified membership.
    /// </remarks>
    [Fact]
    public async Task Membership_cache_never_stores_non_members_or_failures()
    {
        using var databases = new Databases();
        var cache = new TelegramMandatoryJoinMembershipCache();
        var channels = new[] { "@alpha" };

        var nonMemberClient = new MembershipProbeClient(ChatMemberStatus.Left);
        var nonMemberService = BuildBareTelegramService(databases, nonMemberClient, out var nonMemberAccessor, membershipCache: cache);
        using var nonMemberContext = nonMemberAccessor.Push(OwnedContext("owned-nonmember", nonMemberClient));

        Assert.False(await InvokeMandatoryJoinAsync(nonMemberService, channels, 9101, CancellationToken.None));
        Assert.False(await InvokeMandatoryJoinAsync(nonMemberService, channels, 9101, CancellationToken.None));
        Assert.Equal(2, nonMemberClient.GetChatMemberCalls);
        Assert.Equal(0, cache.Count);

        var failingClient = new FailingMembershipClient(new RequestException("membership transport failed"));
        var failingService = BuildBareTelegramService(databases, failingClient, out var failingAccessor, membershipCache: cache);
        using var failingContext = failingAccessor.Push(OwnedContext("owned-failure", failingClient));

        Assert.False(await InvokeMandatoryJoinAsync(failingService, channels, 9102, CancellationToken.None));
        Assert.False(await InvokeMandatoryJoinAsync(failingService, channels, 9102, CancellationToken.None));
        Assert.Equal(2, failingClient.GetChatMemberCalls);
        Assert.Equal(0, cache.Count);

        var kickedClient = new MembershipProbeClient(ChatMemberStatus.Kicked);
        var kickedService = BuildBareTelegramService(databases, kickedClient, out var kickedAccessor, membershipCache: cache);
        using var kickedContext = kickedAccessor.Push(OwnedContext("owned-kicked", kickedClient));

        Assert.False(await InvokeMandatoryJoinAsync(kickedService, channels, 9103, CancellationToken.None));
        Assert.Equal(0, cache.Count);
    }

    /// <summary>
    /// The tenant storefront gate shares the same positive cache and still re-checks whenever nothing is verified.
    /// </summary>
    /// <returns>A task completing after each storefront evaluation and the probe count are asserted.</returns>
    /// <remarks>
    /// The storefront key uses the persisted tenant identity, so a verified customer of one storefront can never be
    /// served to another. A configured channel change alters the channel component of the key and therefore invalidates
    /// previous entries without any write path, and a rejected customer is never cached, which is why the explicit join
    /// retry performs a real Telegram check.
    /// </remarks>
    [Fact]
    public async Task Tenant_storefront_gate_shares_the_positive_cache_and_invalidates_on_channel_changes()
    {
        using var databases = new Databases();
        var client = new MembershipProbeClient();
        var cache = new TelegramMandatoryJoinMembershipCache();
        var service = BuildTenantServiceWithCache(databases, client, cache);
        var store = TenantWithChannels("tenant-cache-store", "@alpha", "@beta");

        Assert.True(await InvokeTenantJoinAsync(service, client, 9301, store, isJoinRetry: false));
        Assert.Equal(2, client.GetChatMemberCalls);

        // A later storefront action is satisfied from the positive cache.
        Assert.True(await InvokeTenantJoinAsync(service, client, 9301, store, isJoinRetry: false));
        Assert.Equal(2, client.GetChatMemberCalls);

        // The explicit retry path also reuses only a verified entry, so no additional probe is needed here.
        Assert.True(await InvokeTenantJoinAsync(service, client, 9301, store, isJoinRetry: true));
        Assert.Equal(2, client.GetChatMemberCalls);

        // A different customer is a different key.
        Assert.True(await InvokeTenantJoinAsync(service, client, 9302, store, isJoinRetry: false));
        Assert.Equal(4, client.GetChatMemberCalls);

        // A tenant channel change invalidates the previous entry naturally.
        var rechanneled = TenantWithChannels("tenant-cache-store", "@alpha");
        Assert.True(await InvokeTenantJoinAsync(service, client, 9301, rechanneled, isJoinRetry: false));
        Assert.Equal(5, client.GetChatMemberCalls);
        Assert.Equal(3, cache.Count);

        // A rejected customer is never cached, so the retry re-checks against Telegram every time.
        var rejectedClient = new MembershipProbeClient(ChatMemberStatus.Left);
        var rejectedService = BuildTenantServiceWithCache(databases, rejectedClient, cache);
        Assert.False(await InvokeTenantJoinAsync(rejectedService, rejectedClient, 9303, store, isJoinRetry: false));
        Assert.False(await InvokeTenantJoinAsync(rejectedService, rejectedClient, 9303, store, isJoinRetry: true));
        Assert.Equal(2, rejectedClient.GetChatMemberCalls);
        Assert.Equal(3, cache.Count);
    }

    /// <summary>
    /// One shared cache instance never authorizes a different bot in another flow from one verification.
    /// </summary>
    /// <returns>A task completing after the cross-flow isolation assertions.</returns>
    /// <remarks>
    /// The two flows deliberately share one cache instance, because the two guards are the same concept. Isolation comes
    /// from the key, not from the instance: the owned verification of a customer must not satisfy the same customer on a
    /// storefront, and the reverse must also hold.
    /// </remarks>
    [Fact]
    public async Task One_shared_cache_never_authorizes_a_different_bot_from_another_flows_check()
    {
        using var databases = new Databases();
        var ownedClient = new MembershipProbeClient();
        var tenantClient = new MembershipProbeClient();
        var cache = new TelegramMandatoryJoinMembershipCache();
        var ownedService = BuildBareTelegramService(databases, ownedClient, out var accessor, membershipCache: cache);
        var tenantService = BuildTenantServiceWithCache(databases, tenantClient, cache);
        var store = TenantWithChannels("tenant-shared-b", "@alpha");

        using var context = accessor.Push(OwnedContext("owned-shared-a", ownedClient));

        Assert.True(await InvokeMandatoryJoinAsync(ownedService, new[] { "@alpha" }, 9401, CancellationToken.None));
        Assert.Equal(1, cache.Count);

        // The same customer on the storefront still needs a real Telegram check.
        Assert.True(await InvokeTenantJoinAsync(tenantService, tenantClient, 9401, store, isJoinRetry: false));
        Assert.Equal(1, tenantClient.GetChatMemberCalls);
        Assert.Equal(2, cache.Count);

        // The owned bot's entry was not overwritten by the storefront verification.
        Assert.True(await InvokeMandatoryJoinAsync(ownedService, new[] { "@alpha" }, 9401, CancellationToken.None));
        Assert.Equal(1, ownedClient.GetChatMemberCalls);
    }

    /// <summary>
    /// An ambiguous foreground send failure is propagated unchanged and never retried automatically.
    /// </summary>
    /// <returns>A task completing after the single-attempt assertion.</returns>
    /// <remarks>
    /// A transport failure may have reached Telegram, so replaying the interactive send could deliver the same reply
    /// twice. The decorator therefore reports the original exception identity after exactly one attempt and lets the
    /// customer press the button again, which produces a fresh update and a fresh decision.
    /// </remarks>
    [Fact]
    public async Task Ambiguous_foreground_send_failure_is_never_retried_automatically()
    {
        var inner = new AmbiguousSendClient();
        var bounded = new ForegroundBoundedTelegramBotClient(
            inner, new TelegramForegroundDeliveryPolicy { OverallBudget = TimeSpan.FromMilliseconds(80) });

        await Assert.ThrowsAsync<RequestException>(() => bounded.SendMessage(
            chatId: 7, text: "menu", cancellationToken: CancellationToken.None));

        Assert.Equal(1, inner.SendAttempts);
    }

    /// <summary>Builds the owned-bot runtime context used by the owned mandatory-join tests.</summary>
    /// <param name="botId">Exact runtime bot id the gate must key its cache entry on.</param>
    /// <param name="client">Fake Telegram client pushed with the context.</param>
    /// <returns>A ready-to-push owned-bot runtime context.</returns>
    private static BotRuntimeContext OwnedContext(string botId, ITelegramBotClient client) => new()
    {
        Config = new BotInstanceConfig { Id = botId, Type = BotInstanceTypes.Owned, Username = botId },
        Client = client
    };

    /// <summary>Builds one tenant storefront row with an enabled forced-join rule and a channel list.</summary>
    /// <param name="id">Internal tenant bot id persisted in users.db.</param>
    /// <param name="channels">Configured forced-join channel identifiers, in any order.</param>
    /// <returns>A detached tenant row suitable for the private forced-join gate.</returns>
    private static BotInstance TenantWithChannels(string id, params string[] channels) => new()
    {
        Id = id,
        Type = BotInstanceTypes.Tenant,
        Enabled = true,
        OwnerTelegramUserId = 6910211284,
        Username = id.Replace("tenant-", "tenant_"),
        BrandName = "cache tenant",
        TenantMandatoryJoinEnabled = true,
        TenantChannelIdsJson = Newtonsoft.Json.JsonConvert.SerializeObject(channels),
        CreatedAtUtc = DateTime.UtcNow
    };

    /// <summary>
    /// Builds a production <see cref="TenantBotService"/> whose forced-join gate shares an explicit membership cache.
    /// </summary>
    /// <param name="databases">Temporary database fixture backing the state and credentials stores.</param>
    /// <param name="client">Fake Telegram transport used to observe membership and prompt calls.</param>
    /// <param name="cache">Explicit positive-only cache the storefront gate must use.</param>
    /// <returns>A storefront service instance ready for the private forced-join gate under test.</returns>
    /// <remarks>
    /// The provisional card services are real instances over the temporary databases so the service is constructed
    /// exactly as production does. They stay inert because the provisional switch is off in this configuration, which
    /// keeps these tests about the forced-join gate rather than about courtesy delivery.
    /// </remarks>
    private static TenantBotService BuildTenantServiceWithCache(
        Databases databases,
        ITelegramBotClient client,
        ITelegramMandatoryJoinMembershipCache cache)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UserActivityLogEnabled"] = "false"
        }).Build();
        var accessor = new BotContextAccessor();
        var provisionalStore = new TenantCardProvisionalOperationStore(databases.Users);
        var provisionalProvisioning = new TenantCardProvisionalProvisioningService(
            databases.Users,
            new XuiV3PurchaseService(configuration, databases.Users),
            new XuiV3CreationOperationStore(databases.Users),
            configuration,
            NullLogger<TenantCardProvisionalProvisioningService>.Instance);
        var provisionalFinalization = new TenantCardProvisionalFinalizationService(
            provisionalStore, configuration, NullLogger<TenantCardProvisionalFinalizationService>.Instance);
        var provisionalRevocation = new TenantCardProvisionalRevocationService(
            provisionalStore, configuration, NullLogger<TenantCardProvisionalRevocationService>.Instance);
        return new TenantBotService(
            new UserWorkflowStore(databases.Users), new UserStateStore(databases.Users),
            new CredentialsStore(databases.Credentials), configuration,
            null!, null!, null!, null!, null!, null!, null!, null!,
            null!, null!,
            null!, null!, accessor, null!, null!, null!, null!, null!, null!, null!,
            NullLogger<TenantBotService>.Instance, null!, null!, null!,
            provisionalProvisioning, provisionalFinalization, provisionalRevocation,
            MandatoryJoinMembershipCache: cache);
    }

    /// <summary>Invokes the private tenant forced-join gate exactly as a storefront message or callback would.</summary>
    /// <param name="service">Storefront service under test.</param>
    /// <param name="client">Fake Telegram transport passed to the gate.</param>
    /// <param name="telegramUserId">Numeric Telegram user id of the customer being checked.</param>
    /// <param name="tenant">Tenant row that owns the forced-join configuration.</param>
    /// <param name="isJoinRetry">True mirrors the explicit join-retry callback path.</param>
    /// <returns>A task that completes with the gate decision.</returns>
    /// <remarks>
    /// The overload is resolved by exact parameter types because the service also exposes a message-shaped overload.
    /// </remarks>
    private static Task<bool> InvokeTenantJoinAsync(
        TenantBotService service,
        ITelegramBotClient client,
        long telegramUserId,
        BotInstance tenant,
        bool isJoinRetry)
    {
        var method = typeof(TenantBotService).GetMethod(
            "EnsureTenantCustomerJoinAsync",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(ITelegramBotClient), typeof(ChatId), typeof(long), typeof(BotInstance), typeof(CancellationToken), typeof(bool) },
            modifiers: null)!;
        return (Task<bool>)method.Invoke(
            service,
            new object[] { client, new ChatId(telegramUserId), telegramUserId, tenant, CancellationToken.None, isJoinRetry })!;
    }

    /// <summary>Fake Telegram transport that answers membership probes with one configurable status.</summary>
    /// <remarks>
    /// Used to prove that the shared cache eliminates repeated <c>GetChatMember</c> traffic only for verified
    /// memberships and never for a rejection.
    /// </remarks>
    private sealed class MembershipProbeClient : StorefrontClient
    {
        /// <summary>Status returned for every membership probe.</summary>
        private readonly ChatMemberStatus _status;

        /// <summary>Total membership probes observed.</summary>
        public int GetChatMemberCalls;

        /// <summary>Creates the probe client for one membership status.</summary>
        /// <param name="status">Status Telegram must appear to report for every probed channel.</param>
        public MembershipProbeClient(ChatMemberStatus status = ChatMemberStatus.Member) => _status = status;

        /// <inheritdoc />
        public override Task<TResponse> SendRequest<TResponse>(
            IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is GetChatMemberRequest)
            {
                Interlocked.Increment(ref GetChatMemberCalls);
                // Telegram exposes the status through the concrete subclass, so the fake only has to pick the shape that
                // matches the configured status.
                var member = _status switch
                {
                    ChatMemberStatus.Left => (ChatMember)new ChatMemberLeft { User = new Telegram.Bot.Types.User { Id = 1 } },
                    ChatMemberStatus.Kicked => new ChatMemberBanned { User = new Telegram.Bot.Types.User { Id = 1 } },
                    _ => new ChatMemberMember { User = new Telegram.Bot.Types.User { Id = 1 } }
                };
                return Task.FromResult((TResponse)(object)member);
            }

            return base.SendRequest(request, cancellationToken);
        }
    }

    /// <summary>Fake Telegram transport whose membership probes always fail with one exception.</summary>
    /// <remarks>
    /// Used to prove that a transport failure is never cached, so the gate keeps failing closed and re-probing.
    /// </remarks>
    private sealed class FailingMembershipClient : StorefrontClient
    {
        /// <summary>Exception raised by every membership probe.</summary>
        private readonly Exception _exception;

        /// <summary>Total membership probes observed.</summary>
        public int GetChatMemberCalls;

        /// <summary>Creates the failing probe client.</summary>
        /// <param name="exception">Exception Telegram must appear to raise for every probed channel.</param>
        public FailingMembershipClient(Exception exception) => _exception = exception;

        /// <inheritdoc />
        public override Task<TResponse> SendRequest<TResponse>(
            IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is GetChatMemberRequest)
            {
                Interlocked.Increment(ref GetChatMemberCalls);
                return Task.FromException<TResponse>(_exception);
            }

            return base.SendRequest(request, cancellationToken);
        }
    }

    /// <summary>Fake Telegram transport whose ordinary message send fails ambiguously.</summary>
    /// <remarks>
    /// Used to prove that the foreground decorator reports the original failure identity after exactly one attempt
    /// instead of replaying a send that Telegram may already have accepted.
    /// </remarks>
    private sealed class AmbiguousSendClient : StorefrontClient
    {
        /// <summary>Total message send attempts observed.</summary>
        public int SendAttempts;

        /// <inheritdoc />
        public override Task<TResponse> SendRequest<TResponse>(
            IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is SendMessageRequest)
            {
                Interlocked.Increment(ref SendAttempts);
                return Task.FromException<TResponse>(new RequestException("connection reset while sending"));
            }

            return base.SendRequest(request, cancellationToken);
        }
    }
}
