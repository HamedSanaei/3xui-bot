namespace Adminbot.Domain;

/// <summary>Restricts ordinary and expired AtlasPay wallet-charge eligibility for super-admin review.</summary>
/// <remarks>Expired status is an operator exception, not provider payment proof; tenant orders and known short payments stay excluded.</remarks>
public sealed partial class AtlasPaySettlementService
{
    /// <summary>Returns whether an unresolved AtlasPay row is suitable for ordinary pending-payment admin review.</summary>
    /// <param name="payment">Persisted local wallet charge inspected after an official provider inquiry.</param>
    /// <returns><c>true</c> only for an uncredited owned-wallet charge with no terminal or underpayment evidence.</returns>
    /// <remarks>Provider-terminal orders use the narrower <see cref="CanOfferExpiredAdminReview"/> exception.</remarks>
    public static bool CanOfferAdminReview(AtlasPayPaymentInfo payment)
        => HasUncreditedOwnedWalletCharge(payment) &&
           !AtlasPayStatuses.IsTerminal(payment.ProviderStatus);

    /// <summary>Identifies an expired owned-wallet charge eligible for an explicit bank-confirmed admin decision.</summary>
    /// <param name="payment">Local charge refreshed by the official AtlasPay verify endpoint within two minutes.</param>
    /// <returns><c>true</c> only for a fresh, identity-complete expired charge with no known underpayment or financial claim.</returns>
    /// <remarks>
    /// An expired provider response is NOT payment proof. The configured super-admin must check the actual bank/provider
    /// settlement independently before the two-stage callback; cancelled/rejected orders and tenant funds cannot use
    /// this exception. A provider failure or identity mismatch removes eligibility, and the final callback re-verifies.
    /// </remarks>
    public static bool CanOfferExpiredAdminReview(AtlasPayPaymentInfo payment)
        => HasUncreditedOwnedWalletCharge(payment) &&
           string.Equals(payment.ProviderStatus, "expired", StringComparison.OrdinalIgnoreCase) &&
           string.Equals(payment.ErrorCode, "provider_expired", StringComparison.Ordinal) &&
           payment.LastInquiryAtUtc is DateTime verifiedAtUtc &&
           verifiedAtUtc >= DateTime.UtcNow.AddMinutes(-2) &&
           payment.ProviderOrderId > 0 &&
           !string.IsNullOrWhiteSpace(payment.MerchantOrderRef) &&
           !string.IsNullOrWhiteSpace(payment.TrackingCode) &&
           payment.TotalAmountToman is > 0 &&
           (!payment.ActualReceivedAmountToman.HasValue ||
            payment.ActualReceivedAmountToman.Value >= payment.TotalAmountToman.Value) &&
           string.Equals(payment.SettlementState, AtlasPaySettlementStates.Pending, StringComparison.Ordinal);

    /// <summary>Checks immutable local wallet identity and absence of a competing settlement or delivery claim.</summary>
    /// <param name="payment">Local AtlasPay row; null or an unrelated tenant order is ineligible.</param>
    /// <returns><c>true</c> for an uncredited owned-wallet charge without known accepted underpayment or ambiguous settlement.</returns>
    /// <remarks>This check is shared by pending and expired review; it never proves that AtlasPay received funds.</remarks>
    private static bool HasUncreditedOwnedWalletCharge(AtlasPayPaymentInfo payment)
        => payment != null &&
           string.Equals(payment.PaymentPurpose, TenantBotPaymentPurposes.WalletCharge, StringComparison.OrdinalIgnoreCase) &&
           string.Equals(payment.WalletOriginBotType, BotInstanceTypes.Owned, StringComparison.OrdinalIgnoreCase) &&
           payment.ProviderOrderId.HasValue &&
           payment.CreationState == AtlasPayCreationStates.Created &&
           payment.BaseAmountToman > 0 &&
           !payment.IsAddedToBalance &&
           !payment.RequiresManualDelivery &&
           (!payment.ActualReceivedAmountToman.HasValue || payment.ActualReceivedAmountToman.Value >= payment.BaseAmountToman) &&
           !string.Equals(payment.SettlementState, AtlasPaySettlementStates.Processing, StringComparison.Ordinal) &&
           !string.Equals(payment.SettlementState, AtlasPaySettlementStates.ManualReview, StringComparison.Ordinal) &&
           !string.Equals(payment.SettlementState, AtlasPaySettlementStates.Settled, StringComparison.Ordinal);
}
