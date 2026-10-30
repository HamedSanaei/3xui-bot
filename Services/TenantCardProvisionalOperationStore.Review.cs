using System.Security.Cryptography;
using System.Text;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;

namespace Adminbot.Services;

/// <summary>Conditional administrator-reviewed recovery transitions; never performs panel or financial work.</summary>
public sealed partial class TenantCardProvisionalOperationStore
{
    /// <summary>Commits an audit/second-confirmation row only while its reviewed database snapshot still exists.</summary>
    /// <param name="review">Actor/bot/chat-bound proof prepared by the authorized read-only review service; contains hashes, not credentials.</param>
    /// <param name="token">Short users.db transaction cancellation.</param>
    /// <returns>The new detached review with its numeric id, or null if any order/saga/receipt version changed.</returns>
    /// <remarks>No saga transition, panel request, balance change or payment approval occurs here.</remarks>
    /// <example><code>var saved = await store.CreateReviewedConfirmationAsync(actorBoundReview, token);</code></example>
    public Task<TenantCardProvisionalReview> CreateReviewedConfirmationAsync(TenantCardProvisionalReview review, CancellationToken token)
        => SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var order = await db.TenantBotOrders.SingleOrDefaultAsync(x => x.Id == review.TenantBotOrderId, ct);
            var operation = await db.TenantCardProvisionalOperations.SingleOrDefaultAsync(x => x.OperationKey == review.OperationKey, ct);
            var receipt = order?.ManualReceiptId is int receiptId
                ? await db.TenantManualPaymentReceipts.SingleOrDefaultAsync(x => x.Id == receiptId, ct) : null;
            if (order == null || operation == null || receipt == null || order.IsFulfilled ||
                ReviewBinding.Snapshot(order, operation, receipt, review.ExpectedInboundIdsJson) != review.SnapshotHash) return null;
            db.TenantCardProvisionalReviews.Add(review);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return review;
        }, token);

    /// <summary>Consumes one fresh second confirmation and atomically opens only a proven-safe recovery stage.</summary>
    /// <param name="reviewId">Internal review id, never an account identifier.</param>
    /// <param name="actor">Authorized global administrator's Telegram user id; must match the prepared row.</param>
    /// <param name="botId">Current owned runtime bot id, bound at preparation.</param>
    /// <param name="chatId">Current private Telegram chat id, bound at preparation.</param>
    /// <param name="bindingHash">Fresh GET-validated immutable order/identity/entitlement digest.</param>
    /// <param name="snapshotHash">Freshly observed database snapshot digest; must equal the prepared confirmation.</param>
    /// <param name="resumeStep">Only claimed after pre-mutation proof, or proven after a complete read-only final-target proof.</param>
    /// <param name="evidence">Sanitized booleans/quota/expiry evidence from the fresh probe, never a raw response.</param>
    /// <param name="token">Short users.db transaction cancellation.</param>
    /// <returns>True only for the single consumed authorization; stale, reused and concurrent confirmations return false.</returns>
    /// <remarks>Caller MUST hold the central tenant fulfillment order gate and re-probe live panel/payment state first.
    /// The prior error is retained on the audit row; frozen quota/expiry are never rewritten. No lease expiry or restart
    /// re-arms a consumed approval. Partial/ambiguous mutations can only enter proven after complete GET proof.</remarks>
    /// <remarks>All linked receipt versions change atomically to fence older approval/rejection workflows,
    /// including a pending alternative receipt. Status, amount and approval/confirmation timestamps never change here.</remarks>
    /// <example><code>var authorized = await store.TryAuthorizeReviewedRecoveryAsync(id, actor, botId, chatId, binding, snapshot, step, evidence, token);</code></example>
    public Task<bool> TryAuthorizeReviewedRecoveryAsync(long reviewId, long actor, string botId, long chatId,
        string bindingHash, string snapshotHash, string resumeStep, string evidence, CancellationToken token)
        => SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var review = await db.TenantCardProvisionalReviews.SingleOrDefaultAsync(x => x.Id == reviewId, ct);
            if (review == null || review.Status != "reviewed" || review.ActorTelegramUserId != actor ||
                review.BotId != botId || review.ChatId != chatId || review.ExpiresAtUtc <= DateTime.UtcNow ||
                review.BindingHash != bindingHash || review.SnapshotHash != snapshotHash || review.ResumeStep != resumeStep) return false;
            var order = await db.TenantBotOrders.SingleOrDefaultAsync(x => x.Id == review.TenantBotOrderId, ct);
            var operation = await db.TenantCardProvisionalOperations.SingleOrDefaultAsync(x => x.OperationKey == review.OperationKey, ct);
            var receipt = order?.ManualReceiptId is int receiptId
                ? await db.TenantManualPaymentReceipts.SingleOrDefaultAsync(x => x.Id == receiptId, ct) : null;
            if (order == null || operation == null || receipt == null || order.IsFulfilled ||
                operation.Kind != TenantCardProvisionalOperationKinds.Finalize || operation.TenantBotOrderId != order.Id ||
                operation.OrderId != order.OrderId || operation.OperationKey != $"tenant-card-finalize:{order.OrderId}" ||
                ReviewBinding.Snapshot(order, operation, receipt, review.ExpectedInboundIdsJson) != snapshotHash ||
                order.PaymentProvider != "tenant_card" || order.OrderKind != TenantBotOrderKinds.Purchase ||
                receipt.Status != TenantManualPaymentReceiptStatuses.Approved || receipt.FinalConfirmedAtUtc == null ||
                receipt.ApprovedAtUtc == null || receipt.AmountToman != order.SalePriceToman ||
                receipt.OrderId != order.OrderId || receipt.TenantBotId != order.TenantBotId || receipt.RejectedAtUtc != null ||
                receipt.OwnerTelegramUserId != order.OwnerTelegramUserId || receipt.CustomerTelegramUserId != order.CustomerTelegramUserId ||
                await db.TenantManualPaymentReceipts.CountAsync(x => x.TenantBotOrderId == order.Id &&
                    x.Status == TenantManualPaymentReceiptStatuses.Approved, ct) != 1 ||
                await db.TenantBotLedgerEntries.AnyAsync(x => x.TenantBotOrderId == order.Id, ct) ||
                await db.TenantCardProvisionalOperations.AnyAsync(x => x.TenantBotOrderId == order.Id && x.Kind == TenantCardProvisionalOperationKinds.Revoke, ct)) return false;
            if (resumeStep == TenantCardProvisionalSteps.Claimed)
            {
                if (!ReviewBinding.IsUnmutatedPlacementFailure(operation)) return false;
            }
            else if (resumeStep != TenantCardProvisionalSteps.Proven) return false;
            review.Status = "authorized";
            review.AuthorizedAtUtc = DateTime.UtcNow;
            review.EvidenceJson = evidence;
            review.OutcomeCode = "authorized_after_fresh_probe";
            // Fence receipt workflows that read before this transaction. Their original-value SaveAsync check must
            // fail rather than overwrite this reviewed payment evidence after authorization. New readers check the lock.
            var receiptVersions = await db.TenantManualPaymentReceipts.Where(x =>
                x.TenantBotOrderId == order.Id || x.OrderId == order.OrderId).ToListAsync(ct);
            foreach (var receiptVersion in receiptVersions) receiptVersion.UpdatedAtUtc = DateTime.UtcNow;
            operation.ReviewedRecoveryId = review.Id;
            operation.Step = resumeStep;
            operation.LastProvenStep = resumeStep;
            operation.MutationPending = false;
            operation.ErrorCode = null;
            operation.CompletedAtUtc = resumeStep == TenantCardProvisionalSteps.Proven ? DateTime.UtcNow : null;
            if (resumeStep == TenantCardProvisionalSteps.Proven) operation.ProvenAtUtc ??= DateTime.UtcNow;
            operation.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }, token);

    /// <summary>Records a mutation boundary before dispatch so reviewed recovery cannot replay a crash/timeout blindly.</summary>
    /// <param name="key">Exact finalize saga key.</param>
    /// <param name="stage">Fixed internal step label, never external content.</param>
    /// <param name="token">Short durable-intent write cancellation; dispatch must wait for success.</param>
    /// <returns>A task after the marker commits; throws rather than mutating a missing or manual-review saga.</returns>
    /// <remarks>AdvanceAsync clears pending only after proof. Ordinary legacy absolute-write retry behavior is unchanged;
    /// reviewed operations inspect this marker on restart and allow GET reconciliation only.</remarks>
    /// <exception cref="InvalidOperationException">The saga is missing or terminal/manual-review.</exception>
    /// <example><code>await store.MarkMutationStartedAsync(finalizeKey, "quota", token); // Await commit BEFORE dispatch.</code></example>
    public Task MarkMutationStartedAsync(string key, string stage, CancellationToken token)
        => SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            var changed = await db.TenantCardProvisionalOperations.Where(x => x.OperationKey == key &&
                x.Step != TenantCardProvisionalSteps.ManualReview &&
                (x.ReviewedRecoveryId == null || !x.MutationPending) &&
                (x.Step != TenantCardProvisionalSteps.Proven || x.ReviewedRecoveryId == null))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.MutationPending, true)
                    .SetProperty(x => x.LastMutationStage, stage).SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow), ct);
            if (changed != 1) throw new InvalidOperationException("Provisional mutation requires an active durable saga.");
            return changed;
        }, token);
}

/// <summary>Pure credential-free hashes and conservative historical pre-mutation proof shared by review and transaction guards.</summary>
internal static class ReviewBinding
{
    /// <summary>Hashes immutable order/receipt/identity/entitlement binding without persisting credential values.</summary>
    /// <param name="o">Detached exact order.</param><param name="s">Its finalize saga.</param><param name="r">Its approved receipt.</param>
    /// <param name="inbounds">Sorted active service placement JSON.</param><returns>Uppercase SHA256; never the serialized private input.</returns>
    /// <remarks>Only hashes leave this helper. Quota/expiry are frozen, not computed from the current time.</remarks>
    internal static string Binding(TenantBotOrder o, TenantCardProvisionalOperation s, TenantManualPaymentReceipt r, string inbounds)
        => Hash(new { OrderInternalId = o.Id, o.OrderId, o.TenantBotId, o.OwnerTelegramUserId, o.CustomerTelegramUserId, o.PaymentProvider,
            o.OrderKind, o.ServiceKey, o.TrafficGb, o.DurationKey, o.UnlimitedPlanKey, o.AccountCount, o.SalePriceToman, o.BaseCostToman,
            o.ProvisionalAccountEmail, o.ProvisionalAccountUuid, o.ProvisionalSubId, s.OperationKey, s.Kind, s.FinalQuotaBytes,
            s.FinalExpiryTimeMs, Effective = s.FinalizationEffectiveAtUtc?.Ticks, ReceiptId = r.Id, r.AmountToman,
            Approved = r.ApprovedAtUtc?.Ticks, Confirmed = r.FinalConfirmedAtUtc?.Ticks, inbounds });
    /// <summary>Hashes mutable database versions in addition to the immutable reviewed binding.</summary>
    /// <param name="o">Current exact order.</param><param name="s">Current saga.</param><param name="r">Current approved receipt.</param>
    /// <param name="inbounds">Sorted current service placement JSON.</param><returns>SHA256 used for stale-confirmation rejection.</returns>
    /// <remarks>SQLite DateTime.Kind differences do not affect ticks. No secrets are logged or returned.</remarks>
    internal static string Snapshot(TenantBotOrder o, TenantCardProvisionalOperation s, TenantManualPaymentReceipt r, string inbounds)
        => Hash(new { Binding = Binding(o, s, r, inbounds), OrderVersion = o.UpdatedAtUtc?.Ticks, o.PaymentStatus,
            o.ProvisionalDeliveryState, s.Step, s.ErrorCode, s.MutationPending, s.LastMutationStage, s.ReviewedRecoveryId,
            s.ResetAtUtc, s.ZeroedAtUtc, s.QuotaWrittenAtUtc, s.ProvenAtUtc, s.EvidenceJson,
            SagaVersion = s.UpdatedAtUtc.Ticks, ReceiptVersion = r.UpdatedAtUtc?.Ticks, r.Status });
    /// <summary>Recognizes only the historical incident's proven before-first-mutation placement failure or an untouched reviewed restart.</summary>
    /// <param name="s">Exact current finalize saga.</param><returns>True only when no prior reset/write/proof or uncertain mutation can be hidden.</returns>
    /// <remarks>Historical null evidence is essential: prior successful quiescence could otherwise hide a reset sent before crash.</remarks>
    internal static bool IsUnmutatedPlacementFailure(TenantCardProvisionalOperation s)
        => !s.MutationPending && s.ResetAtUtc == null && s.ZeroedAtUtc == null && s.QuotaWrittenAtUtc == null && s.ProvenAtUtc == null &&
           ((s.Step == TenantCardProvisionalSteps.ManualReview && s.ErrorCode == "provisional_identity_placement_mismatch" &&
             (string.IsNullOrWhiteSpace(s.EvidenceJson) || s.LastProvenStep == TenantCardProvisionalSteps.Claimed)) ||
            (s.Step == TenantCardProvisionalSteps.Claimed && s.ReviewedRecoveryId != null && s.LastMutationStage == null));
    /// <summary>Hashes an in-memory object and discards its serialized representation.</summary>
    /// <param name="value">Private structured comparison input; never log it.</param><returns>Credential-free SHA256 digest.</returns>
    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(value))));
}
