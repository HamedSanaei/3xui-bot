using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using System.Threading;

/// <summary>
/// Entity Framework context for <c>users.db</c>.
/// It stores bot-scoped conversation state, payment metadata, tenant storefront definitions,
/// tenant orders, tenant ledger rows, durable settlement-notification delivery, XUI volume-reminder cycles, cookies,
/// and other runtime data that must not live in <c>credentials.db</c>.
/// </summary>
/// <remarks>
/// Multi-instance support is implemented here by routing the legacy <see cref="User"/> state API
/// through <see cref="BotUserState"/> rows keyed by <c>BotId + TelegramUserId</c>.
/// Existing call sites can still call <see cref="SaveUserStatus"/>, <see cref="ClearUserStatus"/>,
/// <see cref="ResetUserStatus"/>, <see cref="GetUserStatus"/>, <see cref="IsUserReadyToCreate"/>, and
/// <see cref="IsUserReadyToUpdate"/>;
/// the current bot is resolved from <see cref="BotContextAccessor"/>.
/// </remarks>
public class UserDbContext : DbContext
{
    private static string _databasePath = "./Data/users.db";

    /// <summary>Immutable connection options reused by compatibility state-store calls; never a shared change tracker.</summary>
    private readonly DbContextOptions<UserDbContext> _operationOptions;

    /// <summary>
    /// Creates a users.db context that resolves its SQLite path from <see cref="ConfigureDatabasePath"/>.
    /// </summary>
    /// <remarks>This constructor is retained for explicitly owned legacy background operations.</remarks>
    public UserDbContext()
    {
    }

    /// <summary>
    /// Creates a users.db context with externally supplied options from <see cref="UserDbContextFactory"/>.
    /// </summary>
    /// <param name="options">Configured EF Core options pointing at the application users.db database.</param>
    /// <remarks>Referral and wallet-ledger operations use this constructor to own an independent change tracker.</remarks>
    public UserDbContext(DbContextOptions<UserDbContext> options)
        : base(options)
    {
        _operationOptions = options;
    }

    public DbSet<User> Users { get; set; }
    /// <summary>Private durable Telegram inputs and terminal deduplication receipts, isolated by runtime bot id.</summary>
    public DbSet<TelegramUpdateInboxEntry> TelegramUpdateInbox { get; set; }
    /// <summary>Private durable XUI creation identities that prevent a second addClient after a restart.</summary>
    public DbSet<XuiV3CreationOperation> XuiV3CreationOperations { get; set; }
    public DbSet<BotInstance> BotInstances { get; set; }
    public DbSet<BotUserState> BotUserStates { get; set; }
    // Tenant storefront state stays in users.db; credentials.db owns global profiles, balances, and wallet receipts.
    public DbSet<TenantBotOrder> TenantBotOrders { get; set; }
    public DbSet<TenantBotLedgerEntry> TenantBotLedgerEntries { get; set; }
    public DbSet<WalletLedgerEntry> WalletLedgerEntries { get; set; }
    /// <summary>Global immutable referral relationships shared by all owned bots.</summary>
    public DbSet<ReferralRelationship> ReferralRelationships { get; set; }
    /// <summary>Eligible owned-bot wallet payment events processed by the referral engine.</summary>
    public DbSet<ReferralPaymentEvent> ReferralPaymentEvents { get; set; }
    /// <summary>Retryable referral reward rows linked to exactly-once wallet mutations and ledger entries.</summary>
    public DbSet<ReferralReward> ReferralRewards { get; set; }
    public DbSet<TenantManualPaymentReceipt> TenantManualPaymentReceipts { get; set; }
    /// <summary>Durable outbox intents that relay tenant receipt photos to the Sales Assistant outside update lanes.</summary>
    public DbSet<TenantManualReceiptNotification> TenantManualReceiptNotifications { get; set; }
    /// <summary>Durable post-fulfillment Telegram delivery intents keyed by tenant order and notification kind.</summary>
    public DbSet<TenantOrderNotification> TenantOrderNotifications { get; set; }
    /// <summary>Persistent per-storefront funding episode and customer-attempt cooldown state.</summary>
    public DbSet<TenantStorefrontFundingAlertState> TenantStorefrontFundingAlertStates { get; set; }
    /// <summary>Durable owner alerts for underfunded storefront transitions and blocked customer attempts.</summary>
    public DbSet<TenantStorefrontFundingAlert> TenantStorefrontFundingAlerts { get; set; }
    /// <summary>
    /// Outbox rows for synchronizing successful XUI operations from the bot to the Gozargah website.
    /// </summary>
    /// <remarks>
    /// Rows are written to <c>users.db</c> after bot-side create, update, rename, or delete operations and are
    /// retried by the background worker until the website API accepts them or marks them as skipped.
    /// </remarks>
    public DbSet<GozargahSiteSyncEvent> GozargahSiteSyncEvents { get; set; }
    /// <summary>Durable idempotent XUI v3 account link-change operations scoped by panel and numeric client id.</summary>
    public DbSet<XuiV3LinkChangeOperation> XuiV3LinkChangeOperations { get; set; }
    /// <summary>Durable once-per-period delivery state for aggregate weekly usage reports.</summary>
    public DbSet<UsageReportDispatch> UsageReportDispatches { get; set; }
    /// <summary>
    /// Durable per-panel/client cycles and Telegram delivery claims for 80/90/99 percent traffic reminders.
    /// </summary>
    public DbSet<XuiV3VolumeReminderState> XuiV3VolumeReminderStates { get; set; }
    public DbSet<CookieData> Cookies { get; set; }
    public DbSet<SwapinoPaymentInfo> SwapinoPaymentInfos { get; set; }
    public DbSet<HooshPayPaymentInfo> HooshPayPaymentInfos { get; set; }
    /// <summary>Persisted Tetraminator invoices for owned wallet charges and tenant orders.</summary>
    public DbSet<TetraminatorPaymentInfo> TetraminatorPaymentInfos { get; set; }
    /// <summary>Persisted UniquePay invoices and polling/settlement audit state for owned and tenant payments.</summary>
    public DbSet<UniquePayPaymentInfo> UniquePayPaymentInfos { get; set; }
    public DbSet<AtlasPayPaymentInfo> AtlasPayPaymentInfos { get; set; }
    /// <summary>
    /// Durable, independently retried Telegram notifications created by first-time owned-wallet settlements.
    /// </summary>
    public DbSet<PaymentSettlementNotification> PaymentSettlementNotifications { get; set; }

    public DbSet<ZibalPaymentInfo> ZibalPaymentInfos { get; set; }

    /// <summary>
    /// Durable exactly-once records for XUI v3 renewal operations, keyed by confirmation session or tenant order.
    /// </summary>
    /// <remarks>
    /// Rows are written to <c>users.db</c> before the XUI mutation and survive process restarts. The unique
    /// OperationKey plus the atomic status/lease transitions make renewal mutation and settlement exactly-once.
    /// </remarks>
    public DbSet<XuiV3RenewalOperation> XuiV3RenewalOperations { get; set; }

    /// <summary>
    /// Current SQLite path used by this context.
    /// </summary>
    public static string DatabasePath => _databasePath;





    /// <summary>
    /// Configures the SQLite database path before the application creates or migrates the context.
    /// </summary>
    /// <param name="databasePath">Path to <c>users.db</c>. Empty values keep the current default path.</param>
    public static void ConfigureDatabasePath(string databasePath)
    {
        if (!string.IsNullOrWhiteSpace(databasePath))
            _databasePath = databasePath;
    }

    /// <summary>
    /// Configures SQLite when the context was created without externally supplied options.
    /// </summary>
    /// <param name="optionsBuilder">EF Core options builder for this context instance.</param>
    /// <remarks>Conversation helpers delegate to a factory-backed store using BotId plus TelegramUserId; no database-wide semaphore or shared tracker is retained.
    /// Debt transfer intent is owner-global, retained for audit, and has a filtered unique index allowing only one pending transfer per owner.</remarks>
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
            optionsBuilder.UseSqlite(SqliteOperation.ConnectionString(_databasePath));
    }

    /// <summary>
    /// Defines the <c>users.db</c> schema, indexes, and field limits for payments, bot instances,
    /// tenant orders and renewal-category evidence, ledgers, bot-scoped conversation state, settlement-notification outbox delivery, idempotent
    /// scheduled-report delivery, and durable per-client XUI volume-reminder cycles and claims.
    /// </summary>
    /// <param name="modelBuilder">EF Core model builder used by migrations and runtime metadata.</param>
    /// <remarks>Conversation helpers delegate to a factory-backed store using BotId plus TelegramUserId; no database-wide semaphore or shared tracker is retained.</remarks>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Exact inbox links complement the existing bot/user fallback for historical recovery records.
        modelBuilder.Entity<XuiV3RenewalOperation>().HasIndex(x => x.InboxSequence);
        modelBuilder.Entity<XuiV3LinkChangeOperation>().HasIndex(x => x.InboxSequence);
        modelBuilder.Entity<XuiV3CreationOperation>(entity =>
        {
            entity.HasKey(x => x.OperationKey);
            entity.Property(x => x.OperationKey).HasMaxLength(240);
            entity.HasIndex(x => new { x.TelegramUserId, x.CreatedAtUtc });
            entity.HasIndex(x => x.InboxSequence);
        });
        modelBuilder.Entity<TelegramUpdateInboxEntry>(entity =>
        {
            entity.HasKey(x => x.Sequence);
            entity.HasIndex(x => new { x.BotId, x.UpdateId }).IsUnique();
            entity.HasIndex(x => new { x.BotId, x.TelegramUserId, x.Sequence });
            entity.HasIndex(x => new { x.Status, x.Sequence });
            entity.Property(x => x.BotId).IsRequired();
            entity.Property(x => x.Status).IsRequired();
        });
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<SwapinoPaymentInfo>(entity =>
        {
            entity.ToTable("SwapinoPaymentInfos");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.OrderId).IsRequired().HasMaxLength(120);
            entity.HasIndex(x => x.OrderId).IsUnique();
            entity.HasIndex(x => x.ParentOrderId);
            entity.HasIndex(x => x.PaymentId);
            entity.HasIndex(x => x.TelegramUserId);
            entity.HasIndex(x => x.ChatId);
            entity.HasIndex(x => x.BotId);
            entity.HasIndex(x => x.PaymentPurpose);
            entity.HasIndex(x => x.TenantBotOrderId);
            entity.Property(x => x.BotId).HasMaxLength(64);
            entity.Property(x => x.BotUsername).HasMaxLength(128);
            entity.Property(x => x.PaymentPurpose).HasMaxLength(64);
            entity.Property(x => x.BaseCurrency).HasMaxLength(32);
            entity.Property(x => x.PayCurrency).HasMaxLength(32);
            entity.Property(x => x.InvoiceId).HasMaxLength(120);
            entity.Property(x => x.PaymentId).HasMaxLength(120);
            entity.Property(x => x.PaymentStatus).HasMaxLength(64);
            entity.Property(x => x.PayAddress).HasMaxLength(256);
        });

        modelBuilder.Entity<HooshPayPaymentInfo>(entity =>
        {
            entity.ToTable("HooshPayPaymentInfos");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.OrderId).IsRequired().HasMaxLength(120);
            entity.HasIndex(x => x.OrderId).IsUnique();
            entity.HasIndex(x => x.InvoiceUid);
            entity.HasIndex(x => x.TelegramUserId);
            entity.HasIndex(x => x.ChatId);
            entity.Property(x => x.InvoiceUid).HasMaxLength(120);
            entity.Property(x => x.BotId).HasMaxLength(64);
            entity.Property(x => x.BotUsername).HasMaxLength(128);
            entity.HasIndex(x => x.BotId);
            // PaymentPurpose is the guard that prevents tenant orders from being settled as wallet charges.
            entity.Property(x => x.PaymentPurpose).HasMaxLength(64);
            entity.HasIndex(x => x.PaymentPurpose);
            entity.HasIndex(x => x.TenantBotOrderId);
            entity.HasIndex(x => x.TenantOwnerTelegramUserId);
            entity.Property(x => x.FeeMode).HasMaxLength(32);
            entity.Property(x => x.PaymentStatus).HasMaxLength(64);
            entity.Property(x => x.TrackingCode).HasMaxLength(120);
        });

        modelBuilder.Entity<TetraminatorPaymentInfo>(entity =>
        {
            entity.ToTable("TetraminatorPaymentInfos");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.OrderId).IsRequired().HasMaxLength(160);
            entity.Property(x => x.PayId).HasMaxLength(160);
            entity.Property(x => x.PaymentStatus).HasMaxLength(64);
            entity.Property(x => x.BotId).HasMaxLength(64);
            entity.Property(x => x.BotUsername).HasMaxLength(128);
            entity.Property(x => x.PaymentPurpose).HasMaxLength(64);
            entity.Property(x => x.ErrorCode).HasMaxLength(128);
            entity.HasIndex(x => x.OrderId).IsUnique();
            entity.HasIndex(x => x.PayId).IsUnique().HasFilter("\"PayId\" IS NOT NULL");
            entity.HasIndex(x => x.TelegramUserId);
            entity.HasIndex(x => x.BotId);
            entity.HasIndex(x => x.PaymentPurpose);
            entity.HasIndex(x => x.TenantBotOrderId);
            entity.HasIndex(x => x.TenantOwnerTelegramUserId);
        });

        modelBuilder.Entity<UniquePayPaymentInfo>(entity =>
        {
            entity.ToTable("UniquePayPaymentInfos");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.HashId).IsRequired().HasMaxLength(180);
            entity.Property(x => x.RefId).HasMaxLength(180);
            entity.Property(x => x.Currency).HasMaxLength(16);
            entity.Property(x => x.FeePayer).HasMaxLength(32);
            entity.Property(x => x.PaymentStatus).HasMaxLength(64);
            entity.Property(x => x.CreationState)
                .IsRequired()
                .HasMaxLength(32)
                .HasDefaultValue(UniquePayCreationStates.Ambiguous);
            entity.Property(x => x.CreationErrorCode).HasMaxLength(128);
            entity.Property(x => x.BotId).HasMaxLength(64);
            entity.Property(x => x.BotUsername).HasMaxLength(128);
            entity.Property(x => x.PaymentPurpose).HasMaxLength(64);
            entity.Property(x => x.ErrorCode).HasMaxLength(128);
            entity.Property(x => x.SettlementState).IsRequired().HasMaxLength(32).HasDefaultValue(UniquePaySettlementStates.Pending);
            entity.Property(x => x.SettlementAttemptId).HasMaxLength(64);
            entity.HasIndex(x => x.HashId).IsUnique();
            entity.HasIndex(x => x.RefId).IsUnique().HasFilter("\"RefId\" IS NOT NULL");
            entity.HasIndex(x => x.TelegramUserId);
            entity.HasIndex(x => x.BotId);
            entity.HasIndex(x => x.PaymentPurpose);
            entity.HasIndex(x => x.TenantBotOrderId);
            entity.HasIndex(x => x.TenantOwnerTelegramUserId);
            entity.HasIndex(x => new { x.CreationState, x.NextInquiryAtUtc });
            entity.HasIndex(x => new { x.IsAddedToBalance, x.SettlementState, x.NextInquiryAtUtc });
        });

        modelBuilder.Entity<AtlasPayPaymentInfo>(entity =>
        {
            entity.ToTable("AtlasPayPaymentInfos"); entity.HasKey(x => x.Id); entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.MerchantOrderRef).IsRequired().HasMaxLength(180);
            entity.Property(x => x.TrackingCode).HasMaxLength(180); entity.Property(x => x.CustomerStartLink).HasMaxLength(1024);
            entity.Property(x => x.CardNumberMasked).HasMaxLength(64); entity.Property(x => x.ProviderStatus).HasMaxLength(64);
            entity.Property(x => x.BotId).HasMaxLength(64); entity.Property(x => x.BotUsername).HasMaxLength(128);
            entity.Property(x => x.PaymentPurpose).HasMaxLength(64); entity.Property(x => x.CreationState).IsRequired().HasMaxLength(32).HasDefaultValue(AtlasPayCreationStates.Ambiguous);
            entity.Property(x => x.CreationErrorCode).HasMaxLength(128); entity.Property(x => x.SettlementState).IsRequired().HasMaxLength(32).HasDefaultValue(AtlasPaySettlementStates.Pending);
            entity.Property(x => x.SettlementAttemptId).HasMaxLength(64); entity.Property(x => x.ErrorCode).HasMaxLength(128); entity.Property(x => x.ErrorMessage).HasMaxLength(1000);
            entity.HasIndex(x => x.MerchantOrderRef).IsUnique(); entity.HasIndex(x => x.ProviderOrderId).IsUnique().HasFilter("\"ProviderOrderId\" IS NOT NULL");
            entity.HasIndex(x => x.TrackingCode); entity.HasIndex(x => x.TelegramUserId); entity.HasIndex(x => x.BotId);
            entity.HasIndex(x => x.TenantBotOrderId); entity.HasIndex(x => x.ProviderStatus); entity.HasIndex(x => new { x.SettlementState, x.NextInquiryAtUtc });
        });

        modelBuilder.Entity<PaymentSettlementNotification>(entity =>
        {
            entity.ToTable("PaymentSettlementNotifications");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.NotificationKey).IsRequired().HasMaxLength(220);
            entity.Property(x => x.Provider).IsRequired().HasMaxLength(48);
            entity.Property(x => x.BotId).HasMaxLength(64);
            entity.Property(x => x.MessageText).IsRequired().HasMaxLength(4096);
            entity.Property(x => x.Status)
                .IsRequired()
                .HasMaxLength(32)
                .HasDefaultValue(PaymentSettlementNotificationStatuses.Pending);
            entity.Property(x => x.ClaimToken).HasMaxLength(64);
            entity.Property(x => x.LastError).HasMaxLength(512);
            entity.HasIndex(x => x.NotificationKey).IsUnique();
            entity.HasIndex(x => new { x.Status, x.NextAttemptAtUtc, x.LeaseUntilUtc });
            entity.HasIndex(x => x.TelegramUserId);
            entity.HasIndex(x => x.BotId);
        });

        modelBuilder.Entity<BotInstance>(entity =>
        {
            entity.ToTable("BotInstances");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.Property(x => x.Username).HasMaxLength(128);
            entity.Property(x => x.Token).HasMaxLength(512);
            entity.Property(x => x.BrandName).HasMaxLength(128);
            entity.Property(x => x.Type).HasMaxLength(32);
            entity.Property(x => x.SupportAccount).HasMaxLength(128);
            entity.Property(x => x.LoggerChannel).HasMaxLength(128);
            entity.Property(x => x.BackupChannel).HasMaxLength(128);
            entity.Property(x => x.TenantCardNumber).HasMaxLength(64);
            entity.Property(x => x.TenantCardHolderName).HasMaxLength(128);
            entity.Property(x => x.TenantTutorialsJson);
            entity.Property(x => x.TenantTetraminatorEnabled).HasDefaultValue(true);
            entity.Property(x => x.TenantUniquePayEnabled).HasDefaultValue(true);
            entity.Property(x => x.TenantAtlasPayEnabled).HasDefaultValue(true);
            entity.Property(x => x.TenantOwnerNotificationBotId).HasMaxLength(64);
            entity.HasIndex(x => x.Username);
            entity.HasIndex(x => x.OwnerTelegramUserId);
            // Existing tenant ids stay unchanged; the owner/number pair is the stable management identity.
            entity.HasIndex(x => new { x.OwnerTelegramUserId, x.TenantStoreNumber }).IsUnique();
            entity.HasIndex(x => x.TelegramBotId).IsUnique();
        });

        modelBuilder.Entity<SiteWalletDebitOperation>().HasKey(x => x.Id);
        modelBuilder.Entity<TenantDebtTransfer>().HasKey(x => x.Id);
        modelBuilder.Entity<TenantDebtTransfer>().HasIndex(x => x.OwnerTelegramUserId)
            .IsUnique().HasFilter("\"Status\" = 'pending'");
        modelBuilder.Entity<SiteWalletDebitOperation>().HasIndex(x => new { x.OwnerTelegramUserId, x.Status });
        modelBuilder.Entity<TenantWalletRoute>().HasKey(x => x.Id);
        modelBuilder.Entity<TenantWalletRoute>().Property(x => x.Id).ValueGeneratedNever();

        modelBuilder.Entity<TenantBotOrder>(entity =>
        {
            entity.ToTable("TenantBotOrders");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.OrderId).IsRequired().HasMaxLength(140);
            entity.HasIndex(x => x.OrderId).IsUnique();
            // These indexes support IPN lookup, owner reporting, and customer manual payment checks.
            entity.Property(x => x.TenantBotId).HasMaxLength(64);
            entity.Property(x => x.TenantBotUsername).HasMaxLength(128);
            entity.Property(x => x.OrderKind).HasMaxLength(32);
            entity.Property(x => x.TargetAccountEmail).HasMaxLength(160);
            entity.Property(x => x.TargetAccountUuid).HasMaxLength(64);
            entity.Property(x => x.RenewalServiceResolutionMode).HasMaxLength(48);
            entity.Property(x => x.ServiceKey).HasMaxLength(64);
            entity.Property(x => x.DurationKey).HasMaxLength(64);
            entity.Property(x => x.UnlimitedPlanKey).HasMaxLength(64);
            entity.Property(x => x.PaymentProvider).HasMaxLength(64);
            entity.Property(x => x.PaymentStatus).HasMaxLength(64);
            entity.Property(x => x.FulfillmentSource).HasMaxLength(64);
            entity.Property(x => x.HooshPayInvoiceUid).HasMaxLength(120);
            entity.HasIndex(x => x.ManualReceiptId);
            entity.HasIndex(x => x.NowPaymentsPaymentInfoId);
            entity.HasIndex(x => x.OrderKind);
            entity.HasIndex(x => x.TenantBotId);
            entity.HasIndex(x => x.OwnerTelegramUserId);
            entity.HasIndex(x => x.CustomerTelegramUserId);
            entity.HasIndex(x => x.HooshPayPaymentInfoId);
            entity.HasIndex(x => x.TetraminatorPaymentInfoId);
            entity.HasIndex(x => x.UniquePayPaymentInfoId);
            entity.HasIndex(x => x.AtlasPayPaymentInfoId);
        });

        modelBuilder.Entity<TenantBotLedgerEntry>(entity =>
        {
            entity.ToTable("TenantBotLedgerEntries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.TenantBotId).HasMaxLength(64);
            entity.Property(x => x.TenantBotUsername).HasMaxLength(128);
            entity.Property(x => x.OrderId).HasMaxLength(140);
            entity.HasIndex(x => x.TenantBotId);
            entity.HasIndex(x => x.OwnerTelegramUserId);
            entity.HasIndex(x => x.CustomerTelegramUserId);
            entity.HasIndex(x => x.TenantBotOrderId).IsUnique();
        });

        modelBuilder.Entity<WalletLedgerEntry>(entity =>
        {
            entity.ToTable("WalletLedgerEntries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.BotId).HasMaxLength(64);
            entity.Property(x => x.BotUsername).HasMaxLength(128);
            entity.Property(x => x.BotType).HasMaxLength(32);
            entity.Property(x => x.Direction).HasMaxLength(16);
            entity.Property(x => x.Reason).HasMaxLength(64);
            entity.Property(x => x.Provider).HasMaxLength(64);
            entity.Property(x => x.ReferenceType).HasMaxLength(64);
            entity.Property(x => x.ReferenceId).HasMaxLength(128);
            entity.Property(x => x.OrderId).HasMaxLength(140);
            entity.Property(x => x.IdempotencyKey).HasMaxLength(240);
            entity.HasIndex(x => x.TelegramUserId);
            entity.HasIndex(x => x.OwnerTelegramUserId);
            entity.HasIndex(x => x.BotId);
            entity.HasIndex(x => x.OrderId);
            entity.HasIndex(x => x.CreatedAtUtc);
            entity.HasIndex(x => x.IdempotencyKey)
                .IsUnique()
                .HasFilter("\"IdempotencyKey\" IS NOT NULL");
        });

        modelBuilder.Entity<ReferralRelationship>(entity =>
        {
            entity.ToTable("ReferralRelationships", table =>
                table.HasCheckConstraint(
                    "CK_ReferralRelationships_NoSelfReferral",
                    "\"ReferrerTelegramUserId\" <> \"ReferredTelegramUserId\""));
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.AttributionBotId).IsRequired().HasMaxLength(64);
            entity.Property(x => x.ReferralCode).IsRequired().HasMaxLength(64);
            entity.HasIndex(x => x.ReferredTelegramUserId).IsUnique();
            entity.HasIndex(x => x.ReferrerTelegramUserId);
            entity.HasIndex(x => x.AttributionBotId);
        });

        modelBuilder.Entity<ReferralPaymentEvent>(entity =>
        {
            entity.ToTable("ReferralPaymentEvents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.SourcePaymentKey).IsRequired().HasMaxLength(240);
            entity.Property(x => x.Provider).IsRequired().HasMaxLength(48);
            entity.Property(x => x.PaymentType).IsRequired().HasMaxLength(64);
            entity.Property(x => x.ProviderPaymentId).IsRequired().HasMaxLength(160);
            entity.Property(x => x.BotId).IsRequired().HasMaxLength(64);
            entity.Property(x => x.Status).IsRequired().HasMaxLength(24);
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.HasIndex(x => x.SourcePaymentKey).IsUnique();
            entity.HasIndex(x => new { x.ReferredTelegramUserId, x.IsFirstEligiblePayment })
                .IsUnique()
                .HasFilter("\"IsFirstEligiblePayment\" = 1");
            entity.HasIndex(x => new { x.Status, x.UpdatedAtUtc });
            entity.HasIndex(x => x.ReferralRelationshipId);
        });

        modelBuilder.Entity<ReferralReward>(entity =>
        {
            entity.ToTable("ReferralRewards");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.SourcePaymentKey).IsRequired().HasMaxLength(240);
            entity.Property(x => x.BotId).IsRequired().HasMaxLength(64);
            entity.Property(x => x.RewardKind).IsRequired().HasMaxLength(64);
            entity.Property(x => x.Status).IsRequired().HasMaxLength(24);
            entity.Property(x => x.WalletMutationKey).IsRequired().HasMaxLength(240);
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.Property(x => x.RewardPercentSnapshot).HasPrecision(9, 4);
            entity.HasIndex(x => new { x.SourcePaymentKey, x.BeneficiaryTelegramUserId, x.RewardKind }).IsUnique();
            entity.HasIndex(x => x.WalletMutationKey).IsUnique();
            entity.HasIndex(x => new { x.BeneficiaryTelegramUserId, x.Status });
            entity.HasIndex(x => x.ReferralPaymentEventId);
            entity.HasIndex(x => x.ReferralRelationshipId);
        });

        modelBuilder.Entity<TenantManualPaymentReceipt>(entity =>
        {
            entity.ToTable("TenantManualPaymentReceipts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.OrderId).HasMaxLength(140);
            entity.Property(x => x.TenantBotId).HasMaxLength(64);
            entity.Property(x => x.TenantBotUsername).HasMaxLength(128);
            entity.Property(x => x.PhotoFileId).HasMaxLength(256);
            entity.Property(x => x.Status).HasMaxLength(64);
            entity.HasIndex(x => x.TenantBotOrderId);
            entity.HasIndex(x => x.TenantBotId);
            entity.HasIndex(x => x.OwnerTelegramUserId);
            entity.HasIndex(x => x.CustomerTelegramUserId);
            entity.HasIndex(x => x.Status);
        });

        modelBuilder.Entity<TenantManualReceiptNotification>(entity =>
        {
            entity.ToTable("TenantManualReceiptNotifications");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.Status).IsRequired().HasMaxLength(32);
            entity.Property(x => x.ClaimToken).HasMaxLength(64);
            entity.Property(x => x.LastError).HasMaxLength(256);
            entity.HasIndex(x => x.ReceiptId).IsUnique();
            entity.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
            entity.HasIndex(x => x.LeaseUntilUtc);
        });

        modelBuilder.Entity<TenantOrderNotification>(entity =>
        {
            entity.ToTable("TenantOrderNotifications");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.Kind).IsRequired().HasMaxLength(64);
            entity.Property(x => x.Status).IsRequired().HasMaxLength(32);
            entity.Property(x => x.ClaimToken).HasMaxLength(64);
            entity.Property(x => x.LastError).HasMaxLength(256);
            entity.HasIndex(x => new { x.TenantBotOrderId, x.Kind }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
            entity.HasIndex(x => x.LeaseUntilUtc);
            // Cleanup scans delivered rows by delivered time while other statuses are never candidates.
            entity.HasIndex(x => new { x.Status, x.DeliveredAtUtc });
        });

        modelBuilder.Entity<TenantStorefrontFundingAlertState>(entity =>
        {
            entity.ToTable("TenantStorefrontFundingAlertStates");
            entity.HasKey(x => x.TenantBotId);
            entity.Property(x => x.TenantBotId).HasMaxLength(64).ValueGeneratedNever();
        });

        modelBuilder.Entity<TenantStorefrontFundingAlert>(entity =>
        {
            entity.ToTable("TenantStorefrontFundingAlerts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.BusinessKey).IsRequired().HasMaxLength(180);
            entity.Property(x => x.TenantBotId).IsRequired().HasMaxLength(64);
            entity.Property(x => x.TenantBotUsername).HasMaxLength(128);
            entity.Property(x => x.Kind).IsRequired().HasMaxLength(48);
            entity.Property(x => x.Status).IsRequired().HasMaxLength(32);
            entity.Property(x => x.ClaimToken).HasMaxLength(64);
            entity.Property(x => x.LastError).HasMaxLength(256);
            entity.HasIndex(x => x.BusinessKey).IsUnique();
            entity.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
            entity.HasIndex(x => x.LeaseUntilUtc);
            entity.HasIndex(x => new { x.TenantBotId, x.CreatedAtUtc });
            // Supports the bounded delivered-history cleanup without scanning every row of the outbox.
            entity.HasIndex(x => new { x.Status, x.DeliveredAtUtc });
        });
        modelBuilder.Entity<GozargahSiteSyncEvent>(entity =>
        {
            entity.ToTable("GozargahSiteSyncEvents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.BotId).HasMaxLength(64);
            entity.Property(x => x.TenantBotId).HasMaxLength(64);
            entity.Property(x => x.Operation).HasMaxLength(32);
            entity.Property(x => x.Status).HasMaxLength(32);
            entity.Property(x => x.Email).HasMaxLength(160);
            entity.Property(x => x.PreviousEmail).HasMaxLength(160);
            entity.Property(x => x.Uuid).HasMaxLength(64);
            entity.Property(x => x.SubId).HasMaxLength(160);
            entity.Property(x => x.SiteOrderId).HasMaxLength(64);
            entity.HasIndex(x => x.BotId);
            entity.HasIndex(x => x.TenantBotId);
            entity.HasIndex(x => x.TelegramUserId);
            entity.HasIndex(x => x.OwnerTelegramUserId);
            entity.HasIndex(x => x.BuyerTelegramUserId);
            entity.HasIndex(x => x.Email);
            entity.HasIndex(x => x.Uuid);
            entity.HasIndex(x => x.SubId);
            entity.HasIndex(x => x.Status);
            entity.HasIndex(x => new { x.Operation, x.Email, x.PreviousEmail, x.Uuid, x.SubId });
        });

        modelBuilder.Entity<XuiV3LinkChangeOperation>(entity =>
        {
            entity.ToTable("XuiV3LinkChangeOperations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.OperationKey).IsRequired().HasMaxLength(40);
            entity.Property(x => x.PanelKey).IsRequired().HasMaxLength(64);
            entity.Property(x => x.BotId).IsRequired().HasMaxLength(64);
            entity.Property(x => x.BotUsername).HasMaxLength(128);
            entity.Property(x => x.BotType).HasMaxLength(32);
            entity.Property(x => x.Source).HasMaxLength(16);
            entity.Property(x => x.Status).IsRequired().HasMaxLength(32);
            entity.Property(x => x.Stage).HasMaxLength(64);
            entity.Property(x => x.OldEmail).HasMaxLength(160);
            entity.Property(x => x.OldUuid).HasMaxLength(64);
            entity.Property(x => x.OldSubId).HasMaxLength(160);
            entity.Property(x => x.NewEmail).HasMaxLength(160);
            entity.Property(x => x.NewUuid).HasMaxLength(64);
            entity.Property(x => x.NewSubId).HasMaxLength(160);
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.HasIndex(x => x.OperationKey).IsUnique();
            entity.HasIndex(x => new { x.Status, x.NextAttemptAtUtc });
            entity.HasIndex(x => new { x.TelegramUserId, x.CreatedAtUtc });
            // SQLite enforces one active saga per physical panel client across all owned and tenant bots.
            entity.HasIndex(x => new { x.PanelKey, x.ClientId })
                .IsUnique()
                .HasFilter("\"Status\" IN ('awaiting_confirmation','processing','recovery_pending','manual_review')");
        });

        modelBuilder.Entity<UsageReportDispatch>(entity =>
        {
            entity.ToTable("UsageReportDispatches");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.ReportKey).IsRequired().HasMaxLength(64);
            entity.Property(x => x.Status).IsRequired().HasMaxLength(32);
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.HasIndex(x => x.ReportKey).IsUnique();
            entity.HasIndex(x => new { x.Status, x.LeaseUntilUtc });
            entity.HasIndex(x => x.PeriodEndUtc);
        });

        modelBuilder.Entity<XuiV3VolumeReminderState>(entity =>
        {
            entity.ToTable("XuiV3VolumeReminderStates");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.PanelKey).IsRequired().HasMaxLength(64);
            entity.Property(x => x.Email).IsRequired().HasMaxLength(160);
            entity.Property(x => x.BotId).IsRequired().HasMaxLength(64);
            entity.Property(x => x.DeliveryStatus).IsRequired().HasMaxLength(32);
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.Property(x => x.LastEligibilityCode).HasMaxLength(48);
            entity.Property(x => x.LastEligibilitySummary).HasMaxLength(1000);
            // One durable cycle row represents one physical numeric client on one credential-free panel identity.
            entity.HasIndex(x => new { x.PanelKey, x.ClientId }).IsUnique();
            // Cleanup is panel-scoped and ordered by observation age; query-plan audit showed this avoids a temp sort.
            entity.HasIndex(x => new { x.PanelKey, x.LastObservedAtUtc });
        });

        modelBuilder.Entity<ZibalPaymentInfo>(entity =>
        {
            entity.HasIndex(x => x.BotId);
            entity.Property(x => x.BotId).HasMaxLength(64);
            entity.Property(x => x.BotUsername).HasMaxLength(128);
        });

        modelBuilder.Entity<XuiV3RenewalOperation>(entity =>
        {
            entity.ToTable("XuiV3RenewalOperations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).ValueGeneratedOnAdd();
            entity.Property(x => x.OperationKey).IsRequired().HasMaxLength(180);
            entity.Property(x => x.OperationId).IsRequired().HasMaxLength(64);
            entity.Property(x => x.BotId).HasMaxLength(64);
            entity.Property(x => x.TenantBotId).HasMaxLength(64);
            entity.Property(x => x.TenantBotOrderId).HasMaxLength(140);
            entity.Property(x => x.TargetEmail).HasMaxLength(160);
            entity.Property(x => x.TargetUuid).HasMaxLength(64);
            entity.Property(x => x.NormalizedTargetEmail).HasMaxLength(160);
            entity.Property(x => x.NormalizedTargetUuid).HasMaxLength(64);
            entity.Property(x => x.AccountLockKey).HasMaxLength(240);
            entity.Property(x => x.ExpectedInboundIdsJson).HasMaxLength(2000);
            entity.Property(x => x.PreMutationSnapshotJson).HasMaxLength(8000);
            entity.Property(x => x.ServiceKey).HasMaxLength(64);
            entity.Property(x => x.PaymentMethod).HasMaxLength(64);
            entity.Property(x => x.Status).IsRequired().HasMaxLength(32);
            entity.Property(x => x.SettlementStatus).IsRequired().HasMaxLength(32);
            entity.Property(x => x.ClaimToken).HasMaxLength(40);
            entity.Property(x => x.RecoveryClaimToken).HasMaxLength(40);
            entity.Property(x => x.LastError).HasMaxLength(2000);
            entity.Property(x => x.LastComparisonOutcome).HasMaxLength(40);
            entity.Property(x => x.LastMismatchSummary).HasMaxLength(1000);
            // The unique key is the database-level duplicate guard for repeated confirmations.
            entity.HasIndex(x => x.OperationKey).IsUnique();
            entity.HasIndex(x => x.TenantBotOrderId)
                .IsUnique()
                .HasFilter("\"TenantBotOrderId\" IS NOT NULL");
            entity.HasIndex(x => x.BotId);
            entity.HasIndex(x => x.TelegramUserId);
            entity.HasIndex(x => new { x.Status, x.LeaseUntilUtc });
            entity.HasIndex(x => new { x.SettlementStatus, x.SettlementStartedAtUtc });
            // A nullable partial unique key is held for the entire mutation-plus-settlement lifecycle. Clearing it is
            // the only operation that permits another renewal for the same exact panel account.
            entity.HasIndex(x => x.AccountLockKey)
                .IsUnique()
                .HasFilter("\"AccountLockKey\" IS NOT NULL");
            entity.HasIndex(x => new { x.NormalizedTargetUuid, x.Status, x.SettlementStatus });
            entity.HasIndex(x => new { x.NormalizedTargetEmail, x.Status, x.SettlementStatus });
            entity.HasIndex(x => new { x.Status, x.NextReconcileAtUtc, x.RecoveryLeaseUntilUtc });
        });

        modelBuilder.Entity<BotUserState>(entity =>
        {
            entity.ToTable("BotUserStates");
            // Conversation state is isolated per bot so the same user can use several brands safely.
            entity.HasKey(x => new { x.BotId, x.TelegramUserId });
            entity.Property(x => x.BotId).HasMaxLength(64);
            entity.Property(x => x.PaymentMethod).HasMaxLength(64);
            entity.Property(x => x.RenewTargetUuid).HasMaxLength(64);
            entity.Property(x => x.RenewalSessionId).HasMaxLength(64);
            entity.Property(x => x.RenewalServiceResolutionMode).HasMaxLength(48);
            entity.HasIndex(x => x.TelegramUserId);
            entity.HasIndex(x => x.Flow);
        });
    }


    /// <summary>Creates a factory-backed compatibility store; it never reuses this context's tracker.</summary>
    private UserStateStore StateStore => new(new UserDbContextFactory(_operationOptions ??
        new DbContextOptionsBuilder<UserDbContext>().UseSqlite(SqliteOperation.ConnectionString(_databasePath)).Options));

    /// <summary>Applies partial state for the active bot and Telegram user through an isolated context.</summary>
    /// <param name="user">Detached state; null fields preserve existing values.</param>
    /// <returns>A task completing after persistence.</returns>
    /// <remarks>Conversation helpers delegate to a factory-backed store using BotId plus TelegramUserId; no database-wide semaphore or shared tracker is retained.</remarks>
    public Task SaveUserStatus(User user) => StateStore.SaveUserStatus(user);

    /// <summary>Clears only the active bot's transient state for a Telegram user.</summary>
    /// <param name="user">Detached snapshot identifying the Telegram user.</param>
    /// <returns>A task completing after persistence.</returns>
    /// <remarks>Conversation helpers delegate to a factory-backed store using BotId plus TelegramUserId; no database-wide semaphore or shared tracker is retained.</remarks>
    public Task ClearUserStatus(User user) => StateStore.ClearUserStatus(user);

    /// <summary>Atomically clears and replaces the active bot's transient state.</summary>
    /// <param name="user">Detached replacement snapshot identifying the Telegram user.</param>
    /// <returns>A task completing after the short transaction commits.</returns>
    /// <remarks>Conversation helpers delegate to a factory-backed store using BotId plus TelegramUserId; no database-wide semaphore or shared tracker is retained.</remarks>
    public Task ResetUserStatus(User user) => StateStore.ResetUserStatus(user);

    /// <summary>Checks legacy creation prerequisites in the active bot without tracking entities.</summary>
    /// <param name="teluserid">Telegram sender id in the active bot.</param>
    /// <returns>True when required creation state is present.</returns>
    /// <remarks>Conversation helpers delegate to a factory-backed store using BotId plus TelegramUserId; no database-wide semaphore or shared tracker is retained.</remarks>
    public Task<bool> IsUserReadyToCreate(long teluserid) => StateStore.IsUserReadyToCreate(teluserid);

    /// <summary>Checks legacy renewal prerequisites in the active bot without tracking entities.</summary>
    /// <param name="teluserid">Telegram sender id in the active bot.</param>
    /// <returns>True when required renewal state is present.</returns>
    /// <remarks>Conversation helpers delegate to a factory-backed store using BotId plus TelegramUserId; no database-wide semaphore or shared tracker is retained.</remarks>
    public Task<bool> IsUserReadyToUpdate(long teluserid) => StateStore.IsUserReadyToUpdate(teluserid);

    /// <summary>Reads a detached conversation snapshot for the active bot and Telegram user.</summary>
    /// <param name="userId">Telegram sender id in the active bot.</param>
    /// <returns>A detached state or an empty snapshot; never a tracked entity.</returns>
    /// <remarks>Conversation helpers delegate to a factory-backed store using BotId plus TelegramUserId; no database-wide semaphore or shared tracker is retained.</remarks>
    public Task<User> GetUserStatus(long userId) => StateStore.GetUserStatus(userId);

}

/// <summary>
/// Creates independent users.db contexts for financial, referral, and durable background-state operations that may
/// run concurrently.
/// </summary>
/// <remarks>
/// Conversation stores and financial/background operations use independent contexts. Legacy coordinating services
/// receive a disposable context for their logical execution scope; no application singleton retains a live tracker.
/// </remarks>
public sealed class UserDbContextFactory
{
    /// <summary>Immutable EF options reused to create independent contexts for the same migrated users.db file.</summary>
    private readonly DbContextOptions<UserDbContext> _options;

    /// <summary>
    /// Creates a factory from fully configured users.db options.
    /// </summary>
    /// <param name="options">EF Core SQLite options for the same users.db file migrated at application startup.</param>
    public UserDbContextFactory(DbContextOptions<UserDbContext> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>
    /// Creates a new users.db context owned by the caller.
    /// </summary>
    /// <returns>A new disposable context with an independent change tracker.</returns>
    /// <remarks>Callers must dispose the returned context after completing one logical operation.</remarks>
    /// <example>
    /// <code>
    /// await using var context = factory.CreateDbContext();
    /// var rewards = await context.ReferralRewards.ToListAsync(cancellationToken);
    /// </code>
    /// </example>
    public UserDbContext CreateDbContext()
    {
        return new UserDbContext(_options);
    }
}
