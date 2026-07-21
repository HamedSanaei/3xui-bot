using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using System.Threading;

/// <summary>
/// Entity Framework context for <c>users.db</c>.
/// It stores bot-scoped conversation state, payment metadata, tenant storefront definitions,
/// tenant orders, tenant ledger rows, cookies, and other runtime data that must not live in <c>credentials.db</c>.
/// </summary>
/// <remarks>
/// Multi-instance support is implemented here by routing the legacy <see cref="User"/> state API
/// through <see cref="BotUserState"/> rows keyed by <c>BotId + TelegramUserId</c>.
/// Existing call sites can still call <see cref="SaveUserStatus"/>, <see cref="ClearUserStatus"/>,
/// <see cref="GetUserStatus"/>, <see cref="IsUserReadyToCreate"/>, and <see cref="IsUserReadyToUpdate"/>;
/// the current bot is resolved from <see cref="BotContextAccessor"/>.
/// </remarks>
public class UserDbContext : DbContext
{
    private static string _databasePath = "./Data/users.db";

    /// <summary>
    /// Serializes legacy state-helper calls made through the singleton <see cref="UserDbContext"/> service.
    /// </summary>
    /// <remarks>
    /// Most modern code should use scoped/per-operation contexts, but the legacy bot state API is still injected as
    /// a singleton. This gate prevents overlapping EF Core operations when several bot receivers touch those helper
    /// methods at the same time.
    /// </remarks>
    private readonly SemaphoreSlim _dbGate = new(1, 1);

    /// <summary>
    /// Creates a users.db context that resolves its SQLite path from <see cref="ConfigureDatabasePath"/>.
    /// </summary>
    /// <remarks>This constructor is retained for the legacy singleton context and EF migration tooling.</remarks>
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
    }

    public DbSet<User> Users { get; set; }
    public DbSet<BotInstance> BotInstances { get; set; }
    public DbSet<BotUserState> BotUserStates { get; set; }
    // Tenant storefront state stays in users.db; credentials.db remains wallet/profile only.
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
    public DbSet<CookieData> Cookies { get; set; }
    public DbSet<SwapinoPaymentInfo> SwapinoPaymentInfos { get; set; }
    public DbSet<HooshPayPaymentInfo> HooshPayPaymentInfos { get; set; }

    public DbSet<ZibalPaymentInfo> ZibalPaymentInfos { get; set; }

    /// <summary>
    /// Current SQLite path used by this context.
    /// </summary>
    public static string DatabasePath => _databasePath;

    /// <summary>
    /// Runs a legacy users.db helper while holding the singleton context concurrency gate.
    /// </summary>
    /// <typeparam name="T">Return type produced by the protected helper operation.</typeparam>
    /// <param name="operation">
    /// Asynchronous users.db operation to execute. The delegate must not call another gated helper on the same
    /// context instance.
    /// </param>
    /// <returns>The value returned by <paramref name="operation"/> after the gate is released.</returns>
    /// <remarks>
    /// This is a temporary compatibility guard for singleton registration. It keeps state reads/writes from
    /// colliding across owned and tenant Telegram receivers without changing database schema.
    /// </remarks>
    private async Task<T> RunSerializedAsync<T>(Func<Task<T>> operation)
    {
        await _dbGate.WaitAsync();
        try
        {
            return await operation();
        }
        finally
        {
            _dbGate.Release();
        }
    }

    /// <summary>
    /// Runs a legacy users.db helper while holding the singleton context concurrency gate.
    /// </summary>
    /// <param name="operation">Asynchronous users.db operation that does not return a value.</param>
    /// <returns>A task that completes after <paramref name="operation"/> finishes and the gate is released.</returns>
    private async Task RunSerializedAsync(Func<Task> operation)
    {
        await _dbGate.WaitAsync();
        try
        {
            await operation();
        }
        finally
        {
            _dbGate.Release();
        }
    }

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
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
            optionsBuilder.UseSqlite($"Data Source={_databasePath};Cache=Shared");
    }

    /// <summary>
    /// Defines the <c>users.db</c> schema, indexes, and field limits for payments, bot instances,
    /// tenant orders, ledgers, bot-scoped conversation state, and idempotent scheduled-report delivery.
    /// </summary>
    /// <param name="modelBuilder">EF Core model builder used by migrations and runtime metadata.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
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
            entity.HasIndex(x => x.Username);
            entity.HasIndex(x => x.OwnerTelegramUserId);
        });

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

        modelBuilder.Entity<ZibalPaymentInfo>(entity =>
        {
            entity.HasIndex(x => x.BotId);
            entity.Property(x => x.BotId).HasMaxLength(64);
            entity.Property(x => x.BotUsername).HasMaxLength(128);
        });

        modelBuilder.Entity<BotUserState>(entity =>
        {
            entity.ToTable("BotUserStates");
            // Conversation state is isolated per bot so the same user can use several brands safely.
            entity.HasKey(x => new { x.BotId, x.TelegramUserId });
            entity.Property(x => x.BotId).HasMaxLength(64);
            entity.Property(x => x.PaymentMethod).HasMaxLength(64);
            entity.HasIndex(x => x.TelegramUserId);
            entity.HasIndex(x => x.Flow);
        });
    }


    /// <summary>
    /// Saves the legacy conversation state for the current bot and Telegram user.
    /// </summary>
    /// <param name="user">
    /// Legacy state object used by the old bot flows. Its <c>Id</c> is mapped to <c>TelegramUserId</c>,
    /// and the active <c>BotId</c> is read from <see cref="BotContextAccessor.CurrentBotId"/>.
    /// </param>
    /// <returns>A task that completes after the bot-scoped state row is inserted or updated.</returns>
    public async Task SaveUserStatus(User user)

    {
        await RunSerializedAsync(async () =>
        {
            var context = new UserDbContext();
            var botId = GetCurrentBotId();
            // Schema creation and updates are handled by migrations at application startup.

            var existingUser = await context.BotUserStates.FirstOrDefaultAsync(u => u.BotId == botId && u.TelegramUserId == user.Id);

            if (existingUser == null)
            {
                // User does not exist, create a new user
                context.BotUserStates.Add(BotUserState.FromUser(botId, user));
            }
            else
            {
                // User already exists, update the user's information if needed
                existingUser.ApplyPartial(user);
            }
            await context.SaveChangesAsync();
        });
    }


    /// <summary>
    /// Clears the saved conversation state for one user in the current bot without touching the same user in other bots.
    /// </summary>
    /// <param name="user">Legacy state object whose <c>Id</c> identifies the Telegram user to clear.</param>
    /// <returns>A task that completes after the clear operation is persisted.</returns>
    public async Task ClearUserStatus(User user)
    {
        await RunSerializedAsync(async () =>
        {
            var context = new UserDbContext();
            var botId = GetCurrentBotId();
            // context.Database.EnsureCreated(); // Create the database if it doesn't exist

            var existingUser = await context.BotUserStates.FirstOrDefaultAsync(u => u.BotId == botId && u.TelegramUserId == user.Id);

            if (existingUser == null)
            {
                // User does not exist, create a new user
                var newState = BotUserState.FromUser(botId, user);
                newState.Clear();
                context.BotUserStates.Add(newState);
            }
            else
            {
                // User already exists, update the user's information if needed
                existingUser.Clear();
            }
            await context.SaveChangesAsync();
        });
    }
    /// <summary>
    /// Checks whether the current bot has collected all legacy purchase fields needed to create an account.
    /// </summary>
    /// <param name="teluserid">Telegram user id whose bot-scoped state should be inspected.</param>
    /// <returns>
    /// <c>true</c> when the required create-flow fields are present; otherwise <c>false</c>.
    /// A missing state row is created as an empty bot-scoped state and returns <c>false</c>.
    /// </returns>
    public async Task<bool> IsUserReadyToCreate(long teluserid)
    {
        return await RunSerializedAsync(async () =>
        {
            UserDbContext context = new UserDbContext();
            var botId = GetCurrentBotId();
            // context.Database.EnsureCreated(); // Create the database if it doesn't exist

            var existingUser = await context.BotUserStates.FirstOrDefaultAsync(u => u.BotId == botId && u.TelegramUserId == teluserid);

            if (existingUser == null)
            {
                // User does not exist, create a new user
                context.BotUserStates.Add(BotUserState.FromUser(botId, new User { Id = teluserid }));
                return false;
            }

            if (existingUser.Type == "realityv6")
            {
                existingUser.TotoalGB = "500";
            }

            if (!string.IsNullOrEmpty(existingUser.LastStep) && !string.IsNullOrEmpty(existingUser.SelectedCountry) && !string.IsNullOrEmpty(existingUser.SelectedPeriod) && !string.IsNullOrEmpty(existingUser.TotoalGB) && !string.IsNullOrEmpty(existingUser.Type))
            {
                return true;
            }
            else
            {
                return false;
            }
        });


    }


    /// <summary>
    /// Checks whether the current bot has collected all legacy renewal/update fields needed to continue.
    /// </summary>
    /// <param name="teluserid">Telegram user id whose bot-scoped state should be inspected.</param>
    /// <returns>
    /// <c>true</c> when the required update-flow fields are present; otherwise <c>false</c>.
    /// A missing state row is created as an empty bot-scoped state and returns <c>false</c>.
    /// </returns>
    public async Task<bool> IsUserReadyToUpdate(long teluserid)
    {
        return await RunSerializedAsync(async () =>
        {
            UserDbContext context = new UserDbContext();
            var botId = GetCurrentBotId();
            // context.Database.EnsureCreated(); // Create the database if it doesn't exist

            var existingUser = await context.BotUserStates.FirstOrDefaultAsync(u => u.BotId == botId && u.TelegramUserId == teluserid);

            if (existingUser == null)
            {
                // User does not exist, create a new user
                context.BotUserStates.Add(BotUserState.FromUser(botId, new User { Id = teluserid }));
                return false;
            }

            if (existingUser.Type == "realityv6")
            {
                existingUser.TotoalGB = "500";
            }

            if (!string.IsNullOrEmpty(existingUser.LastStep) && !string.IsNullOrEmpty(existingUser.SelectedPeriod) && !string.IsNullOrEmpty(existingUser.TotoalGB))
            {
                return true;
            }
            else
            {
                return false;
            }
        });


    }

    /// <summary>
    /// Loads the legacy conversation state for the current bot and returns it as the existing <see cref="User"/> model.
    /// </summary>
    /// <param name="userId">Telegram user id to load for the active bot context.</param>
    /// <returns>
    /// The saved state converted from <see cref="BotUserState"/>, or a new empty <see cref="User"/> when no row exists.
    /// </returns>
    public async Task<User> GetUserStatus(long userId)
    {
        return await RunSerializedAsync(async () =>
        {
            var context = new UserDbContext();
            var botId = GetCurrentBotId();
            // context.Database.EnsureCreated(); // Create the database if it doesn't exist

            var existingUser = await context.BotUserStates.FirstOrDefaultAsync(u => u.BotId == botId && u.TelegramUserId == userId);

            if (existingUser != null)
            {
                // User does not exist, create a new user
                return existingUser.ToUser();
            }
            else
            {
                var newUser = new User { Id = userId };

                return newUser;
            }
        });

    }

    /// <summary>
    /// Resolves the bot id that should scope legacy state reads and writes.
    /// </summary>
    /// <returns>The current async bot id, or the default bot id when no update context is active.</returns>
    private static string GetCurrentBotId()
    {
        return BotContextAccessor.CurrentBotId;
    }

}

/// <summary>
/// Creates independent users.db contexts for financial and referral operations that may run concurrently.
/// </summary>
/// <remarks>
/// Legacy conversation-state helpers still use the singleton context, while new financial code uses this factory
/// so each operation owns its EF Core change tracker and can rely on database uniqueness for concurrency control.
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
