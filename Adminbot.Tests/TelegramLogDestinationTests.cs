using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Telegram.Bot.Types;
using Xunit;

/// <summary>
/// Regression coverage for Telegram logger destination handling.
/// </summary>
/// <remarks>
/// Production defect being protected against: global <c>loggerChannel</c>/<c>backupChannel</c> were absent and the
/// default owned bot had no per-bot values, so the missing <see cref="AppConfig.BackupChannel"/> (a <c>long</c> whose
/// default is 0) was normalized into the non-blank string <c>"0"</c>. Durable outbox rows were then created with an
/// empty logger destination and a <c>"0"</c> backup destination. Telegram.Bot raised a local
/// <see cref="ArgumentException"/> before any Telegram response existed, the rate-limit policy did not classify it as
/// permanent, and the rows stayed Pending at <c>AttemptCount=5</c> forever.
///
/// The tests below pin the four independent rules that together close that defect:
/// <list type="number">
/// <item><description>one shared parser decides what a Telegram destination is, and it accepts exactly the two forms
/// the Telegram client itself accepts;</description></item>
/// <item><description>zero never becomes the string <c>"0"</c> anywhere in the configuration path;</description></item>
/// <item><description>a durable row is never created with a destination that can never be delivered;</description></item>
/// <item><description>an already-persisted malformed row converges to DeadLetter immediately instead of retrying as a
/// transient failure.</description></item>
/// </list>
///
/// No test starts a listener or performs a Telegram request: delivery goes through a recording fake.
/// </remarks>
public sealed class TelegramLogDestinationTests
{
    /// <summary>Valid negative supergroup/channel chat id used as the logger destination.</summary>
    private const string LoggerChannel = "-1001234567890";

    /// <summary>Valid negative supergroup/channel chat id used as the backup destination.</summary>
    private const string BackupChannel = "-1001234567891";

    /// <summary>Stable sanitized error written to a dead-lettered row for an unusable destination.</summary>
    private const string InvalidDestinationCode = "invalid_logger_destination";

    /// <summary>
    /// Proves the parser accepts exactly what the Telegram client accepts, using the real client type as the oracle.
    /// </summary>
    /// <param name="destination">Accepted destination form under test.</param>
    /// <remarks>
    /// Constructing <see cref="ChatId"/> from the normalized text is the strongest available contract check: it fails
    /// the build if a future Telegram.Bot version narrows the accepted grammar, and it is the exact construction the
    /// transport performs when it converts the string destination into a request.
    /// </remarks>
    [Theory]
    [InlineData("-1001234567890")]
    [InlineData("-987654321")]
    [InlineData("123456789")]
    [InlineData("@vpnetiran_logger")]
    [InlineData("  -1001234567890  ")]
    public void Accepted_destinations_satisfy_the_telegram_client_contract(string destination)
    {
        Assert.True(TelegramDestination.TryNormalize(destination, out var normalized, out var reason));
        Assert.Empty(reason);

        // The transport converts the string destination through this constructor; it must never throw for a value the
        // parser accepted, because there is no Telegram response to classify when it does.
        var chatId = new ChatId(normalized);
        Assert.Equal(normalized.Trim(), normalized);
        Assert.NotNull(chatId);
    }

    /// <summary>
    /// Proves blank, zero-sentinel, and malformed values are rejected with a reason instead of reaching the transport.
    /// </summary>
    /// <param name="destination">Destination value that must be rejected.</param>
    /// <param name="expectedReason">Closed-vocabulary reason the parser must report.</param>
    [Theory]
    [InlineData(null, TelegramDestination.ReasonBlank)]
    [InlineData("", TelegramDestination.ReasonBlank)]
    [InlineData("   ", TelegramDestination.ReasonBlank)]
    [InlineData("0", TelegramDestination.ReasonZero)]
    [InlineData("0 ", TelegramDestination.ReasonZero)]
    [InlineData("vpnetiranbot", TelegramDestination.ReasonMalformed)]
    [InlineData("my channel", TelegramDestination.ReasonMalformed)]
    [InlineData("@ab", TelegramDestination.ReasonMalformed)]
    [InlineData("@bad-name", TelegramDestination.ReasonMalformed)]
    public void Missing_and_malformed_destinations_are_rejected_with_a_reason(string? destination, string expectedReason)
    {
        Assert.False(TelegramDestination.TryNormalize(destination!, out var normalized, out var reason));
        Assert.Equal(string.Empty, normalized);
        Assert.Equal(expectedReason, reason);
        Assert.Equal(TelegramDestinationKind.None, TelegramDestination.Classify(destination!));
        Assert.Equal(string.Empty, TelegramDestination.Sanitize(destination!));
    }

    /// <summary>
    /// Proves a bare word is never repaired into a username, because that would invent a destination.
    /// </summary>
    /// <remarks>
    /// The previous behavior in the field was that a non-numeric, non-<c>@</c> value reached the transport and threw a
    /// local argument error. Silently prepending <c>@</c> would convert that loud failure into a quiet misroute of
    /// operational logs to an unintended channel, so rejection is the deliberate contract.
    /// </remarks>
    [Fact]
    public void A_bare_word_is_never_converted_into_a_username()
    {
        Assert.False(TelegramDestination.TryNormalize("vpnetiran", out var normalized, out _));
        Assert.Equal(string.Empty, normalized);
        Assert.DoesNotContain("@", TelegramDestination.Sanitize("vpnetiran"));
    }

    /// <summary>
    /// Proves precedence between the global key and the per-bot value is applied after validation.
    /// </summary>
    /// <remarks>
    /// A malformed global value must not shadow a correct per-bot value: ordering the resolution before validation
    /// would silently disable a working channel because an unrelated legacy key was unedited.
    /// </remarks>
    [Fact]
    public void Valid_per_bot_value_wins_over_a_malformed_global_value()
    {
        // A malformed global value must not mask a correct per-bot value...
        Assert.Equal(LoggerChannel, TelegramDestination.SelectValid("not-a-destination", LoggerChannel));
        // ...while a valid higher-precedence value still wins over a valid lower-precedence one.
        Assert.Equal(BackupChannel, TelegramDestination.SelectValid(BackupChannel, LoggerChannel));
        Assert.Equal(string.Empty, TelegramDestination.SelectValid("not-a-destination", "0"));
    }

    /// <summary>
    /// Proves the numeric zero sentinel never becomes the string "0" in the runtime bot registry.
    /// </summary>
    /// <remarks>
    /// This is the exact production configuration shape: a <c>long BackupChannel</c> whose missing default is 0 and
    /// a default owned bot with no per-bot channel values. Before the fix it produced the non-blank string "0", which
    /// downstream code treated as a configured Telegram destination.
    /// </remarks>
    [Fact]
    public void Zero_backup_channel_normalizes_to_absent_and_never_to_the_string_zero()
    {
        var registry = new BotRegistry(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["BotToken"] = "1:test-token",
                ["BackupChannel"] = "0"
            }).Build());

        Assert.NotNull(registry.DefaultBot);
        Assert.Equal(string.Empty, registry.DefaultBot.BackupChannel);
        Assert.Equal(string.Empty, registry.DefaultBot.LoggerChannel);
        Assert.NotEqual("0", registry.DefaultBot.BackupChannel);
    }

    /// <summary>
    /// Proves a configured numeric channel survives normalization unchanged, and an absent key stays absent.
    /// </summary>
    /// <param name="configuredBackupChannel">Raw global backup channel value, or null to omit the key entirely.</param>
    /// <param name="expected">Expected normalized value on the default owned bot.</param>
    /// <remarks>
    /// The key is omitted rather than set to an empty string, because <see cref="AppConfig.BackupChannel"/> is a
    /// <c>long</c> and the configuration binder rejects an empty value for it at startup. Absence is therefore the
    /// only supported "unset" representation for this legacy key, which is precisely why the numeric zero sentinel
    /// had to be handled here as well.
    /// </remarks>
    [Theory]
    [InlineData("-1001234567891", "-1001234567891")]
    [InlineData("0", "")]
    [InlineData(null, "")]
    public void Configured_backup_channel_is_preserved_while_absence_stays_empty(string? configuredBackupChannel, string expected)
    {
        var values = new Dictionary<string, string?> { ["BotToken"] = "1:test-token" };
        if (configuredBackupChannel != null)
            values["BackupChannel"] = configuredBackupChannel;

        var registry = new BotRegistry(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        Assert.Equal(expected, registry.DefaultBot.BackupChannel);
        Assert.NotEqual("0", registry.DefaultBot.BackupChannel);
    }

    /// <summary>
    /// Proves the stored-channel helper keeps an unrecognised value instead of destroying an operator setting.
    /// </summary>
    /// <remarks>
    /// The helper is used by the configuration-to-database sync. Discarding an unrecognised value there would make a
    /// configuration mistake unrecoverable, so only blank and the exact zero sentinel are collapsed.
    /// </remarks>
    [Fact]
    public void Stored_channel_normalization_collapses_only_blank_and_zero()
    {
        Assert.Equal(string.Empty, TelegramDestination.NormalizeStoredChannel(null!));
        Assert.Equal(string.Empty, TelegramDestination.NormalizeStoredChannel("   "));
        Assert.Equal(string.Empty, TelegramDestination.NormalizeStoredChannel("0"));
        Assert.Equal(string.Empty, TelegramDestination.NormalizeStoredChannel(" 0 "));
        Assert.Equal("unrecognised-value", TelegramDestination.NormalizeStoredChannel(" unrecognised-value "));
    }

    /// <summary>
    /// Proves a blank logger destination never reaches the transport and never creates a durable row.
    /// </summary>
    /// <remarks>
    /// The row itself is the defect: a durable Payment/Html row whose destination is empty can never be acknowledged,
    /// so it accumulates as an unbounded backlog and hides the real configuration fault behind permanent delivery
    /// errors. Logging must fail locally and silently instead.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("vpnetiranbot")]
    public async Task Durable_log_with_an_unusable_logger_destination_creates_no_row_and_no_send(string destination)
    {
        await using var fixture = new BackupRecoveryTests.Fixture();
        var sender = new RecordingLogSender();
        await using var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options);
        var logger = BuildLogger(dispatcher, destination, BackupChannel);

        // Must not throw: the caller may be inside payment settlement or Telegram update handling.
        logger.LogTelegramHtml("<b>audit</b>");

        Assert.Empty(sender.Texts);
        Assert.Equal(0, await CountRowsAsync(fixture.Options.OutboxDatabasePath));
        Assert.Equal(0, (await ReadBackupRequestedAsync(fixture.Options.OutboxDatabasePath)));
    }

    /// <summary>
    /// Proves a blank destination with an explicit empty fallback also produces no row and no send.
    /// </summary>
    /// <remarks>
    /// Covers the second half of the production state, where the default owned bot had a blank
    /// <c>LoggerChannel</c> and the global <c>loggerChannel</c> key was absent altogether.
    /// </remarks>
    [Fact]
    public async Task Blank_destination_is_never_enqueued_durably()
    {
        await using var fixture = new BackupRecoveryTests.Fixture();
        var sender = new RecordingLogSender();
        await using var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options);
        var logger = BuildLogger(dispatcher, string.Empty, string.Empty);

        logger.LogTelegramHtml("<b>audit</b>");

        Assert.Empty(sender.Texts);
        Assert.Equal(0, await CountRowsAsync(fixture.Options.OutboxDatabasePath));
    }

    /// <summary>
    /// Proves a payment whose audit destination is misconfigured neither throws nor loses its backup request.
    /// </summary>
    /// <remarks>
    /// This is the financial invariant: a logging misconfiguration must never be able to fail payment or wallet
    /// settlement, and it must never silently stop the database snapshots those payments request. The audit line
    /// itself is intentionally not queued, because it could never be delivered.
    /// </remarks>
    [Fact]
    public async Task Payment_with_a_misconfigured_destination_keeps_backup_intent_and_cannot_fail_settlement()
    {
        await using var fixture = new BackupRecoveryTests.Fixture();
        var sender = new RecordingLogSender();
        await using var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options);
        var logger = BuildLogger(dispatcher, string.Empty, BackupChannel);

        logger.LogPayment("<b>پرداخت</b>");

        Assert.Empty(sender.Texts);
        Assert.Equal(0, await CountRowsAsync(fixture.Options.OutboxDatabasePath));
        Assert.Equal(1, await ReadBackupRequestedAsync(fixture.Options.OutboxDatabasePath));
    }

    /// <summary>
    /// Proves a historical durable row with an empty logger destination converges to DeadLetter instead of retrying.
    /// </summary>
    /// <remarks>
    /// Existing outbox databases can already contain the malformed rows produced by the defective build. They must
    /// converge under the existing permanent-failure rules rather than being retried as transient failures forever,
    /// and the historical row must not be deleted: the dead-letter row keeps the audit body inspectable.
    /// </remarks>
    [Fact]
    public async Task Historical_row_with_a_blank_logger_destination_converges_to_dead_letter()
    {
        await using var fixture = new BackupRecoveryTests.Fixture();
        await using (var outbox = new TelegramLogOutbox(fixture.Options.OutboxDatabasePath))
            await outbox.EnqueueAsync(HistoricalRow(loggerChannelId: string.Empty));

        var sender = new RecordingLogSender();
        await using (var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options))
            await WaitForDeadLetterAsync(fixture.Options.OutboxDatabasePath);

        var (status, lastError) = await ReadSingleRowStateAsync(fixture.Options.OutboxDatabasePath);
        Assert.Equal((int)TelegramLogOutboxStatus.DeadLetter, status);
        Assert.Equal(InvalidDestinationCode, lastError);
        Assert.Empty(sender.Texts);
        Assert.Equal(1, await CountRowsAsync(fixture.Options.OutboxDatabasePath));
    }

    /// <summary>
    /// Proves a historical row whose destination is the zero sentinel also converges to DeadLetter.
    /// </summary>
    /// <remarks>
    /// The production evidence recorded <c>BackupChannelId = "0"</c>; a legacy row can just as easily carry
    /// <c>LoggerChannelId = "0"</c>, which must be treated as a definitive configuration error, not a retry.
    /// </remarks>
    [Fact]
    public async Task Historical_row_with_the_zero_sentinel_converges_to_dead_letter()
    {
        await using var fixture = new BackupRecoveryTests.Fixture();
        await using (var outbox = new TelegramLogOutbox(fixture.Options.OutboxDatabasePath))
            await outbox.EnqueueAsync(HistoricalRow(loggerChannelId: "0"));

        var sender = new RecordingLogSender();
        await using (var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options))
            await WaitForDeadLetterAsync(fixture.Options.OutboxDatabasePath);

        var (status, lastError) = await ReadSingleRowStateAsync(fixture.Options.OutboxDatabasePath);
        Assert.Equal((int)TelegramLogOutboxStatus.DeadLetter, status);
        Assert.Equal(InvalidDestinationCode, lastError);
        Assert.Empty(sender.Texts);
    }

    /// <summary>
    /// Proves the invalid-destination failure is classified as permanent and never as transient.
    /// </summary>
    /// <remarks>
    /// The classification is what stopped the production rows from retrying forever. It is asserted directly, and
    /// also asserted to be impossible to confuse with a programming-bug argument error.
    /// </remarks>
    [Fact]
    public void Invalid_destination_is_permanent_and_never_transient()
    {
        var invalidDestination = new TelegramDestinationInvalidException(TelegramDestination.ReasonBlank);

        Assert.True(TelegramRateLimitPolicy.IsPermanentFailure(invalidDestination));
        Assert.False(TelegramRateLimitPolicy.IsTransientFailure(invalidDestination, cancellationRequested: false));
        Assert.False(TelegramRateLimitPolicy.IsRateLimited(invalidDestination));
    }

    /// <summary>
    /// Proves an unrelated argument error is not swallowed as a Telegram permanent delivery error.
    /// </summary>
    /// <remarks>
    /// Classifying every <see cref="ArgumentException"/> as permanent would bury genuine programming bugs: their rows
    /// would dead-letter silently instead of being retried and investigated. Only the dedicated destination type is
    /// permanent.
    /// </remarks>
    [Fact]
    public void Unrelated_argument_exception_is_not_a_permanent_telegram_failure()
    {
        var programmingBug = new ArgumentException("a real bug elsewhere", "unrelatedParameter");

        Assert.False(TelegramRateLimitPolicy.IsPermanentFailure(programmingBug));
        Assert.False(TelegramRateLimitPolicy.IsTransientFailure(programmingBug, cancellationRequested: false));
    }

    /// <summary>
    /// Proves the typed failure never exposes a destination value, token, or chat id through its message.
    /// </summary>
    /// <remarks>
    /// The stable code is persisted into the outbox <c>LastError</c> column and printed to the journal, so it must be
    /// safe to store arbitrary-source text next to it.
    /// </remarks>
    [Fact]
    public void Typed_failure_message_carries_only_the_stable_code()
    {
        var failure = new TelegramDestinationInvalidException(TelegramDestination.ReasonMalformed);

        Assert.Equal(InvalidDestinationCode, failure.Message);
        Assert.Equal(TelegramDestination.ReasonMalformed, failure.Reason);
        Assert.DoesNotContain(":", failure.Message);
    }

    /// <summary>
    /// Proves the startup report describes the exact production configuration shape without echoing values.
    /// </summary>
    /// <remarks>
    /// Global <c>loggerChannel</c>/<c>backupChannel</c> absent and default-bot per-bot values absent is the reported
    /// production state. The report must surface it once at startup without printing the raw configured value,
    /// because that value could be a pasted token. Reporting only: this must never throw, so the process still starts.
    /// </remarks>
    [Fact]
    public void Startup_report_describes_the_unconfigured_production_shape_without_echoing_values()
    {
        var lines = ConfigurationPreflight.DescribeStartupReport(
            globalLoggerChannel: "tokenLookingSecret123",
            defaultBotLoggerChannel: string.Empty,
            globalBackupChannel: "0",
            defaultBotBackupChannel: string.Empty,
            xuiV3ApiBaseUrl: "");

        Assert.Equal("[ConfigurationPreflight] loggerChannel=none backupChannel=none", lines[0]);
        Assert.Contains(lines, line => line.Contains("no deliverable Telegram logger channel"));
        Assert.Contains(lines, line => line.Contains("botLoggerChannel=blank"));
        Assert.Contains(lines, line => line.Contains("globalLoggerChannel=malformed"));
        Assert.Contains(lines, line => line.Contains("globalBackupChannel=zero"));
        // Sanitized: the rejected value itself, which here looks like a credential, is never echoed.
        Assert.DoesNotContain(lines, line => line.Contains("tokenLookingSecret123"));
    }

    /// <summary>
    /// Proves a fully valid configuration produces only the single positive summary line.
    /// </summary>
    [Fact]
    public void Startup_report_accepts_a_valid_configuration()
    {
        var lines = ConfigurationPreflight.DescribeStartupReport(
            globalLoggerChannel: string.Empty,
            defaultBotLoggerChannel: LoggerChannel,
            globalBackupChannel: string.Empty,
            defaultBotBackupChannel: BackupChannel,
            xuiV3ApiBaseUrl: "https://panel.example.com:54321/");

        Assert.Equal("[ConfigurationPreflight] loggerChannel=chat-id backupChannel=chat-id", Assert.Single(lines));
    }

    /// <summary>
    /// Proves a correct destination keeps durable delivery and the acknowledge/delete behavior.
    /// </summary>
    /// <remarks>
    /// The fix must not weaken the outbox: a valid destination still commits a durable row, still delivers, and is
    /// still deleted only after Telegram accepts the message (at-least-once preserved).
    /// </remarks>
    [Fact]
    public async Task Valid_destination_keeps_durable_delivery_and_acknowledge_behavior()
    {
        await using var fixture = new BackupRecoveryTests.Fixture();
        var sender = new RecordingLogSender();
        await using var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options);
        var logger = BuildLogger(dispatcher, LoggerChannel, BackupChannel);

        logger.LogPayment("<b>پرداخت</b>");

        await BackupRecoveryTests.Until(() => sender.Texts.Count == 1);
        Assert.Equal(LoggerChannel, Assert.Single(sender.Channels));
        Assert.Equal(1, await ReadBackupRequestedAsync(fixture.Options.OutboxDatabasePath));

        // Acknowledged rows are deleted, exactly as before.
        await BackupRecoveryTests.Until(() => CountRowsAsync(fixture.Options.OutboxDatabasePath).GetAwaiter().GetResult() == 0);
        Assert.Equal(0, await CountRowsAsync(fixture.Options.OutboxDatabasePath));
    }

    /// <summary>
    /// Proves a whitespace-padded valid destination is delivered in its canonical trimmed form.
    /// </summary>
    [Fact]
    public async Task Padded_destination_is_delivered_trimmed()
    {
        await using var fixture = new BackupRecoveryTests.Fixture();
        var sender = new RecordingLogSender();
        await using var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options);
        var logger = BuildLogger(dispatcher, "  " + LoggerChannel + "  ", BackupChannel);

        logger.LogTelegramHtml("<b>audit</b>");

        await BackupRecoveryTests.Until(() => sender.Texts.Count == 1);
        Assert.Equal(LoggerChannel, Assert.Single(sender.Channels));
    }

    /// <summary>
    /// Proves a missing backup destination leaves the durable generation pending without a transport call.
    /// </summary>
    /// <remarks>
    /// The backup watermark is deliberately monotonic: a misconfigured backup destination must keep the intent
    /// pending so a later configuration change still produces the snapshot, rather than dropping it silently.
    /// </remarks>
    [Fact]
    public async Task Backup_generation_stays_pending_when_the_backup_destination_is_the_zero_sentinel()
    {
        await using var fixture = new BackupRecoveryTests.Fixture();
        var sender = new RecordingLogSender();
        await using var outbox = new TelegramLogOutbox(fixture.Options.OutboxDatabasePath);
        // A Payment row is required: only a Payment event increments the global backup generation, so only a Payment
        // event can prove the backup destination check leaves that generation pending.
        await outbox.EnqueueAsync(HistoricalRow(
            loggerChannelId: LoggerChannel,
            backupChannelId: "0",
            kind: TelegramLogDeliveryKind.Payment));

        var warning = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var dispatcher = new TelegramLogDispatcher(_ => sender, fixture.Options with
        {
            BackupDebounce = TimeSpan.Zero,
            BackupWarning = message => warning.TrySetResult(message)
        }))
        {
            Assert.Equal("[DatabaseBackup] destination unavailable; durable generation remains pending.",
                await warning.Task.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Empty(sender.Documents);
            var state = await outbox.ReadBackupAsync();
            Assert.Equal(1, state.Requested);
            Assert.Equal(0, state.Covered);
            // Clear the fixture's own intent so disposal does not wait out its production drain allowance.
            await outbox.CoverBackupAsync(1);
        }
    }

    /// <summary>Builds a production Telegram logger bound to a real durable dispatcher and the given destinations.</summary>
    /// <param name="dispatcher">Shared durable dispatcher under test.</param>
    /// <param name="loggerChannel">Fallback logger channel passed as the legacy global configuration value.</param>
    /// <param name="backupChannel">Fallback backup channel passed as the legacy global configuration value.</param>
    /// <returns>A logger whose default owned bot has no per-bot destinations, matching the production shape.</returns>
    private static TelegramLogger BuildLogger(TelegramLogDispatcher dispatcher, string loggerChannel, string backupChannel)
    {
        // The default owned bot has no per-bot values, so every resolved destination comes from the fallback pair.
        var registry = new BotRegistry(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["BotToken"] = "1:test-token" }).Build());
        return new TelegramLogger("destination-test", null, registry, new BotContextAccessor(), loggerChannel, backupChannel, dispatcher);
    }

    /// <summary>Builds a non-secret durable row that mirrors a row produced by the defective build.</summary>
    /// <param name="loggerChannelId">Persisted logger destination, deliberately malformed for these tests.</param>
    /// <param name="backupChannelId">Persisted backup destination; valid by default.</param>
    /// <param name="kind">Delivery kind; Payment is required when the test asserts backup generation behavior.</param>
    /// <returns>A Pending row ready to be inserted directly into the outbox.</returns>
    private static TelegramLogOutboxItem HistoricalRow(
        string loggerChannelId,
        string backupChannelId = BackupChannel,
        TelegramLogDeliveryKind kind = TelegramLogDeliveryKind.Html) =>
        new(0, DateTime.UtcNow, kind, kind, "vpnetiranbot",
            loggerChannelId, backupChannelId, "<b>historical audit</b>", 0, DateTime.UtcNow, null,
            TelegramLogOutboxStatus.Pending, null, null);

    /// <summary>Polls until the single outbox row reaches DeadLetter, failing on a bounded deadline.</summary>
    /// <param name="outboxPath">Fixture-owned outbox database path.</param>
    /// <returns>A task completing after the row is dead-lettered.</returns>
    private static async Task WaitForDeadLetterAsync(string outboxPath)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (await ReadStatusAsync(outboxPath) != (int)TelegramLogOutboxStatus.DeadLetter)
            await Task.Delay(10, deadline.Token);
    }

    /// <summary>Counts every outbox row, including dead-lettered rows.</summary>
    private static async Task<int> CountRowsAsync(string outboxPath)
    {
        using var db = new SqliteConnection("Data Source=" + outboxPath);
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM TelegramLogOutbox";
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    /// <summary>Reads the single outbox row's status and last error, for dead-letter assertions.</summary>
    private static async Task<(int Status, string LastError)> ReadSingleRowStateAsync(string outboxPath)
    {
        using var db = new SqliteConnection("Data Source=" + outboxPath);
        await db.OpenAsync();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Status, COALESCE(LastError, '') FROM TelegramLogOutbox";
        using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync(), "expected exactly one outbox row");
        return (reader.GetInt32(0), reader.GetString(1));
    }

    /// <summary>Reads the single outbox row's status for bounded polling.</summary>
    private static async Task<int> ReadStatusAsync(string outboxPath)
    {
        var (status, _) = await ReadSingleRowStateAsync(outboxPath);
        return status;
    }

    /// <summary>Reads the durable backup generation requested by the outbox watermark.</summary>
    private static async Task<long> ReadBackupRequestedAsync(string outboxPath)
    {
        await using var outbox = new TelegramLogOutbox(outboxPath);
        return (await outbox.ReadBackupAsync()).Requested;
    }

    /// <summary>Records the exact destinations and bodies a production delivery would have sent.</summary>
    private sealed class RecordingLogSender : ITelegramLogSender
    {
        /// <summary>Delivered message bodies; the count proves whether a transport call happened at all.</summary>
        public System.Collections.Concurrent.ConcurrentBag<string> Texts { get; } = [];

        /// <summary>Delivered destinations, in delivery order, for normalization assertions.</summary>
        public System.Collections.Concurrent.ConcurrentBag<string> Channels { get; } = [];

        /// <summary>Uploaded document names; unused by destination assertions but required by the interface.</summary>
        public System.Collections.Concurrent.ConcurrentBag<string> Documents { get; } = [];

        /// <inheritdoc />
        public Task SendMessage(string channelId, string message, Telegram.Bot.Types.Enums.ParseMode? parseMode, CancellationToken cancellationToken)
        {
            Texts.Add(message);
            Channels.Add(channelId);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task SendDocument(string channelId, string fileName, Stream content, CancellationToken cancellationToken)
        {
            Documents.Add(fileName);
            return Task.CompletedTask;
        }
    }
}
