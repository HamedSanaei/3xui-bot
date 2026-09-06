using System.Reflection;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

/// <summary>
/// Regression coverage proving that the "🗽 Admin" entry is privileged high-priority navigation inside owned bots.
/// </summary>
/// <remarks>
/// A configured super-admin pressing <c>🗽 Admin</c> must open the global panel immediately, preempting every stale
/// XUI/customer/renewal/colleague sub-flow and any service-plan/panel dependency. The tests drive the real private
/// routing methods of <see cref="TelegramBotService"/> through a send-capturing probe whose production collaborators
/// (purchase, admin flow, panel, gateway, referral services) are null, so any accidental external dependency would
/// throw a null reference instead of passing.
/// </remarks>
public sealed class AdminPanelRoutingTests
{
    private const string AdminEntry = "🗽 Admin";
    private const string OldPromoteLabel = "🚀 Promote as admin";
    private const string OldDemoteLabel = "❌ Demote as admin";
    private const long SuperAdminId = 85758085;
    private const long OtherUserId = 111222333;
    private const long TargetUserId = 987654321;

    /// <summary>Captures the super-admin panel and main-menu sends without opening a Telegram network client.</summary>
    /// <param name="stateStore">Real bot-scoped conversation store used by the routing seams.</param>
    /// <param name="sessions">Real in-memory XUI purchase session store cleared on navigation.</param>
    /// <param name="configuration">Configuration that binds the injected <c>adminsUserIds</c> allow-list.</param>
    /// <param name="credentials">Optional real credentials store used by colleague-presentation scenarios.</param>
    private sealed class ProbeTelegramBotService : TelegramBotService
    {
        /// <summary>Count of super-admin panel sends performed by the routed entry.</summary>
        public int PanelSendCount { get; private set; }

        /// <summary>Count of owned-bot main-menu sends performed by the panel menu exit.</summary>
        public int MainMenuSendCount { get; private set; }

        /// <summary>Exact greeting text the production panel content builder would send.</summary>
        public string? LastPanelText { get; private set; }

        /// <summary>Exact keyboard the production panel content builder would attach.</summary>
        public ReplyKeyboardMarkup? LastPanelMarkup { get; private set; }

        /// <summary>Creates the probe with production collaborators intentionally null except the tested local stores.</summary>
        public ProbeTelegramBotService(
            UserStateStore stateStore,
            XuiV3PurchaseSessionStore sessions,
            IConfiguration configuration,
            BotContextAccessor botContextAccessor,
            CredentialsStore? credentials = null)
            : base(
                botClient: null,
                dbContext: null,
                stateStore: stateStore,
                credentialsDb: credentials,
                configuration: configuration,
                logger: NullLogger<TelegramBotService>.Instance,
                broadcastManager: null,
                nowPayments: null,
                nowPaymentsSettlementService: null,
                hooshPay: null,
                hooshPaySettlementService: null,
                tetraminator: null,
                tetraminatorSettlementService: null,
                uniquePay: null,
                uniquePayReconciliation: null,
                gatewayAvailability: null,
                xuiV3PurchaseService: null,
                xuiV3BotFlowService: null,
                xuiV3PurchaseSessionStore: sessions,
                xuiV3AdminFlowService: null,
                tenantBotService: null,
                salesAssistantService: null,
                userActivityLog: null,
                usageAnalyticsService: null,
                usageReportChartRenderer: null,
                walletLedgerService: null,
                ownedBotNotificationService: null,
                gozargahSiteApiClient: null,
                gozargahSiteSyncService: null,
                botRegistry: null,
                botRuntimeStatusStore: null,
                botContextAccessor: botContextAccessor,
                referralService: null)
        {
        }

        /// <summary>Records the exact production panel content and counts the send.</summary>
        internal override Task<Message> SendAdminPanelAsync(ITelegramBotClient botClient, ChatId chatId, CancellationToken cancellationToken)
        {
            PanelSendCount++;
            var content = BuildAdminPanelContent();
            LastPanelText = content.Text;
            LastPanelMarkup = content.Markup;
            return Task.FromResult<Message>(new Message());
        }

        /// <summary>Counts the owned-bot main-menu send performed by the panel menu exit.</summary>
        internal override Task<Message> SendMainMenuAsync(ITelegramBotClient botClient, ChatId chatId, CancellationToken cancellationToken)
        {
            MainMenuSendCount++;
            return Task.FromResult<Message>(new Message());
        }
    }

    /// <summary>Owns two isolated real SQLite databases and the bot context used by the routing seams.</summary>
    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "AdminbotAdminPanel-" + Guid.NewGuid().ToString("N"));

        /// <summary>Factory for independent users.db operations.</summary>
        public UserDbContextFactory Users { get; }

        /// <summary>Factory for the shared-wallet test database.</summary>
        public CredentialsDbContextFactory Credentials { get; }

        /// <summary>Bot-scoped conversation store under test.</summary>
        public UserStateStore State { get; }

        /// <summary>Creates WAL SQLite databases with the current schema.</summary>
        public Fixture()
        {
            Directory.CreateDirectory(_directory);
            Users = new(new DbContextOptionsBuilder<UserDbContext>().UseSqlite(SqliteOperation.ConnectionString(Path.Combine(_directory, "users.db"))).Options);
            Credentials = new(new DbContextOptionsBuilder<CredentialsDbContext>().UseSqlite(SqliteOperation.ConnectionString(Path.Combine(_directory, "credentials.db"))).Options);
            using var users = Users.CreateDbContext();
            using var credentials = Credentials.CreateDbContext();
            users.Database.EnsureCreated();
            credentials.Database.EnsureCreated();
            State = new global::UserStateStore(Users);
        }

        /// <summary>Closes SQLite pools and removes only this fixture's validated temporary directory.</summary>
        public void Dispose()
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(_directory).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Invalid test directory");
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>Builds the probe with the injected allow-list and an optional missing-plans path.</summary>
    /// <param name="fixture">Database fixture.</param>
    /// <param name="admins">Configured super-admin ids; the empty list denies everyone.</param>
    /// <param name="plansPath">Service-plan path; a missing file must never block the panel.</param>
    /// <param name="credentials">Optional real credentials store.</param>
    /// <returns>A probe whose panel sends are captured and whose external collaborators are null.</returns>
    private static ProbeTelegramBotService BuildProbe(
        Fixture fixture,
        IReadOnlyList<long> admins,
        string plansPath = "./Data/xui-v3-service-plans.json",
        CredentialsStore? credentials = null)
    {
        var values = new Dictionary<string, string?>
        {
            ["XuiV3ServicePlansPath"] = plansPath,
            ["XuiApiVersionMode"] = "v3"
        };
        for (var i = 0; i < admins.Count; i++)
            values["AdminsUserIds:" + i] = admins[i].ToString();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new ProbeTelegramBotService(
            fixture.State,
            new XuiV3PurchaseSessionStore(),
            configuration,
            new BotContextAccessor(),
            credentials);
    }

    /// <summary>Builds an owned-bot runtime context for the accessor push.</summary>
    private static BotRuntimeContext OwnedContext(string botId) => new()
    {
        Config = new BotInstanceConfig { Id = botId, Type = BotInstanceTypes.Owned, Username = botId }
    };

    /// <summary>Builds a tenant-bot runtime context for the accessor push.</summary>
    private static BotRuntimeContext TenantContext(string botId) => new()
    {
        Config = new BotInstanceConfig { Id = botId, Type = BotInstanceTypes.Tenant, Username = botId }
    };

    /// <summary>Builds a private text message from the given actor.</summary>
    private static Message AdminMessage(long actor, string text, long chatId) => new()
    {
        Text = text,
        Chat = new Chat { Id = chatId, Type = Telegram.Bot.Types.Enums.ChatType.Private },
        From = new Telegram.Bot.Types.User { Id = actor, FirstName = "test" },
        Date = DateTime.UtcNow
    };

    /// <summary>Invokes the private high-priority Admin-entry route on the probe.</summary>
    /// <returns>True when the entry consumed the message and opened the panel.</returns>
    private static Task<bool> InvokeAdminEntryAsync(TelegramBotService service, Message message)
    {
        var method = typeof(TelegramBotService).GetMethod(
            "TryHandleSuperAdminPanelEntryAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<bool>)method.Invoke(service, new object?[] { null, message, CancellationToken.None })!;
    }

    /// <summary>Invokes the private high-priority panel menu-exit route on the probe.</summary>
    /// <returns>True when the message returned the actor to the owned-bot main menu.</returns>
    private static Task<bool> InvokeMenuExitAsync(TelegramBotService service, Message message)
    {
        var method = typeof(TelegramBotService).GetMethod(
            "TryHandleSuperAdminMenuExitAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task<bool>)method.Invoke(service, new object?[] { null, message, CancellationToken.None })!;
    }

    /// <summary>Reflects the production admin action list used by the panel keyboard.</summary>
    private static string[] AdminActions(TelegramBotService service)
    {
        var method = typeof(TelegramBotService).GetMethod("GetAdminActions", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (string[])method.Invoke(service, Array.Empty<object>())!;
    }

    /// <summary>Flattens the button labels of a reply keyboard.</summary>
    private static List<string> ButtonLabels(ReplyKeyboardMarkup markup) =>
        markup.Keyboard.SelectMany(row => row).Select(button => button.Text).ToList();

    /// <summary>Asserts the stale state row for the current bot/user carries the given flow.</summary>
    private static async Task AssertStaleAsync(UserStateStore state, long user, string flow, string step)
    {
        var row = await state.GetUserStatus(user);
        Assert.Equal(flow, row.Flow);
        Assert.Equal(step, row.LastStep);
    }

    /// <summary>Asserts the Admin route cleared the current bot/user conversation.</summary>
    private static async Task AssertClearedAsync(UserStateStore state, long user)
    {
        var row = await state.GetUserStatus(user);
        Assert.True(string.IsNullOrEmpty(row.Flow), "stale flow was not cleared");
        Assert.True(string.IsNullOrEmpty(row.LastStep), "stale step was not cleared");
    }

    /// <summary>A configured super-admin in an owned bot with no stale state opens the panel immediately.</summary>
    /// <returns>A completed task after the panel greeting and keyboard are asserted.</returns>
    [Fact]
    public async Task AdminEntry_opens_panel_for_owned_super_admin()
    {
        using var fixture = new Fixture();
        var accessor = new BotContextAccessor();
        var probe = BuildProbe(fixture, new[] { SuperAdminId });
        var field = typeof(TelegramBotService).GetField("_botContextAccessor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(probe, accessor);

        using (accessor.Push(OwnedContext("owned-main")))
        {
            Assert.True(await InvokeAdminEntryAsync(probe, AdminMessage(SuperAdminId, AdminEntry, SuperAdminId)));
        }
        Assert.Equal(1, probe.PanelSendCount);
        Assert.Equal("پنل مدیریت", probe.LastPanelText);
        var labels = ButtonLabels(probe.LastPanelMarkup!);
        Assert.Contains("🚫 Ban user", labels);
        Assert.Contains("🤝 همکار کردن کاربر", labels);
        // Q and R: the audited wallet credit/debit actions stay reachable from the same panel.
        Assert.Contains("➕ Add credit", labels);
        Assert.Contains("➖ Reduce credit", labels);
        Assert.Contains("📑 Menu", labels);
    }

    /// <summary>A super-admin who is also a colleague still receives only the super-admin panel, never colleague keys.</summary>
    /// <returns>A completed task after exactly one admin-keyboard send is asserted.</returns>
    /// <remarks>Role precedence for privileged navigation is SuperAdmin &gt; Colleague &gt; Customer, and IsColleague is never mutated by the route.</remarks>
    [Fact]
    public async Task AdminEntry_super_admin_overrides_colleague_presentation()
    {
        using var fixture = new Fixture();
        var credentials = new CredentialsStore(fixture.Credentials);
        await credentials.AddEmptyUser(SuperAdminId);
        Assert.True(await credentials.PromotOrDemote(SuperAdminId, true));
        var accessor = new BotContextAccessor();
        var probe = BuildProbe(fixture, new[] { SuperAdminId }, credentials: credentials);
        // Replace the probe's accessor instance so pushes are visible to the routing method.
        var field = typeof(TelegramBotService).GetField("_botContextAccessor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(probe, accessor);

        using (accessor.Push(OwnedContext("owned-main")))
        {
            Assert.True(await InvokeAdminEntryAsync(probe, AdminMessage(SuperAdminId, AdminEntry, SuperAdminId)));
        }
        Assert.Equal(1, probe.PanelSendCount);
        Assert.Equal("پنل مدیریت", probe.LastPanelText);
        Assert.DoesNotContain("🗽 Admin", ButtonLabels(probe.LastPanelMarkup!).Where(l => l != "🗽 Admin"));
        // The admin panel carries colleague management actions; customer purchase actions are absent from the panel.
        var labels = ButtonLabels(probe.LastPanelMarkup!);
        Assert.Contains("🚫 Ban user", labels);
        Assert.DoesNotContain("💳خرید اکانت جدید", labels);
        // The route never demotes the colleague role.
        await using var credentialsDb = fixture.Credentials.CreateDbContext();
        var profile = await credentialsDb.Users.AsNoTracking().SingleAsync(x => x.TelegramUserId == SuperAdminId);
        Assert.True(profile.IsColleague);
    }

    /// <summary>Stale xui-v3-admin create-service state is cleared and the panel opens without any service-plan lookup.</summary>
    /// <param name="missingPlans">When true the configured service-plan file does not exist, which must not block the panel.</param>
    /// <returns>A completed task after state clearing and panel delivery are asserted.</returns>
    /// <remarks>Protects the production crash: the previous router let the XUI admin flow consume "🗽 Admin" as
    /// create-service input, triggering a service-plan lookup (FileNotFoundException). The probe keeps every XUI and
    /// purchase service null, so any such dependency would fail the test instead of silently succeeding.</remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AdminEntry_preempts_stale_admin_flow_and_never_reads_service_plans(bool missingPlans)
    {
        using var fixture = new Fixture();
        var accessor = new BotContextAccessor();
        var plansPath = missingPlans
            ? Path.Combine(Path.GetTempPath(), "Adminbot-plans-missing-" + Guid.NewGuid().ToString("N"), "xui-v3-service-plans.json")
            : "./Data/xui-v3-service-plans.json";
        var probe = BuildProbe(fixture, new[] { SuperAdminId }, plansPath: plansPath);
        var field = typeof(TelegramBotService).GetField("_botContextAccessor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(probe, accessor);

        using (accessor.Push(OwnedContext("owned-main")))
        {
            await fixture.State.SaveUserStatus(new global::User { Id = SuperAdminId, Flow = "xui-v3-admin", LastStep = "create-service" });
            Assert.True(await InvokeAdminEntryAsync(probe, AdminMessage(SuperAdminId, AdminEntry, SuperAdminId)));
            await AssertClearedAsync(fixture.State, SuperAdminId);
        }
        Assert.Equal(1, probe.PanelSendCount);
        Assert.Equal("پنل مدیریت", probe.LastPanelText);
    }

    /// <summary>Stale owned-bot purchase and renewal flows are abandoned by the Admin entry.</summary>
    /// <param name="flow">Real XUI customer purchase or renewal flow name persisted as stale state.</param>
    /// <param name="step">Stale step inside that flow.</param>
    /// <returns>A completed task after the state was cleared and the panel opened.</returns>
    /// <remarks>Proves tests E and F: the purchase/renewal state machine never consumes the Admin text because the
    /// privileged route runs before any stateful customer handler.</remarks>
    [Theory]
    [InlineData("xui-v3", "select-plan")]
    [InlineData("xui-v3-renew", "renew-account")]
    public async Task AdminEntry_abandons_stale_customer_purchase_and_renewal_flows(string flow, string step)
    {
        using var fixture = new Fixture();
        var accessor = new BotContextAccessor();
        var probe = BuildProbe(fixture, new[] { SuperAdminId });
        var field = typeof(TelegramBotService).GetField("_botContextAccessor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(probe, accessor);

        using (accessor.Push(OwnedContext("owned-main")))
        {
            await fixture.State.SaveUserStatus(new global::User { Id = SuperAdminId, Flow = flow, LastStep = step });
            Assert.True(await InvokeAdminEntryAsync(probe, AdminMessage(SuperAdminId, AdminEntry, SuperAdminId)));
            await AssertClearedAsync(fixture.State, SuperAdminId);
        }
        Assert.Equal(1, probe.PanelSendCount);
    }

    /// <summary>A non-super-admin gains nothing from typing the Admin entry: no panel and no state change.</summary>
    /// <returns>A completed task after the refusal is asserted.</returns>
    /// <remarks>Protects tests G and H: neither a normal customer nor a colleague-only actor is authorized.</remarks>
    [Fact]
    public async Task AdminEntry_denies_non_super_admin_without_escalation()
    {
        using var fixture = new Fixture();
        var accessor = new BotContextAccessor();
        var probe = BuildProbe(fixture, Array.Empty<long>());
        var field = typeof(TelegramBotService).GetField("_botContextAccessor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(probe, accessor);

        using (accessor.Push(OwnedContext("owned-main")))
        {
            await fixture.State.SaveUserStatus(new global::User { Id = OtherUserId, Flow = "xui-v3", LastStep = "select-plan" });
            Assert.False(await InvokeAdminEntryAsync(probe, AdminMessage(OtherUserId, AdminEntry, OtherUserId)));
            await AssertStaleAsync(fixture.State, OtherUserId, "xui-v3", "select-plan");
        }
        Assert.Equal(0, probe.PanelSendCount);
    }

    /// <summary>A colleague who is not a configured super-admin never opens the global panel.</summary>
    /// <returns>A completed task after the colleague-only refusal is asserted.</returns>
    /// <remarks>Protects test H: <c>CredUser.IsColleague</c> is colleague pricing/assistant presentation only and never
    /// confers super-admin navigation. The colleague role is preserved after the denial.</remarks>
    [Fact]
    public async Task AdminEntry_denies_colleague_only_without_global_panel()
    {
        using var fixture = new Fixture();
        var credentials = new CredentialsStore(fixture.Credentials);
        await credentials.AddEmptyUser(OtherUserId);
        Assert.True(await credentials.PromotOrDemote(OtherUserId, true));
        var accessor = new BotContextAccessor();
        var probe = BuildProbe(fixture, new[] { SuperAdminId }, credentials: credentials);
        var field = typeof(TelegramBotService).GetField("_botContextAccessor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(probe, accessor);

        using (accessor.Push(OwnedContext("owned-main")))
        {
            Assert.False(await InvokeAdminEntryAsync(probe, AdminMessage(OtherUserId, AdminEntry, OtherUserId)));
        }
        Assert.Equal(0, probe.PanelSendCount);
        // The denial did not strip the colleague presentation flag.
        await using var verify = fixture.Credentials.CreateDbContext();
        var profile = await verify.Users.AsNoTracking().SingleAsync(x => x.TelegramUserId == OtherUserId);
        Assert.True(profile.IsColleague);
    }

    /// <summary>A configured super-admin inside a tenant bot never opens the global platform panel.</summary>
    /// <returns>A completed task after the tenant refusal is asserted.</returns>
    /// <remarks>Protects test I: tenant owners are not super-admins and tenant storefronts never expose the global panel.</remarks>
    [Fact]
    public async Task AdminEntry_is_not_exposed_inside_tenant_bot()
    {
        using var fixture = new Fixture();
        var accessor = new BotContextAccessor();
        var probe = BuildProbe(fixture, new[] { SuperAdminId });
        var field = typeof(TelegramBotService).GetField("_botContextAccessor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(probe, accessor);

        using (accessor.Push(TenantContext("tenant-shop")))
        {
            Assert.False(await InvokeAdminEntryAsync(probe, AdminMessage(SuperAdminId, AdminEntry, SuperAdminId)));
        }
        Assert.Equal(0, probe.PanelSendCount);
    }

    /// <summary>The Admin entry performs no wallet or payment mutation and clears only the current BotId/user state.</summary>
    /// <returns>A completed task after wallet immutability and cross-bot isolation are asserted.</returns>
    /// <remarks>Protects tests J, K, and L: only a local conversation reset and a Telegram send occur; another owned
    /// bot's independent flow for the same Telegram user is untouched.</remarks>
    [Fact]
    public async Task AdminEntry_is_local_and_wallet_safe()
    {
        using var fixture = new Fixture();
        var credentials = new CredentialsStore(fixture.Credentials);
        await credentials.AddEmptyUser(SuperAdminId);
        var accessor = new BotContextAccessor();
        var probe = BuildProbe(fixture, new[] { SuperAdminId }, credentials: credentials);
        var field = typeof(TelegramBotService).GetField("_botContextAccessor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(probe, accessor);

        await using (var credentialsBefore = fixture.Credentials.CreateDbContext())
        {
            Assert.Equal(0, await credentialsBefore.WalletOperations.CountAsync());
        }
        await using (var usersBefore = fixture.Users.CreateDbContext())
        {
            Assert.Equal(0, await usersBefore.WalletLedgerEntries.CountAsync());
        }

        using (accessor.Push(OwnedContext("owned-b")))
        {
            await fixture.State.SaveUserStatus(new global::User { Id = SuperAdminId, Flow = "xui-v3-renew", LastStep = "renew-account" });
        }
        using (accessor.Push(OwnedContext("owned-a")))
        {
            await fixture.State.SaveUserStatus(new global::User { Id = SuperAdminId, Flow = "xui-v3", LastStep = "select-plan" });
            Assert.True(await InvokeAdminEntryAsync(probe, AdminMessage(SuperAdminId, AdminEntry, SuperAdminId)));
            await AssertClearedAsync(fixture.State, SuperAdminId);
        }
        // Another bot's independent flow for the same Telegram user is preserved.
        using (accessor.Push(OwnedContext("owned-b")))
        {
            await AssertStaleAsync(fixture.State, SuperAdminId, "xui-v3-renew", "renew-account");
        }
        // No wallet mutation: no receipt, no wallet ledger entry, and no balance change was created by the
        // privileged navigation. The only persisted effect is the bot-scoped conversation reset in users.db.
        await using var verifyCredentials = fixture.Credentials.CreateDbContext();
        Assert.Equal(0, await verifyCredentials.WalletOperations.CountAsync());
        var profile = await verifyCredentials.Users.AsNoTracking().SingleAsync(x => x.TelegramUserId == SuperAdminId);
        Assert.Equal(0, profile.AccountBalance);
        await using var verifyUsers = fixture.Users.CreateDbContext();
        Assert.Equal(0, await verifyUsers.WalletLedgerEntries.CountAsync());
        Assert.Equal(1, probe.PanelSendCount);
    }

    /// <summary>The panel menu exit clears admin state and returns the owned-bot main menu without revoking authorization.</summary>
    /// <returns>A completed task after the menu-exit route is asserted.</returns>
    /// <remarks>Protects test M: inside the super-admin panel, <c>📑 Menu</c> abandons any stale admin sub-flow and
    /// shows the normal owned-bot menu; the configured admin allow-list is untouched.</remarks>
    [Fact]
    public async Task MenuExit_returns_super_admin_to_owned_bot_main_menu()
    {
        using var fixture = new Fixture();
        var accessor = new BotContextAccessor();
        var probe = BuildProbe(fixture, new[] { SuperAdminId });
        var field = typeof(TelegramBotService).GetField("_botContextAccessor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(probe, accessor);

        using (accessor.Push(OwnedContext("owned-main")))
        {
            await fixture.State.SaveUserStatus(new global::User { Id = SuperAdminId, Flow = "xui-v3-admin", LastStep = "create-service" });
            Assert.True(await InvokeMenuExitAsync(probe, AdminMessage(SuperAdminId, "📑 Menu", SuperAdminId)));
            await AssertClearedAsync(fixture.State, SuperAdminId);
        }
        Assert.Equal(1, probe.MainMenuSendCount);
        Assert.Equal(0, probe.PanelSendCount);

        // The same menu text is harmless for a non-super-admin (no admin panel authorization is revoked or granted).
        Assert.False(await InvokeMenuExitAsync(probe, AdminMessage(OtherUserId, "📑 Menu", OtherUserId)));
        Assert.Equal(1, probe.MainMenuSendCount);
    }

    /// <summary>The misleading English role labels are replaced by the real Persian colleague actions everywhere.</summary>
    /// <returns>A completed task after the panel action set is asserted.</returns>
    /// <remarks>Protects tests N, O, and P at the presented-surface level: the only labels a user can press now read
    /// <c>همکار کردن کاربر</c> / <c>لغو همکاری کاربر</c>, and the old English labels cannot be triggered anywhere.
    /// The underlying action still toggles only <c>CredUser.IsColleague</c>; configured <c>AdminsUserIds</c> are never changed.</remarks>
    [Fact]
    public async Task Role_actions_are_renamed_to_real_colleague_labels()
    {
        using var fixture = new Fixture();
        var accessor = new BotContextAccessor();
        var probe = BuildProbe(fixture, new[] { SuperAdminId });
        var field = typeof(TelegramBotService).GetField("_botContextAccessor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(probe, accessor);

        string[] actions;
        using (accessor.Push(OwnedContext("owned-main")))
        {
            Assert.True(await InvokeAdminEntryAsync(probe, AdminMessage(SuperAdminId, AdminEntry, SuperAdminId)));
            actions = AdminActions(probe);
        }
        Assert.Contains("🤝 همکار کردن کاربر", actions);
        Assert.Contains("👤 لغو همکاری کاربر", actions);
        Assert.DoesNotContain(OldPromoteLabel, actions);
        Assert.DoesNotContain(OldDemoteLabel, actions);

        var labels = ButtonLabels(probe.LastPanelMarkup!);
        Assert.Contains("🤝 همکار کردن کاربر", labels);
        Assert.Contains("👤 لغو همکاری کاربر", labels);
        Assert.DoesNotContain(OldPromoteLabel, labels);
        Assert.DoesNotContain(OldDemoteLabel, labels);
        // The final full-width row is the menu escape.
        Assert.Equal("📑 Menu", labels[^1]);
    }

    /// <summary>The renamed colleague actions flip only <c>CredUser.IsColleague</c> and never the admin allow-list.</summary>
    /// <returns>A completed task after the promote/demote transitions are asserted.</returns>
    /// <remarks>Protects tests N, O, and P at the role-toggle seam that both renamed panel actions call in production
    /// (<c>ApplyColleagueRoleChangeAsync</c>). Promoting flips IsColleague false-&gt;true, demoting flips it back, the
    /// target's balance and receipts stay untouched, and the configuration-controlled <c>AdminsUserIds</c> list is
    /// identical before and after both toggles.</remarks>
    [Fact]
    public async Task Colleague_toggle_actions_flip_only_IsColleague_and_never_admins()
    {
        using var fixture = new Fixture();
        var credentials = new CredentialsStore(fixture.Credentials);
        await credentials.AddEmptyUser(TargetUserId);
        var accessor = new BotContextAccessor();
        var probe = BuildProbe(fixture, new[] { SuperAdminId }, credentials: credentials);
        var field = typeof(TelegramBotService).GetField("_botContextAccessor", BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(probe, accessor);

        var configField = typeof(TelegramBotService).GetField("_appConfig", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var appConfig = (AppConfig)configField.GetValue(probe)!;
        var adminsBefore = appConfig.AdminsUserIds.ToArray();

        var toggle = typeof(TelegramBotService).GetMethod(
            "ApplyColleagueRoleChangeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var actor = new Telegram.Bot.Types.User { Id = SuperAdminId, FirstName = "owner" };

        // N: promoting a normal user (همکار کردن کاربر) flips IsColleague to true.
        await (Task)toggle.Invoke(probe, new object?[] { new CredUser { TelegramUserId = TargetUserId }, true, actor, false })!;
        await using (var promoteDb = fixture.Credentials.CreateDbContext())
        {
            var promoted = await promoteDb.Users.AsNoTracking().SingleAsync(x => x.TelegramUserId == TargetUserId);
            Assert.True(promoted.IsColleague);
        }

        // O: demoting (لغو همکاری کاربر) flips IsColleague back to false.
        await (Task)toggle.Invoke(probe, new object?[] { new CredUser { TelegramUserId = TargetUserId }, false, actor, true })!;
        await using (var demoteDb = fixture.Credentials.CreateDbContext())
        {
            var demoted = await demoteDb.Users.AsNoTracking().SingleAsync(x => x.TelegramUserId == TargetUserId);
            Assert.False(demoted.IsColleague);
            Assert.Equal(0, demoted.AccountBalance);
        }

        // P: neither toggle touched the configured super-admin allow-list or created wallet activity.
        Assert.Equal(adminsBefore, appConfig.AdminsUserIds);
        await using var verify = fixture.Credentials.CreateDbContext();
        Assert.Equal(0, await verify.WalletOperations.CountAsync());
    }
}
