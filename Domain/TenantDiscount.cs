namespace Adminbot.Domain
{
    /// <summary>Supported forms of a tenant storefront discount.</summary>
    public static class TenantDiscountKinds
    {
        /// <summary>A fixed whole-toman reduction.</summary>
        public const string Fixed = "fixed";
        /// <summary>A percentage reduction, rounded down to whole toman.</summary>
        public const string Percent = "percent";
    }

    /// <summary>Eligible tenant storefront checkout types.</summary>
    public static class TenantDiscountScopes
    {
        /// <summary>New tenant account purchases only.</summary>
        public const string Purchase = "purchase";
        /// <summary>Tenant account renewals only.</summary>
        public const string Renew = "renew";
        /// <summary>Both purchases and renewals.</summary>
        public const string Both = "both";
    }

    /// <summary>Lifecycle of a customer-visible checkout quote.</summary>
    public static class TenantDiscountQuoteStates
    {
        /// <summary>Displayed checkout not yet committed to an order.</summary>
        public const string Open = "open";
        /// <summary>Checkout committed to the linked payable order.</summary>
        public const string Admitted = "admitted";
        /// <summary>Unadmitted checkout no longer eligible for admission.</summary>
        public const string Expired = "expired";
    }

    /// <summary>Lifecycle of an exactly-once discount claim against one order.</summary>
    public static class TenantDiscountRedemptionStates
    {
        /// <summary>Unpaid order occupies one use of its code.</summary>
        public const string Reserved = "reserved";
        /// <summary>Verified payment has permanently used this claim unless a verified refund releases it.</summary>
        public const string Consumed = "consumed";
        /// <summary>Authoritatively unpaid order or verified refund freed its claim.</summary>
        public const string Released = "released";
    }

    /// <summary>Owner-managed tenant-scoped definition; deletion is soft so order claims remain auditable.</summary>
    public class TenantDiscountCode
    {
        /// <summary>Database identity, never exposed as an authorization token.</summary>
        public int Id { get; set; }
        /// <summary>Storefront bot owning the code and its capacity.</summary>
        public string TenantBotId { get; set; }
        /// <summary>Canonical uppercase ASCII customer input, unique among live codes in this storefront.</summary>
        public string Code { get; set; }
        /// <summary>Either fixed or percent.</summary>
        public string Kind { get; set; }
        /// <summary>Purchase, renew or both.</summary>
        public string Scope { get; set; }
        /// <summary>Positive whole-toman reduction for fixed codes; null for percentage codes.</summary>
        public long? FixedAmountToman { get; set; }
        /// <summary>Integer percentage from 1 to 100 for percentage codes; null for fixed codes.</summary>
        public int? Percent { get; set; }
        /// <summary>Optional positive whole-toman ceiling on fixed or percentage reductions.</summary>
        public long? MaxDiscountToman { get; set; }
        /// <summary>Minimum undiscounted checkout amount in whole toman.</summary>
        public long MinimumOrderToman { get; set; }
        /// <summary>Maximum simultaneous reserved plus consumed uses across the storefront.</summary>
        public int MaxUses { get; set; }
        /// <summary>Whether new checkouts may select this live code.</summary>
        public bool IsActive { get; set; }
        /// <summary>Soft-delete marker; old claims remain linked even after deletion.</summary>
        public bool IsDeleted { get; set; }
        /// <summary>Creation timestamp in UTC.</summary>
        public DateTime CreatedAtUtc { get; set; }
        /// <summary>Configuration revision, changed on real owner edits, not writer-lock acquisition.</summary>
        public DateTime UpdatedAtUtc { get; set; }
    }

    /// <summary>Customer/message-bound displayed price and one-time admission record for tenant purchase checkout.</summary>
    public class TenantDiscountQuote
    {
        /// <summary>Database identity used by compact callbacks, not by itself proof of ownership.</summary>
        public int Id { get; set; }
        /// <summary>Storefront bot scope.</summary>
        public string TenantBotId { get; set; }
        /// <summary>Telegram sender bound to the displayed quote.</summary>
        public long CustomerTelegramUserId { get; set; }
        /// <summary>Telegram chat containing the displayed quote.</summary>
        public long ChatId { get; set; }
        /// <summary>Real Telegram message id, null until successfully displayed.</summary>
        public int? MessageId { get; set; }
        /// <summary>Server-side checkout selection, never replaced by callback-supplied prices.</summary>
        public string SelectionKey { get; set; }
        /// <summary>Selected code identity, or null for an undiscounted quote.</summary>
        public int? CodeId { get; set; }
        /// <summary>Selected code configuration revision captured at preview.</summary>
        public DateTime? CodeUpdatedAtUtc { get; set; }
        /// <summary>Undiscounted storefront price in whole toman.</summary>
        public long GrossToman { get; set; }
        /// <summary>Colleague cost used only to enforce the profit floor.</summary>
        public long BaseCostToman { get; set; }
        /// <summary>Actual capped reduction displayed to the customer.</summary>
        public long DiscountAmountToman { get; set; }
        /// <summary>Displayed and admitted payable amount in whole toman.</summary>
        public long NetToman { get; set; }
        /// <summary>Admitted order identity, null before admission.</summary>
        public int? OrderId { get; set; }
        /// <summary>First admitted payment method, frozen for replay safety.</summary>
        public string SelectedProvider { get; set; }
        /// <summary>Open, admitted or expired.</summary>
        public string State { get; set; } = TenantDiscountQuoteStates.Open;
        /// <summary>Creation timestamp in UTC.</summary>
        public DateTime CreatedAtUtc { get; set; }
        /// <summary>Last display or state-transition timestamp in UTC.</summary>
        public DateTime UpdatedAtUtc { get; set; }
        /// <summary>Unadmitted UI expiry timestamp in UTC.</summary>
        public DateTime ExpiresAtUtc { get; set; }
        /// <summary>First admission timestamp, if admitted.</summary>
        public DateTime? AdmittedAtUtc { get; set; }
    }

    /// <summary>Exactly one durable capacity claim for an admitted tenant order; never physically deleted.</summary>
    public class TenantDiscountRedemption
    {
        /// <summary>Database identity of the audit record.</summary>
        public int Id { get; set; }
        /// <summary>Original tenant code identity, retained through soft deletion.</summary>
        public int CodeId { get; set; }
        /// <summary>Unique order that owns this claim.</summary>
        public int TenantBotOrderId { get; set; }
        /// <summary>Reserved, consumed or released.</summary>
        public string State { get; set; } = TenantDiscountRedemptionStates.Reserved;
        /// <summary>Capacity reservation timestamp in UTC.</summary>
        public DateTime ReservedAtUtc { get; set; }
        /// <summary>Verified payment timestamp, if consumed.</summary>
        public DateTime? ConsumedAtUtc { get; set; }
        /// <summary>Authoritative release timestamp, if released.</summary>
        public DateTime? ReleasedAtUtc { get; set; }
    }
}
