using System.Text.Json;
using System.Threading;
using Telegram.Bot;

namespace Adminbot.Domain
{
    /// <summary>
    /// In-memory runtime configuration for one Telegram bot instance.
    /// Instances are loaded from configuration for owned brand bots and from users.db for tenant storefront bots.
    /// </summary>
    public class BotInstanceConfig
    {
        public string Id { get; set; }
        public string Username { get; set; }
        public string Token { get; set; }
        public string BrandName { get; set; }
        public List<string> ChannelIds { get; set; } = new();
        public string SupportAccount { get; set; }
        public string LoggerChannel { get; set; }
        public string BackupChannel { get; set; }
        public string[] IosTutorial { get; set; }
        public string[] AndroidTutorial { get; set; }
        public string[] WindowsTutorial { get; set; }
        public string Type { get; set; } = BotInstanceTypes.Owned;
        public bool IsDefault { get; set; }
        public bool Enabled { get; set; } = true;
        public long? OwnerTelegramUserId { get; set; }
        public int TenantPriceMarkupPercent { get; set; }
        public string TenantWelcomeText { get; set; }
        public bool TenantMandatoryJoinEnabled { get; set; }
        public List<string> TenantChannelIds { get; set; } = new();
        public bool TenantCardPaymentEnabled { get; set; }
        public string TenantCardNumber { get; set; }
        public string TenantCardHolderName { get; set; }
        public bool TenantHooshPayEnabled { get; set; } = true;
        public bool TenantNowPaymentsEnabled { get; set; } = true;
        /// <summary>
        /// Allows this tenant storefront to offer Tetraminator while the global gateway switch is enabled.
        /// </summary>
        public bool TenantTetraminatorEnabled { get; set; } = true;
        /// <summary>
        /// Allows this tenant storefront to offer UniquePay while the global gateway switch is enabled.
        /// </summary>
        public bool TenantUniquePayEnabled { get; set; } = true;
        public bool TenantAtlasPayEnabled { get; set; } = true;
        public string TenantOwnerNotificationBotId { get; set; }
        /// <summary>
        /// JSON array of tenant-owned tutorial links shown to storefront customers.
        /// Each item contains a user-facing title and a Telegram or web URL owned by the tenant.
        /// </summary>
        public string TenantTutorialsJson { get; set; }
        public bool IsSalesAssistant { get; set; }
    }

    /// <summary>
    /// Supported bot ownership modes.
    /// Owned bots are controlled by the project config; tenant bots are created by colleagues at runtime.
    /// </summary>
    public static class BotInstanceTypes
    {
        public const string Owned = "owned";
        public const string Tenant = "tenant";
        public const string SalesAssistant = "sales_assistant";
    }

    /// <summary>
    /// Persisted representation of a bot instance in users.db.
    /// This lets runtime-created tenant bots survive application restarts without changing credentials.db.
    /// </summary>
    public class BotInstance
    {
        /// <summary>Stable one-based storefront number within OwnerTelegramUserId; null for non-tenant bots.</summary>
        /// <remarks>Unique with the owner. Resetting a store retains its number, id, orders and financial history.</remarks>
        public int? TenantStoreNumber { get; set; }
        /// <summary>Original add-button nonce; retained on reset so Telegram redelivery cannot allocate another store.</summary>
        public string TenantCreationKey { get; set; }
        /// <summary>Unique numeric Telegram bot identity reserved for this token; tenant registration verifies it with getMe.</summary>
        /// <remarks>Historical/configured rows derive it from the token prefix before receivers start. It is not a credential.</remarks>
        public long? TelegramBotId { get; set; }
        public string Id { get; set; }
        public string Username { get; set; }
        public string Token { get; set; }
        public string BrandName { get; set; }
        public string Type { get; set; } = BotInstanceTypes.Owned;
        public bool IsDefault { get; set; }
        public bool Enabled { get; set; } = true;
        public long? OwnerTelegramUserId { get; set; }
        public string ChannelIdsJson { get; set; }
        public string SupportAccount { get; set; }
        public string LoggerChannel { get; set; }
        public string BackupChannel { get; set; }
        public string IosTutorialJson { get; set; }
        public string AndroidTutorialJson { get; set; }
        public string WindowsTutorialJson { get; set; }
        public int TenantPriceMarkupPercent { get; set; }
        public string TenantWelcomeText { get; set; }
        public bool TenantMandatoryJoinEnabled { get; set; }
        public string TenantChannelIdsJson { get; set; }
        public bool TenantCardPaymentEnabled { get; set; }
        public string TenantCardNumber { get; set; }
        public string TenantCardHolderName { get; set; }
        public bool TenantHooshPayEnabled { get; set; } = true;
        public bool TenantNowPaymentsEnabled { get; set; } = true;
        /// <summary>
        /// Tenant-scoped preference for the Tetraminator gateway; the global configuration remains the final gate.
        /// </summary>
        public bool TenantTetraminatorEnabled { get; set; } = true;
        /// <summary>
        /// Tenant-scoped preference for UniquePay; the live global switch remains the final invoice-creation gate.
        /// </summary>
        public bool TenantUniquePayEnabled { get; set; } = true;
        public bool TenantAtlasPayEnabled { get; set; } = true;
        public string TenantOwnerNotificationBotId { get; set; }
        /// <summary>
        /// Stores tenant-owned tutorial links as JSON in users.db.
        /// The value is scoped to this bot instance and is never shared with owned bots or other tenants.
        /// </summary>
        public string TenantTutorialsJson { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAtUtc { get; set; }
    }

    /// <summary>
    /// Purpose values stored on HooshPayPaymentInfo so settlement can route the payment correctly.
    /// </summary>
    public static class TenantBotPaymentPurposes
    {
        public const string WalletCharge = "wallet_charge";
        public const string TenantOrder = "tenant_order";
    }

    /// <summary>
    /// Local order states for a tenant storefront sale.
    /// These states are independent from the external HooshPay status.
    /// </summary>
    public static class TenantBotOrderStatuses
    {
        public const string Pending = "pending";
        public const string AwaitingReceipt = "awaiting_receipt";
        public const string ReceiptSubmitted = "receipt_submitted";
        public const string ReceiptApproved = "receipt_approved";
        public const string ReceiptRejected = "receipt_rejected";
        public const string Paid = "paid";
        public const string Fulfilled = "fulfilled";
        public const string Failed = "failed";
    }

    /// <summary>
    /// Tenant order kinds used to distinguish new account purchases from renewals of existing XUI clients.
    /// </summary>
    public static class TenantBotOrderKinds
    {
        /// <summary>
        /// A tenant storefront order that creates a new XUI account after payment settlement.
        /// </summary>
        public const string Purchase = "purchase";

        /// <summary>
        /// A tenant storefront order that renews an existing XUI account after payment settlement.
        /// </summary>
        public const string Renew = "renew";
    }

    /// <summary>
    /// Stable evidence modes persisted for tenant renewal service-category authorization.
    /// </summary>
    /// <remarks>
    /// These values separate an authoritative metadata decision from a deterministic legacy fallback and an explicit
    /// customer choice for a legacy client whose shared inbounds cannot distinguish normal from unlimited service.
    /// They grant renewal permission only when the current email, UUID, and compatible live services are revalidated.
    /// </remarks>
    public static class TenantRenewalServiceResolutionModes
    {
        /// <summary>The service was read from identity-checked structured XUI client metadata.</summary>
        public const string Metadata = "metadata";

        /// <summary>The service was uniquely determined from legacy national inbound, first-use expiry, or one compatible service.</summary>
        public const string LegacyDeterministic = "legacy_deterministic";

        /// <summary>The customer explicitly selected one currently compatible service for an otherwise ambiguous legacy client.</summary>
        public const string CustomerSelectedLegacy = "customer_selected_legacy";
    }

    /// <summary>
    /// Represents one direct-sale order made inside a colleague tenant bot.
    /// The order links customer, owner, selected XUI plan, HooshPay invoice, fulfillment result, and owner profit.
    /// </summary>
    public class TenantBotOrder
    {
        public int Id { get; set; }
        public string OrderId { get; set; }
        public string TenantBotId { get; set; }
        public string TenantBotUsername { get; set; }
        public long OwnerTelegramUserId { get; set; }
        public long CustomerTelegramUserId { get; set; }
        public long CustomerChatId { get; set; }
        public string CustomerUsername { get; set; }
        public string CustomerFirstName { get; set; }
        public string CustomerLastName { get; set; }
        /// <summary>
        /// Business kind of this order, for example a new purchase or a renewal.
        /// Fulfillment uses this value to decide whether to create a new XUI client or update an existing one.
        /// </summary>
        public string OrderKind { get; set; } = TenantBotOrderKinds.Purchase;
        /// <summary>
        /// Existing XUI client email that should be renewed when <see cref="OrderKind"/> is <c>renew</c>.
        /// This field is tenant-scoped through <see cref="TenantBotId"/> and must be empty for normal purchases.
        /// </summary>
        public string TargetAccountEmail { get; set; }
        /// <summary>
        /// Normalized XUI client UUID that pins a tenant renewal order to the exact account selected.
        /// </summary>
        /// <remarks>
        /// Every newly created safely lockable renewal order stores the panel-derived UUID regardless of whether the
        /// account belongs to the payer. Fulfillment must match it with <see cref="TargetAccountEmail"/> before any
        /// panel or wallet effect. Null is retained for old orders and owned legacy clients whose panel row has no valid
        /// UUID; those remain owner-checked. The value must not enter callbacks, messages, or operational logs.
        /// </remarks>
        public string TargetAccountUuid { get; set; }
        /// <summary>
        /// Gets or sets the evidence mode that authorized <see cref="ServiceKey" /> for a tenant renewal order.
        /// </summary>
        /// <remarks>
        /// This value is null for historical orders. New renewals persist one value from
        /// <see cref="TenantRenewalServiceResolutionModes" /> before payment-provider creation. A customer-selected
        /// legacy mode is valid only while a fresh identity-checked panel read still lists the stored service among the
        /// compatible candidates. It never transfers account ownership or management access.
        /// </remarks>
        public string RenewalServiceResolutionMode { get; set; }
        public string ServiceKey { get; set; }
        public int? TrafficGb { get; set; }
        public string DurationKey { get; set; }
        public string UnlimitedPlanKey { get; set; }
        public int AccountCount { get; set; } = 1;
        public string UserComment { get; set; }
        public long SalePriceToman { get; set; }
        public long BaseCostToman { get; set; }
        public long ProfitToman { get; set; }
        public string PaymentProvider { get; set; } = "hooshpay";
        public string PaymentStatus { get; set; } = TenantBotOrderStatuses.Pending;
        public int? HooshPayPaymentInfoId { get; set; }
        public int? NowPaymentsPaymentInfoId { get; set; }
        /// <summary>
        /// Optional users.db identifier of the Tetraminator payment that funds this tenant order.
        /// </summary>
        public int? TetraminatorPaymentInfoId { get; set; }
        /// <summary>
        /// Optional users.db identifier of the UniquePay payment that funds this tenant purchase or renewal.
        /// </summary>
        public int? UniquePayPaymentInfoId { get; set; }
        public int? AtlasPayPaymentInfoId { get; set; }
        public int? ManualReceiptId { get; set; }
        public string HooshPayInvoiceUid { get; set; }
        public string PaymentUrl { get; set; }
        public bool IsFulfilled { get; set; }
        public bool IsOwnerCredited { get; set; }
        public string FulfillmentSource { get; set; }
        public long OwnerWalletDelta { get; set; }
        public long? OwnerBalanceBefore { get; set; }
        public long? OwnerBalanceAfter { get; set; }
        public string CreatedAccountEmail { get; set; }
        public string CreatedSubLink { get; set; }
        public string CreatedAccountJson { get; set; }
        public string ErrorMessage { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAtUtc { get; set; }
        public DateTime? PaidAtUtc { get; set; }
        public DateTime? FulfilledAtUtc { get; set; }
    }

    /// <summary>
    /// Append-only ledger entry created when a tenant order profit is credited to the owner balance.
    /// It is used for accounting and audit; it should not be edited after creation.
    /// </summary>
    public class TenantBotLedgerEntry
    {
        public int Id { get; set; }
        public string TenantBotId { get; set; }
        public string TenantBotUsername { get; set; }
        public int TenantBotOrderId { get; set; }
        public string OrderId { get; set; }
        public long OwnerTelegramUserId { get; set; }
        public long CustomerTelegramUserId { get; set; }
        public long SalePriceToman { get; set; }
        public long BaseCostToman { get; set; }
        public long ProfitToman { get; set; }
        public long? OwnerBalanceBefore { get; set; }
        public long? OwnerBalanceAfter { get; set; }
        public string Description { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Direction values used by <see cref="WalletLedgerEntry.Direction" />.
    /// </summary>
    public static class WalletLedgerDirections
    {
        public const string Credit = "credit";
        public const string Debit = "debit";
    }

    /// <summary>
    /// Business reason values used to classify wallet ledger rows for user history and admin audit.
    /// </summary>
    public static class WalletLedgerReasons
    {
        public const string WalletCharge = "wallet_charge";
        public const string AccountPurchase = "account_purchase";
        public const string AccountRenew = "account_renew";
        public const string AdminAdjustment = "admin_adjustment";
        public const string TenantGatewayProfit = "tenant_gateway_profit";
        public const string TenantCardBaseCost = "tenant_card_base_cost";
        /// <summary>Credit generated by the global owned-bot referral program.</summary>
        public const string ReferralReward = "referral_reward";
        /// <summary>Debit reserved for an explicit future reversal of a previously applied referral reward.</summary>
        public const string ReferralRewardReversal = "referral_reward_reversal";
    }

    /// <summary>
    /// Append-only audit row for one wallet balance change.
    /// </summary>
    /// <remarks>
    /// The actual wallet balance remains in <c>credentials.db</c>. This entity is stored in <c>users.db</c>
    /// after the balance mutation and records before/after values so credits and debits can be shown to users.
    /// </remarks>
    public class WalletLedgerEntry
    {
        public int Id { get; set; }
        public string BotId { get; set; }
        public string BotUsername { get; set; }
        public string BotType { get; set; }
        public long? OwnerTelegramUserId { get; set; }
        public long TelegramUserId { get; set; }
        public long? CounterpartyTelegramUserId { get; set; }
        public string Direction { get; set; }
        public long AmountToman { get; set; }
        public long BalanceBefore { get; set; }
        public long BalanceAfter { get; set; }
        public string Reason { get; set; }
        public string Provider { get; set; }
        public string ReferenceType { get; set; }
        public string ReferenceId { get; set; }
        public string OrderId { get; set; }
        public string Description { get; set; }
        /// <summary>
        /// Optional globally unique financial mutation key used to make ledger persistence retry-safe.
        /// </summary>
        public string IdempotencyKey { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    /// <summary>
    /// Review statuses for tenant card-to-card receipts handled by the sales assistant bot.
    /// </summary>
    public static class TenantManualPaymentReceiptStatuses
    {
        public const string Pending = "pending";
        public const string Approved = "approved";
        public const string Rejected = "rejected";
    }

    /// <summary>
    /// Pending or reviewed card-to-card receipt submitted by a tenant storefront customer.
    /// </summary>
    /// <remarks>
    /// A receipt links one customer photo to one tenant order. Approval is two-step in the sales assistant bot;
    /// final approval fulfills the order and debits the tenant owner's base cost from the shared wallet.
    /// </remarks>
    public class TenantManualPaymentReceipt
    {
        public int Id { get; set; }
        public int TenantBotOrderId { get; set; }
        public string OrderId { get; set; }
        public string TenantBotId { get; set; }
        public string TenantBotUsername { get; set; }
        public long OwnerTelegramUserId { get; set; }
        public long CustomerTelegramUserId { get; set; }
        public long CustomerChatId { get; set; }
        public string PhotoFileId { get; set; }
        public long AmountToman { get; set; }
        public string Status { get; set; } = TenantManualPaymentReceiptStatuses.Pending;
        public long? ReviewerTelegramUserId { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAtUtc { get; set; }
        public DateTime? ApprovedAtUtc { get; set; }
        public DateTime? RejectedAtUtc { get; set; }
        public DateTime? FinalConfirmedAtUtc { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Bot-scoped conversation state.
    /// It mirrors the legacy User state shape while adding BotId to prevent cross-brand state collisions.
    /// </summary>
    public class BotUserState
    {
        /// <summary>Selected storefront database id for owner input in this owned-bot/user conversation.</summary>
        /// <remarks>Not an authorization grant: every handler rechecks the owner. Switching stores clears pending input.</remarks>
        public string OwnerStoreId { get; set; }
        public string BotId { get; set; }
        public long TelegramUserId { get; set; }
        public string SelectedCountry { get; set; }
        public string SelectedPeriod { get; set; }
        public string Type { get; set; }
        public string Flow { get; set; }
        public string LastStep { get; set; }
        public string TotoalGB { get; set; }
        public string ConfigLink { get; set; }
        public string SubLink { get; set; }
        public string Email { get; set; }
        public string _ConfigPrice { get; set; }
        public DateTime LastFreeAcc { get; set; } = DateTime.MinValue;
        public string PaymentMethod { get; set; } = "credit";
        /// <summary>
        /// Normalized panel UUID that locks the exact account selected for renewal.
        /// </summary>
        /// <remarks>
        /// Every safely lockable owned or tenant renewal stores this value independently from <see cref="PaymentMethod"/>,
        /// whether or not the payer owns the account. Preview, order, and completion compare it with saved email and
        /// fresh panel data. Empty values are accepted for old state and owned legacy clients whose panel row lacks a
        /// valid UUID; those remain owner-checked. This lock grants no management or configuration permission.
        /// </remarks>
        public string RenewTargetUuid { get; set; }
        /// <summary>
        /// Stable confirmation-session identity used as the exactly-once renewal operation key.
        /// </summary>
        /// <remarks>
        /// The value is generated once when the renewal flow enters the confirm step and stays stable across
        /// Telegram redelivery, repeated confirm presses, and process restarts. It is cleared with the conversation
        /// when the renewal finishes, so a later legitimate renewal starts a brand-new session and a new operation.
        /// </remarks>
        public string RenewalSessionId { get; set; }
        /// <summary>Owned purchase session id scoped by BotId and TelegramUserId; cleared with transient conversation state.</summary>
        public string PurchaseSessionId { get; set; }
        /// <summary>
        /// Gets or sets the evidence mode for the temporary tenant renewal service category.
        /// </summary>
        /// <remarks>
        /// The value is scoped by <see cref="BotId" /> plus <see cref="TelegramUserId" /> and is cleared with the
        /// conversation. It is copied to a tenant renewal order only after the exact email and UUID target and the live
        /// compatible service set are revalidated. Owned renewal flows leave it empty.
        /// </remarks>
        public string RenewalServiceResolutionMode { get; set; }
        public int AccountCounter { get; set; }
        public int PendingAccountCount { get; set; }
        public string PendingUserComment { get; set; }
        public DateTime LastFreeNationalAcc { get; set; } = DateTime.MinValue;
        public DateTime LastFreeNormalAcc { get; set; } = DateTime.MinValue;
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
        public DateTime? UpdatedAtUtc { get; set; }

        /// <summary>
        /// Creates a bot-scoped state row from the legacy User flow object.
        /// </summary>
        /// <param name="botId">Runtime bot id that owns this conversation state.</param>
        /// <param name="user">Legacy User state object collected by existing call sites.</param>
        /// <returns>A new BotUserState that can be inserted into users.db.</returns>
        /// <remarks>
        /// The conversion copies an optional renewal UUID target lock and tenant service-resolution evidence into the
        /// specified bot scope, along with the owner-selected store id. It performs no account, wallet, order, or panel operation and callers remain responsible
        /// for persisting the returned row.
        /// </remarks>
        public static BotUserState FromUser(string botId, User user)
        {
            return new BotUserState
            {
                BotId = string.IsNullOrWhiteSpace(botId) ? BotContextAccessor.DefaultBotId : botId,
                TelegramUserId = user.Id,
                OwnerStoreId = user.OwnerStoreId,
                SelectedCountry = user.SelectedCountry,
                SelectedPeriod = user.SelectedPeriod,
                Type = user.Type,
                Flow = user.Flow,
                LastStep = user.LastStep,
                TotoalGB = user.TotoalGB,
                ConfigLink = user.ConfigLink,
                SubLink = user.SubLink,
                Email = user.Email,
                _ConfigPrice = user._ConfigPrice,
                LastFreeAcc = user.LastFreeAcc,
                PaymentMethod = user.PaymentMethod ?? "credit",
                RenewTargetUuid = user.RenewTargetUuid,
                RenewalSessionId = user.RenewalSessionId,
                PurchaseSessionId = user.PurchaseSessionId,
                RenewalServiceResolutionMode = user.RenewalServiceResolutionMode,
                AccountCounter = user.AccountCounter,
                PendingAccountCount = user.PendingAccountCount,
                PendingUserComment = user.PendingUserComment,
                LastFreeNationalAcc = user.LastFreeNationalAcc,
                LastFreeNormalAcc = user.LastFreeNormalAcc,
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Converts this bot-scoped state back to the legacy User shape expected by existing flow code.
        /// </summary>
        /// <returns>A User object with the same conversation fields and Telegram user id.</returns>
        /// <remarks>
        /// The sensitive renewal UUID target lock and tenant service-resolution evidence are copied so later preview/payment
        /// handlers can revalidate them; the owner-selected store id is also restored for input after restart.
        /// The returned compatibility DTO is detached and authorizes no action by itself.
        /// </remarks>
        public User ToUser()
        {
            return new User
            {
                Id = TelegramUserId,
                OwnerStoreId = OwnerStoreId,
                SelectedCountry = SelectedCountry,
                SelectedPeriod = SelectedPeriod,
                Type = Type,
                Flow = Flow,
                LastStep = LastStep,
                TotoalGB = TotoalGB,
                ConfigLink = ConfigLink,
                SubLink = SubLink,
                Email = Email,
                _ConfigPrice = _ConfigPrice,
                LastFreeAcc = LastFreeAcc,
                PaymentMethod = PaymentMethod ?? "credit",
                RenewTargetUuid = RenewTargetUuid,
                RenewalSessionId = RenewalSessionId,
                PurchaseSessionId = PurchaseSessionId,
                RenewalServiceResolutionMode = RenewalServiceResolutionMode,
                AccountCounter = AccountCounter,
                PendingAccountCount = PendingAccountCount,
                PendingUserComment = PendingUserComment,
                LastFreeNationalAcc = LastFreeNationalAcc,
                LastFreeNormalAcc = LastFreeNormalAcc
            };
        }

        /// <summary>
        /// Applies every non-null field from a legacy User object while preserving omitted partial values.
        /// </summary>
        /// <param name="user">Partial legacy state update.</param>
        /// <remarks>
        /// Null means "preserve the stored value" for all nullable legacy fields; an explicit empty string clears a
        /// string field. This distinction preserves the renewal UUID target lock and service-resolution evidence across
        /// payment-method and plan updates but means callers that must clear either value must pass an empty string or use
        /// a full reset. OwnerStoreId follows the same null-preserves/empty-clears rule. Callers must save the tracked state after this in-memory merge.
        /// </remarks>
        public void ApplyPartial(User user)
        {
            if (user.OwnerStoreId != null) OwnerStoreId = user.OwnerStoreId;
            if (user.SelectedCountry != null) SelectedCountry = user.SelectedCountry;
            if (user.SelectedPeriod != null) SelectedPeriod = user.SelectedPeriod;
            if (user.Type != null) Type = user.Type;
            if (user.LastStep != null) LastStep = user.LastStep;
            if (user.TotoalGB != null) TotoalGB = user.TotoalGB;
            if (user.ConfigLink != null) ConfigLink = user.ConfigLink;
            if (user.SubLink != null) SubLink = user.SubLink;
            if (user.Email != null) Email = user.Email;
            if (user.Flow != null) Flow = user.Flow;
            if (user._ConfigPrice != null) _ConfigPrice = user._ConfigPrice;
            if (user.AccountCounter > AccountCounter) AccountCounter = user.AccountCounter;
            if (user.PaymentMethod != PaymentMethod) PaymentMethod = user.PaymentMethod;
            if (user.RenewTargetUuid != null) RenewTargetUuid = user.RenewTargetUuid;
            if (user.RenewalSessionId != null) RenewalSessionId = user.RenewalSessionId;
            if (user.PurchaseSessionId != null) PurchaseSessionId = user.PurchaseSessionId;
            if (user.RenewalServiceResolutionMode != null) RenewalServiceResolutionMode = user.RenewalServiceResolutionMode;
            if (user.PendingAccountCount > 0) PendingAccountCount = user.PendingAccountCount;
            if (user.PendingUserComment != null) PendingUserComment = user.PendingUserComment;
            if (user.LastFreeAcc > DateTime.MinValue) LastFreeAcc = user.LastFreeAcc;
            if (user.LastFreeNationalAcc > DateTime.MinValue) LastFreeNationalAcc = user.LastFreeNationalAcc;
            if (user.LastFreeNormalAcc > DateTime.MinValue) LastFreeNormalAcc = user.LastFreeNormalAcc;
            UpdatedAtUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Clears transient flow fields while keeping the bot/user row and long-lived counters.
        /// </summary>
        /// <remarks>
        /// Renewal UUID target lock, tenant category evidence, and payment choice are cleared with the conversation so a
        /// later flow cannot inherit authorization. Wallets, tenant orders, account metadata, and state rows belonging to
        /// other bots are untouched. The owner-selected store is cleared too, cancelling pending settings input.
        /// </remarks>
        public void Clear()
        {
            OwnerStoreId = "";
            SelectedCountry = "";
            SelectedPeriod = "";
            Type = "";
            LastStep = "";
            Flow = "";
            TotoalGB = "";
            ConfigLink = "";
            Email = "";
            SubLink = "";
            _ConfigPrice = "0";
            PaymentMethod = "credit";
            RenewTargetUuid = "";
            RenewalSessionId = "";
            PurchaseSessionId = "";
            RenewalServiceResolutionMode = "";
            PendingAccountCount = 0;
            PendingUserComment = "";
            UpdatedAtUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Runtime context attached to one incoming Telegram update.
    /// It carries both the bot config and the concrete Telegram client that received the update.
    /// </summary>
    public class BotRuntimeContext
    {
        public BotInstanceConfig Config { get; init; }
        public ITelegramBotClient Client { get; init; }
        public string BotId => Config?.Id ?? BotContextAccessor.DefaultBotId;
        public string Username => Config?.Username;
    }

    /// <summary>
    /// AsyncLocal accessor for the current bot context.
    /// Services that were originally single-bot can use this to resolve the correct BotId, username, client, and tenant owner.
    /// </summary>
    public class BotContextAccessor
    {
        public const string DefaultBotId = "vpnetiranbot";
        // AsyncLocal scopes state/payment/log resolution to the bot currently handling an update.
        private static readonly AsyncLocal<BotRuntimeContext> CurrentContext = new();

        public BotRuntimeContext Current => CurrentContext.Value;
        public static string CurrentBotId => CurrentContext.Value?.BotId ?? DefaultBotId;
        public static string CurrentBotUsername => CurrentContext.Value?.Username ?? DefaultBotId;
        public static string CurrentBotType => CurrentContext.Value?.Config?.Type ?? BotInstanceTypes.Owned;
        public static long? CurrentBotOwnerTelegramUserId => CurrentContext.Value?.Config?.OwnerTelegramUserId;

        /// <summary>
        /// Sets the current bot context for the lifetime of a using block.
        /// </summary>
        /// <param name="context">Bot runtime context for the update currently being processed.</param>
        /// <returns>An IDisposable that restores the previous context when disposed.</returns>
        public IDisposable Push(BotRuntimeContext context)
        {
            var previous = CurrentContext.Value;
            CurrentContext.Value = context;
            return new PopWhenDisposed(previous);
        }

        private sealed class PopWhenDisposed : IDisposable
        {
            private readonly BotRuntimeContext _previous;
            private bool _disposed;

            public PopWhenDisposed(BotRuntimeContext previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (_disposed)
                    return;

                CurrentContext.Value = _previous;
                _disposed = true;
            }
        }
    }

    /// <summary>
    /// Helper methods for BotInstanceConfig values shared by payment and persistence code.
    /// </summary>
    public static class BotInstanceConfigExtensions
    {
        /// <summary>
        /// Builds a Telegram deep-link for the current bot username.
        /// </summary>
        /// <param name="bot">Bot configuration that contains the Telegram username.</param>
        /// <param name="start">The payload passed to /start.</param>
        /// <returns>A t.me start URL scoped to the bot.</returns>
        public static string BuildTelegramStartUrl(this BotInstanceConfig bot, string start)
        {
            var username = string.IsNullOrWhiteSpace(bot?.Username)
                ? BotContextAccessor.DefaultBotId
                : bot.Username.Trim().TrimStart('@');

            return $"https://t.me/{username}?start={Uri.EscapeDataString(start ?? string.Empty)}";
        }

        /// <summary>
        /// Serializes a string collection for storing bot config arrays in users.db.
        /// Empty and whitespace-only entries are ignored.
        /// </summary>
        /// <param name="values">Values to persist.</param>
        /// <returns>A JSON array string.</returns>
        public static string SerializeStringArray(IEnumerable<string> values)
        {
            return JsonSerializer.Serialize(values?.Where(v => !string.IsNullOrWhiteSpace(v)).ToArray() ?? Array.Empty<string>());
        }
    }
}
