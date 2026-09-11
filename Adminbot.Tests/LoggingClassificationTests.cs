using System.Reflection;
using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Telegram.Bot.Types;
using Xunit;

/// <summary>
/// Regression coverage for the logging classification contract: non-financial operational/security/admin audits
/// must stay durable through the Telegram outbox as Html (EventId 1001/TelegramHtml) and must never request a
/// database backup, while genuine financial events keep Payment semantics (EventId 1000) that increment the
/// backup generation. The five converted call sites are exercised through their real production methods (the
/// private log builders invoked reflectively on null-dependency service instances, the same pattern used by
/// <see cref="BackupRecoveryTests"/>), and the two inline flow call sites are protected by a source-contract
/// assertion so a future reclassification to <c>LogPayment</c> fails the build.
/// </summary>
public sealed class LoggingClassificationTests
{
    /// <summary>
    /// Admin phone verification is an operational security audit: it must be committed to the durable outbox as
    /// Html with the masked phone content preserved, and the backup generation must stay at zero.
    /// </summary>
    [Fact]
    public async Task Admin_phone_verification_is_durable_html_without_backup()
    {
        await using var fixture = new BackupRecoveryTests.Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new BackupRecoveryTests.Sender { TextBarrier = release.Task };
        await using var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options);
        var service = BuildTelegramBotService(BuildLogger(dispatcher));

        var method = typeof(TelegramBotService).GetMethod(
            "LogAdminPhoneVerification",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [typeof(Telegram.Bot.Types.User), typeof(CredUser), typeof(string), typeof(string)],
            null)!;

        try
        {
            method.Invoke(service, [
                new Telegram.Bot.Types.User { Id = 222, Username = "superadmin", FirstName = "مدیر" },
                new CredUser { TelegramUserId = 111, ChatID = 111, Username = "customer", FirstName = "مشتری", LastName = "تست" },
                "",
                "09121234567"]);

            var row = Assert.Single(await ReadRowsAsync(fixture.Options.OutboxDatabasePath));
            Assert.Equal((int)TelegramLogDeliveryKind.Html, row.DeliveryKind);
            Assert.Equal("log", row.Channel);
            Assert.Contains("تأیید دستی شماره تلفن", row.Message);
            Assert.Contains("091*****567", row.Message);
            Assert.Equal(0, await RequestedGenerationsAsync(fixture.Options.OutboxDatabasePath));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>
    /// Admin role changes affect access and pricing but do not mutate financial state: the audit must remain
    /// durable Html and must not request a backup.
    /// </summary>
    [Fact]
    public async Task Admin_role_change_is_durable_html_without_backup()
    {
        await using var fixture = new BackupRecoveryTests.Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new BackupRecoveryTests.Sender { TextBarrier = release.Task };
        await using var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options);
        var service = BuildTelegramBotService(BuildLogger(dispatcher));

        var method = typeof(TelegramBotService).GetMethod(
            "LogAdminRoleChange",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [typeof(Telegram.Bot.Types.User), typeof(CredUser), typeof(bool), typeof(bool)],
            null)!;

        try
        {
            method.Invoke(service, [
                new Telegram.Bot.Types.User { Id = 222, Username = "superadmin", FirstName = "مدیر" },
                new CredUser { TelegramUserId = 111, ChatID = 111, Username = "partner", FirstName = "همکار" },
                false,
                true]);

            var row = Assert.Single(await ReadRowsAsync(fixture.Options.OutboxDatabasePath));
            Assert.Equal((int)TelegramLogDeliveryKind.Html, row.DeliveryKind);
            Assert.Contains("ارتقای کاربر به همکار", row.Message);
            Assert.Contains("نقش بعد", row.Message);
            Assert.Equal(0, await RequestedGenerationsAsync(fixture.Options.OutboxDatabasePath));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>
    /// A colleague/cooperation request is an operational audit: the production message builder output must be
    /// delivered as durable Html with its content intact and without backup intent.
    /// </summary>
    [Fact]
    public async Task Colleague_request_audit_is_durable_html_without_backup()
    {
        await using var fixture = new BackupRecoveryTests.Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new BackupRecoveryTests.Sender { TextBarrier = release.Task };
        await using var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options);
        var logger = BuildLogger(dispatcher);

        var build = typeof(XuiV3BotFlowService).GetMethod(
            "BuildColleagueRequestLogMessage",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            [typeof(CredUser)],
            null)!;
        var credUser = new CredUser
        {
            TelegramUserId = 444,
            ChatID = 444,
            Username = "partner",
            FirstName = "همکار",
            PhoneNumber = "0912*******",
            Email = "partner@example.com",
            AccountBalance = 12_000_000,
            IsColleague = true
        };

        try
        {
            var message = (string)build.Invoke(null, [credUser])!;
            logger.LogTelegramHtml(message);

            var row = Assert.Single(await ReadRowsAsync(fixture.Options.OutboxDatabasePath));
            Assert.Equal((int)TelegramLogDeliveryKind.Html, row.DeliveryKind);
            Assert.Contains("درخواست همکاری جدید", row.Message);
            Assert.Contains("partner@example.com", row.Message);
            Assert.Equal(0, await RequestedGenerationsAsync(fixture.Options.OutboxDatabasePath));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>
    /// A link change does not mutate wallet or payment state: the production audit message must be delivered as
    /// durable Html with its identifiers intact and without backup intent.
    /// </summary>
    [Fact]
    public async Task Link_change_audit_is_durable_html_without_backup()
    {
        await using var fixture = new BackupRecoveryTests.Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new BackupRecoveryTests.Sender { TextBarrier = release.Task };
        await using var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options);
        var logger = BuildLogger(dispatcher);

        var build = typeof(XuiV3BotFlowService).GetMethod(
            "BuildChangeLinkLogMessage",
            BindingFlags.Static | BindingFlags.NonPublic,
            null,
            [typeof(CredUser), typeof(XuiV3Client), typeof(string), typeof(string), typeof(string), typeof(string),
             typeof(string), typeof(string), typeof(string), typeof(string), typeof(ServerInfo), typeof(XuiOperationTimingSnapshot)],
            null)!;
        var actor = new CredUser { TelegramUserId = 555, ChatID = 555, Username = "owner", FirstName = "مالک" };
        var client = new XuiV3Client { Email = "old@example.com", Uuid = "uuid-old", SubId = "sub-old", TgId = 555, TotalGB = 50_000_000_000 };

        try
        {
            var message = (string)build.Invoke(null, [
                actor, client, "old@example.com", "uuid-old", "sub-old", "old-sub-link",
                "new@example.com", "uuid-new", "sub-new", "new-sub-link",
                new ServerInfo { Name = "test-panel" },
                new XuiOperationTimingSnapshot(TimeSpan.FromMilliseconds(4), TimeSpan.FromMilliseconds(8))])!;
            logger.LogTelegramHtml(message);

            var row = Assert.Single(await ReadRowsAsync(fixture.Options.OutboxDatabasePath));
            Assert.Equal((int)TelegramLogDeliveryKind.Html, row.DeliveryKind);
            Assert.Contains("گزارش تغییر لینک اکانت نسخه ۳", row.Message);
            Assert.Contains("new@example.com", row.Message);
            Assert.Equal(0, await RequestedGenerationsAsync(fixture.Options.OutboxDatabasePath));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>
    /// Account deletion is an account-management audit, not a financial event: the production delete audit must
    /// remain durable Html with the deleted account identifiers and without backup intent.
    /// </summary>
    [Fact]
    public async Task Account_deletion_audit_is_durable_html_without_backup()
    {
        await using var fixture = new BackupRecoveryTests.Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new BackupRecoveryTests.Sender { TextBarrier = release.Task };
        await using var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options);
        var flow = BuildXuiFlowService(BuildLogger(dispatcher));

        var method = typeof(XuiV3BotFlowService).GetMethod(
            "LogAccountDelete",
            BindingFlags.Instance | BindingFlags.NonPublic,
            null,
            [typeof(XuiV3Client), typeof(CredUser), typeof(string), typeof(XuiOperationTimingSnapshot)],
            null)!;

        try
        {
            method.Invoke(flow, [
                new XuiV3Client { Email = "deleted-user", Uuid = "uuid-1", SubId = "sub-1", TgId = 333 },
                new CredUser { TelegramUserId = 333, ChatID = 333, Username = "customer", FirstName = "کاربر" },
                "list",
                new XuiOperationTimingSnapshot(TimeSpan.FromMilliseconds(3), TimeSpan.FromMilliseconds(9))]);

            var row = Assert.Single(await ReadRowsAsync(fixture.Options.OutboxDatabasePath));
            Assert.Equal((int)TelegramLogDeliveryKind.Html, row.DeliveryKind);
            Assert.Contains("گزارش حذف اکانت نسخه ۳", row.Message);
            Assert.Contains("deleted-user", row.Message);
            Assert.Equal(0, await RequestedGenerationsAsync(fixture.Options.OutboxDatabasePath));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>
    /// The two inline flow call sites (colleague request and link change) emit the audit directly inside large
    /// update handlers that are not constructible in a unit test. This source-contract assertion protects the
    /// exact production classification from silently reverting to <c>LogPayment</c>.
    /// </summary>
    [Fact]
    public void Inline_operational_audit_call_sites_are_reclassified_to_telegram_html()
    {
        var sourcePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Services/XuiV3BotFlowService.cs"));
        var source = System.IO.File.ReadAllText(sourcePath);
        Assert.Contains("_logger.LogTelegramHtml(BuildColleagueRequestLogMessage(credUser));", source);
        Assert.DoesNotContain("_logger.LogPayment(BuildColleagueRequestLogMessage", source);
        Assert.Contains("_logger.LogTelegramHtml(BuildChangeLinkLogMessage(", source);
        Assert.DoesNotContain("_logger.LogPayment(BuildChangeLinkLogMessage", source);
    }

    /// <summary>
    /// A genuine financial event must keep Payment semantics: the durable row carries Payment kind and the backup
    /// generation increments by exactly one.
    /// </summary>
    [Fact]
    public async Task Genuine_payment_log_keeps_payment_kind_and_requests_backup()
    {
        await using var fixture = new BackupRecoveryTests.Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sender = new BackupRecoveryTests.Sender { TextBarrier = release.Task };
        await using var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options);
        try
        {
            BuildLogger(dispatcher).LogPayment("financial payment");

            var row = Assert.Single(await ReadRowsAsync(fixture.Options.OutboxDatabasePath));
            Assert.Equal((int)TelegramLogDeliveryKind.Payment, row.DeliveryKind);
            Assert.Equal("financial payment", row.Message);
            Assert.Equal(1, await RequestedGenerationsAsync(fixture.Options.OutboxDatabasePath));
        }
        finally
        {
            release.TrySetResult();
        }
    }

    /// <summary>
    /// Converting operational audits to <c>LogTelegramHtml</c> must not weaken reliability: a durable Html row
    /// that was not delivered before the dispatcher stopped must be delivered by a fresh dispatcher after a
    /// transient failure and restart, without any backup document.
    /// </summary>
    [Fact]
    public async Task Operational_html_audit_survives_dispatcher_restart()
    {
        await using var fixture = new BackupRecoveryTests.Fixture();
        long elapsedMs = 0;
        var origin = DateTime.UtcNow;
        var options = fixture.Options with { UtcNow = () => origin.AddMilliseconds(Interlocked.Read(ref elapsedMs)) };
        const string message = "restart-audit <b>op</b>";

        // Phase 1: the producer commits the audit durably; the first send fails transiently and the dispatcher
        // stops (crash simulation) before the retry is due.
        var failing = new BackupRecoveryTests.Sender { FailFirst = true };
        await using (var dispatcher = new TelegramLogDispatcher(_ => failing, options))
        {
            BuildLogger(dispatcher).LogTelegramHtml(message);
            await BackupRecoveryTests.Until(() => failing.Texts == 1);

            var row = Assert.Single(await ReadRowsAsync(fixture.Options.OutboxDatabasePath));
            Assert.Equal((int)TelegramLogDeliveryKind.Html, row.DeliveryKind);
            Assert.Equal(message, row.Message);
            Assert.Equal(0, await RequestedGenerationsAsync(fixture.Options.OutboxDatabasePath));
        }

        // Phase 2: restart after the persisted backoff has elapsed; a fresh dispatcher must deliver the audit.
        Interlocked.Exchange(ref elapsedMs, 60_000);
        var recovered = new BackupRecoveryTests.Sender();
        await using (var dispatcher = new TelegramLogDispatcher(_ => recovered, options))
        {
            await BackupRecoveryTests.Until(() => recovered.Texts == 1);
            Assert.Equal(0, recovered.Documents);
        }

        // The local ack (row deletion) happens right after Telegram acceptance; wait for it to prove delivery.
        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while ((await ReadRowsAsync(fixture.Options.OutboxDatabasePath)).Count > 0)
            await Task.Delay(10, drain.Token);
    }

    /// <summary>Builds a production Telegram logger bound to a real durable dispatcher and outbox database.</summary>
    private static TelegramLogger BuildLogger(TelegramLogDispatcher dispatcher)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var registry = new BotRegistry(configuration);
        return new TelegramLogger("classification-test", null, registry, new BotContextAccessor(), "log", "backup", dispatcher);
    }

    /// <summary>Builds a production service instance whose private audit methods only need the injected logger.</summary>
    private static TelegramBotService BuildTelegramBotService(ILogger logger)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        return new TelegramBotService(
            botClient: null,
            dbContext: null,
            stateStore: null,
            credentialsDb: null,
            configuration: configuration,
            logger: new TypedLogger<TelegramBotService>(logger),
            broadcastManager: null,
            nowPayments: null,
            nowPaymentsSettlementService: null,
            hooshPay: null,
            hooshPaySettlementService: null,
            tetraminator: null,
            tetraminatorSettlementService: null,
            uniquePay: null,
            uniquePayReconciliation: null,
            atlasPay: null,
            atlasPayReconciliation: null,
            gatewayAvailability: null,
            clientDownloadAvailability: null,
            clientReleaseService: null,
            xuiV3PurchaseService: null,
            xuiV3BotFlowService: null,
            xuiV3PurchaseSessionStore: null,
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
            botContextAccessor: null,
            referralService: null);
    }

    /// <summary>Builds a production XUI v3 flow service whose private audit methods only need the injected logger.</summary>
    private static XuiV3BotFlowService BuildXuiFlowService(ILogger logger)
    {
        var configuration = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        return new XuiV3BotFlowService(
            purchaseService: null,
            sessionStore: null,
            userDbContext: null,
            credentialsDbContext: null,
            configuration: configuration,
            logger: new TypedLogger<XuiV3BotFlowService>(logger),
            activityLog: null,
            walletLedgerService: null,
            gozargahSiteSyncService: null,
            botContextAccessor: null,
            linkChangeOperationStore: null,
            volumeReminderStateStore: null,
            renewalOperationStore: null);
    }

    /// <summary>Forwards to the wrapped production Telegram logger under a concrete logger category.</summary>
    private sealed class TypedLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel level) => inner.IsEnabled(level);
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? error, Func<TState, Exception?, string> formatter) => inner.Log(level, eventId, state, error, formatter);
    }

    /// <summary>Reads all durable outbox rows for kind, channel, and content assertions.</summary>
    private static async Task<List<(int DeliveryKind, string Channel, string Message)>> ReadRowsAsync(string outboxPath)
    {
        using var db = new SqliteConnection("Data Source=" + outboxPath);
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT DeliveryKind, LoggerChannelId, Message FROM TelegramLogOutbox";
        var rows = new List<(int, string, string)>();
        using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            rows.Add((reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        return rows;
    }

    /// <summary>Reads the durable backup watermark to prove no backup intent was created.</summary>
    private static async Task<long> RequestedGenerationsAsync(string outboxPath)
    {
        await using var outbox = new TelegramLogOutbox(outboxPath);
        return (await outbox.ReadBackupAsync()).Requested;
    }
}