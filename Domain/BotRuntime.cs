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
        public static BotUserState FromUser(string botId, User user)
        {
            return new BotUserState
            {
                BotId = string.IsNullOrWhiteSpace(botId) ? BotContextAccessor.DefaultBotId : botId,
                TelegramUserId = user.Id,
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
        public User ToUser()
        {
            return new User
            {
                Id = TelegramUserId,
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
                AccountCounter = AccountCounter,
                PendingAccountCount = PendingAccountCount,
                PendingUserComment = PendingUserComment,
                LastFreeNationalAcc = LastFreeNationalAcc,
                LastFreeNormalAcc = LastFreeNormalAcc
            };
        }

        /// <summary>
        /// Applies only non-empty fields from a legacy User object.
        /// This preserves older flow behavior where SaveUserStatus receives partial state updates.
        /// </summary>
        /// <param name="user">Partial legacy state update.</param>
        public void ApplyPartial(User user)
        {
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
        public void Clear()
        {
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
