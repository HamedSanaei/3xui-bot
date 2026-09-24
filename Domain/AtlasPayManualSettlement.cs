namespace Adminbot.Domain;

/// <summary>AtlasPay admin-review eligibility helpers.</summary>
public sealed partial class AtlasPaySettlementService
{
    /// <summary>Returns whether an unresolved AtlasPay row is suitable for explicit super-admin review.</summary>
    /// <param name="payment">Persisted row inspected after a fresh provider verification.</param>
    /// <returns><c>true</c> only for unresolved owned-wallet charges without terminal or underpayment evidence.</returns>
    public static bool CanOfferAdminReview(AtlasPayPaymentInfo payment)
        => payment != null &&
           string.Equals(payment.PaymentPurpose, TenantBotPaymentPurposes.WalletCharge, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(payment.WalletOriginBotType, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase) &&
           payment.ProviderOrderId.HasValue &&
           payment.CreationState == AtlasPayCreationStates.Created &&
           payment.BaseAmountToman > 0 &&
           !payment.IsAddedToBalance &&
           !payment.RequiresManualDelivery &&
           (!payment.ActualReceivedAmountToman.HasValue || payment.ActualReceivedAmountToman.Value >= payment.BaseAmountToman) &&
           !AtlasPayStatuses.IsTerminal(payment.ProviderStatus) &&
           !string.Equals(payment.SettlementState, AtlasPaySettlementStates.Processing, StringComparison.Ordinal) &&
           !string.Equals(payment.SettlementState, AtlasPaySettlementStates.ManualReview, StringComparison.Ordinal) &&
           !string.Equals(payment.SettlementState, AtlasPaySettlementStates.Settled, StringComparison.Ordinal);
}
