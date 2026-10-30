using Adminbot.Domain;
using Adminbot.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;

/// <summary>Tenant storefront operations; reviewed provisional recovery shares the ordinary fulfillment gate and financial tail.</summary>
public partial class TenantBotService
{
    /// <summary>Checks the durable receipt freeze of an explicitly reviewed provisional purchase.</summary>
    /// <param name="orderId">Internal exact tenant order id from its loaded receipt/order, never a callback customer id.</param>
    /// <param name="token">Local users.db read cancellation.</param>
    /// <returns>True after reviewed authorization, including interrupted recovery; ordinary/renewal/gateway orders remain false.</returns>
    /// <remarks>Call AFTER capturing the receipt snapshot and BEFORE editing it. Authorization changes UpdatedAtUtc atomically,
    /// so an earlier reader also fails the existing workflow original-value comparison if it races this guard.</remarks>
    private Task<bool> IsReviewedProvisionalReceiptLockedAsync(int orderId, CancellationToken token)
        => _workflow.ReadAsync(db => db.TenantCardProvisionalOperations.AnyAsync(
            x => x.TenantBotOrderId == orderId && x.ReviewedRecoveryId != null, token));

    /// <summary>Re-enters central paid purchase fulfillment with a separate administrator-bound reviewed authorization.</summary>
    /// <param name="orderId">Internal existing tenant order id, never a new order/payment.</param>
    /// <param name="request">Second confirmation; freshly authorized under the central order gate.</param>
    /// <param name="token">Cancellation of panel proof, durable transition and existing financial settlement.</param>
    /// <returns>Existing settlement outcome; only the order's durable IsFulfilled flag proves completion.</returns>
    /// <remarks>Never calls Verify payment, never manufactures receipt approval, and never creates an XUI retry generation.
    /// Exactly-once base-cost keys, tenant ledger uniqueness and notification intents remain in the original central path.</remarks>
    /// <example><code>var result = await tenantService.RecoverReviewedProvisionalOrderAsync(existingOrder.Id, confirmation, token);</code></example>
    public async Task<NowPaymentsSettlementResult> RecoverReviewedProvisionalOrderAsync(int orderId,
        TenantCardReviewedRecoveryRequest request, CancellationToken token)
    {
        if (request == null || !_serviceProvider.GetRequiredService<TenantCardProvisionalManualReviewService>()
                .IsAuthorized(request.Actor, request.BotId, request.ChatId))
            return NowPaymentsSettlementResult.ProviderNotPaid();
        return await FULFILLPAIDTENANTORDERASYNC(new TenantBotOrder { Id = orderId }, "super-admin-provisional-review",
            null, null, true, token, provisionalReview: request);
    }
}
