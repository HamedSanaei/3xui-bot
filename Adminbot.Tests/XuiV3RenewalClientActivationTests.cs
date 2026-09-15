using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Adminbot.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

/// <summary>
/// Pins the post-renewal activation contract that makes "successful renewal implies an active account" true for every
/// renewal entry point.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these tests protect against.</b> 3x-ui disables a client when it expires or exhausts its quota, and
/// <c>enable</c> is the panel's only instantaneous admission gate. A renewal that raises the quota and extends the
/// expiry can therefore be recorded by the panel while the customer's account stays OFF: the payment succeeds, the
/// renewal succeeds, and the account does not work. Every scenario below drives the real production renewal payload from
/// <see cref="XuiV3RenewalPolicy" /> and the real activation step against <see cref="FakeUpstreamPanel" />, whose client
/// update can be told to accept the envelope while ignoring the admission flag - exactly that symptom.
/// </para>
/// <para>
/// <b>Scenario mapping.</b> A already-enabled client stays enabled and is not written to; B an expired disabled client
/// is enabled after the renewal; C a quota-exhausted disabled client is enabled after the renewal; D a renewal the panel
/// rejected enables nothing at all. The remaining tests cover fail-closed reads, idempotency, read-back verification,
/// the documented ordering with the traffic reset, log evidence, ownership preservation, and failure containment.
/// </para>
/// </remarks>
public sealed class XuiV3RenewalClientActivationTests
{
    /// <summary>One binary gigabyte in bytes, matching the renewal policy's traffic unit.</summary>
    private const long Gib = 1024L * 1024L * 1024L;

    /// <summary>Numeric Telegram id of the customer or administrator whose renewal is being replayed.</summary>
    private const long ActorTelegramUserId = 4242;

    /// <summary>
    /// Scenario A: a client the panel already reports as enabled is left completely untouched by the activation step.
    /// </summary>
    /// <returns>A task completing after the no-mutation assertions.</returns>
    /// <remarks>
    /// Protects the idempotency requirement: the common renewal path must not send a second write that could clobber
    /// quota, expiry, or ownership, so the assertion is on the panel's own request counters rather than on our logs.
    /// </remarks>
    [Fact]
    public async Task Already_enabled_client_is_left_untouched_by_a_renewal()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        var configuration = BuildConfiguration();
        const string email = "renew-enabled";

        await SeedClientAsync(panel, configuration, email, enable: true, quotaBytes: 30 * Gib, expiryTimeMs: FutureExpiryMs());
        await ApplyRenewalMutationAsync(panel, configuration, email);
        var writesBefore = panel.UpdateRequestCount;
        var admissionsBefore = panel.EnableMutationCount;

        var result = await ActivateAsync(panel, configuration, email, renewalApplied: true);

        Assert.Equal(XuiV3RenewalActivationStatus.AlreadyEnabled, result.Status);
        Assert.True(result.IsActive);
        Assert.False(result.MutationIssued);
        // No additional client update was sent, and the panel never changed the admission flag.
        Assert.Equal(writesBefore, panel.UpdateRequestCount);
        Assert.Equal(admissionsBefore, panel.EnableMutationCount);
        Assert.True(ReadEnabled(panel, email));
    }

    /// <summary>
    /// Scenario B: a client disabled because it had expired is enabled after a successful renewal.
    /// </summary>
    /// <returns>A task completing after the repair assertions.</returns>
    /// <remarks>
    /// The seeded expiry is in the past, which is what makes 3x-ui disable the client. The fake panel is told to ignore
    /// the enable field of the renewal update so the defect under test is reproduced exactly: quota and expiry are
    /// renewed while the account stays off, and the activation step is the only thing that restores service.
    /// </remarks>
    [Fact]
    public async Task Expired_disabled_client_is_enabled_after_a_successful_renewal()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        var configuration = BuildConfiguration();
        const string email = "renew-expired";

        await SeedClientAsync(panel, configuration, email, enable: false, quotaBytes: 30 * Gib, expiryTimeMs: PastExpiryMs());
        panel.IgnoreClientEnableWrites = true;
        await ApplyRenewalMutationAsync(panel, configuration, email);

        // The renewal was accepted and raised the entitlement, yet the client is still disabled: the production symptom.
        Assert.False(ReadEnabled(panel, email));
        var renewedQuota = ReadQuota(panel, email);
        var renewedExpiry = ReadExpiry(panel, email);
        Assert.True(renewedExpiry > PastExpiryMs());

        panel.IgnoreClientEnableWrites = false;
        var admissionsBefore = panel.EnableMutationCount;
        var runtimeAdmissionsBefore = panel.RuntimeAddUserCount;
        var result = await ActivateAsync(panel, configuration, email, renewalApplied: true);

        Assert.Equal(XuiV3RenewalActivationStatus.Enabled, result.Status);
        Assert.True(result.IsActive);
        Assert.True(result.MutationIssued);
        Assert.True(ReadEnabled(panel, email));
        Assert.Equal(admissionsBefore + 1, panel.EnableMutationCount);
        // The panel admitted the credential again, which is the outcome the customer paid for.
        Assert.Equal(runtimeAdmissionsBefore + 1, panel.RuntimeAddUserCount);
        // And the enable write preserved the renewed entitlement instead of resetting it to what the panel had read.
        Assert.Equal(renewedQuota, ReadQuota(panel, email));
        Assert.Equal(renewedExpiry, ReadExpiry(panel, email));
    }

    /// <summary>
    /// Scenario C: a client disabled because it exhausted its traffic quota is enabled after a successful renewal.
    /// </summary>
    /// <returns>A task completing after the repair assertions.</returns>
    /// <remarks>
    /// Here the expiry is still in the future and the consumed usage already exceeds the quota, which is the other way
    /// 3x-ui disables a client. The renewal path must treat both causes identically, because the panel state it repairs
    /// is the same disable flag.
    /// </remarks>
    [Fact]
    public async Task Traffic_exhausted_disabled_client_is_enabled_after_a_successful_renewal()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        var configuration = BuildConfiguration();
        const string email = "renew-traffic-exhausted";

        // 40 GiB consumed against a 30 GiB quota, with time still left on the clock.
        await SeedClientAsync(panel, configuration, email, enable: false, quotaBytes: 30 * Gib, expiryTimeMs: FutureExpiryMs(), usedBytes: 40 * Gib);
        panel.IgnoreClientEnableWrites = true;
        await ApplyRenewalMutationAsync(panel, configuration, email);
        Assert.False(ReadEnabled(panel, email));

        panel.IgnoreClientEnableWrites = false;
        var admissionsBefore = panel.EnableMutationCount;
        var result = await ActivateAsync(panel, configuration, email, renewalApplied: true);

        Assert.Equal(XuiV3RenewalActivationStatus.Enabled, result.Status);
        Assert.True(result.IsActive);
        Assert.True(ReadEnabled(panel, email));
        Assert.Equal(admissionsBefore + 1, panel.EnableMutationCount);
    }

    /// <summary>
    /// Scenario D: a renewal the panel did not accept must never enable the client.
    /// </summary>
    /// <returns>A task completing after the no-write assertions.</returns>
    /// <remarks>
    /// Every renewal entry point passes the panel's own verdict into the activation step, so a rejected update reaches
    /// this method as <c>RenewalApplied: false</c>. The step must then send no request at all: an account whose renewal
    /// failed must stay exactly as the panel has it, and this test asserts the panel saw zero writes of any kind.
    /// </remarks>
    [Fact]
    public async Task Rejected_renewal_never_enables_the_client()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        var configuration = BuildConfiguration();
        const string email = "renew-rejected";

        await SeedClientAsync(panel, configuration, email, enable: false, quotaBytes: 30 * Gib, expiryTimeMs: PastExpiryMs());
        var quotaBefore = ReadQuota(panel, email);
        var writesBefore = panel.UpdateRequestCount;
        var admissionsBefore = panel.EnableMutationCount;

        var result = await ActivateAsync(panel, configuration, email, renewalApplied: false);

        Assert.Equal(XuiV3RenewalActivationStatus.SkippedRenewalNotApplied, result.Status);
        Assert.False(result.IsActive);
        Assert.False(result.MutationIssued);
        Assert.Equal(writesBefore, panel.UpdateRequestCount);
        Assert.Equal(admissionsBefore, panel.EnableMutationCount);
        Assert.False(ReadEnabled(panel, email));
        Assert.Equal(quotaBefore, ReadQuota(panel, email));
    }

    /// <summary>
    /// A client whose state cannot be read is never mutated, because the step cannot know whether it is disabled.
    /// </summary>
    /// <returns>A task completing after the fail-closed assertions.</returns>
    /// <remarks>Protects the rule that an unreadable account is reported, never guessed at and never written to.</remarks>
    [Fact]
    public async Task Unreadable_client_is_never_mutated()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        var configuration = BuildConfiguration();

        var writesBefore = panel.UpdateRequestCount;
        var admissionsBefore = panel.EnableMutationCount;

        var result = await ActivateAsync(panel, configuration, "missing-client", renewalApplied: true);

        Assert.Equal(XuiV3RenewalActivationStatus.ClientUnreadable, result.Status);
        Assert.False(result.IsActive);
        Assert.False(result.MutationIssued);
        Assert.Equal(writesBefore, panel.UpdateRequestCount);
        Assert.Equal(admissionsBefore, panel.EnableMutationCount);
    }

    /// <summary>
    /// Running the activation step twice for the same renewal performs the repair once.
    /// </summary>
    /// <returns>A task completing after the replay assertions.</returns>
    /// <remarks>
    /// This is the duplicate-confirmation and crash-take-over case: the second call must observe an enabled client and
    /// issue nothing, so a repeated callback cannot produce a second admission or touch the entitlement again.
    /// </remarks>
    [Fact]
    public async Task Repeated_activation_is_idempotent()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        var configuration = BuildConfiguration();
        const string email = "renew-idempotent";

        await SeedClientAsync(panel, configuration, email, enable: false, quotaBytes: 30 * Gib, expiryTimeMs: PastExpiryMs());

        var first = await ActivateAsync(panel, configuration, email, renewalApplied: true);
        var updatesAfterFirst = panel.UpdateRequestCount;
        var second = await ActivateAsync(panel, configuration, email, renewalApplied: true);

        Assert.Equal(XuiV3RenewalActivationStatus.Enabled, first.Status);
        Assert.Equal(XuiV3RenewalActivationStatus.AlreadyEnabled, second.Status);
        Assert.False(second.MutationIssued);
        Assert.Equal(updatesAfterFirst, panel.UpdateRequestCount);
        Assert.Equal(1, panel.EnableMutationCount);
    }

    /// <summary>
    /// An enable update the panel accepts but does not reflect is reported as unproven rather than assumed to have worked.
    /// </summary>
    /// <returns>A task completing after the read-back assertions.</returns>
    /// <remarks>
    /// Protects the read-back requirement: the step trusts the panel's stored state, not its success envelope, so a
    /// panel that answers <c>success</c> while keeping the client disabled produces an operator warning instead of a
    /// false claim that the account was activated.
    /// </remarks>
    [Fact]
    public async Task Accepted_enable_that_the_panel_does_not_reflect_is_reported_unproven()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        var configuration = BuildConfiguration();
        const string email = "renew-not-reflected";

        await SeedClientAsync(panel, configuration, email, enable: false, quotaBytes: 30 * Gib, expiryTimeMs: PastExpiryMs());
        // The panel keeps ignoring the admission flag, so even the repair write is accepted and not applied.
        panel.IgnoreClientEnableWrites = true;

        var result = await ActivateAsync(panel, configuration, email, renewalApplied: true);

        Assert.Equal(XuiV3RenewalActivationStatus.EnableNotReflected, result.Status);
        Assert.True(result.MutationIssued);
        Assert.False(result.IsActive);
        Assert.False(ReadEnabled(panel, email));
    }

    /// <summary>
    /// A traffic reset issued after the activation cannot put a proven-active renewed client back into a disabled state.
    /// </summary>
    /// <returns>A task completing after the ordering assertions.</returns>
    /// <remarks>
    /// This pins the documented ordering argument of the shared helper: the reset used by the renewal flows can only
    /// admit or enable a client, never disable one, so placing the activation before it cannot lose the repair, and the
    /// reset must not remove a credential the customer has paid for.
    /// </remarks>
    [Fact]
    public async Task Traffic_reset_after_activation_cannot_disable_the_client()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        var configuration = BuildConfiguration();
        const string email = "renew-reset-after";

        await SeedClientAsync(panel, configuration, email, enable: false, quotaBytes: 30 * Gib, expiryTimeMs: PastExpiryMs());
        var activation = await ActivateAsync(panel, configuration, email, renewalApplied: true);
        Assert.True(activation.IsActive);

        var reset = await ApiServicev3.ResetClientTrafficAsync(panel.ServerInfo, configuration, email);

        Assert.True(reset.Success, reset.Msg);
        Assert.True(ReadEnabled(panel, email));
        Assert.Equal(1, panel.EnableMutationCount);
        Assert.Equal(0, panel.RuntimeRemoveUserCount);
    }

    /// <summary>
    /// The enable write preserves the account owner instead of transferring it to the payer.
    /// </summary>
    /// <returns>A task completing after the ownership assertions.</returns>
    /// <remarks>
    /// A renewal is paid by one Telegram user and can target an account owned by another. The activation step must not
    /// become an ownership transfer, so the panel-resolved owner (and the metadata owner inside the comment) must be
    /// the same after the repair as before it.
    /// </remarks>
    [Fact]
    public async Task Enable_write_preserves_the_panel_owner()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        var configuration = BuildConfiguration();
        const string email = "renew-owner";
        const long panelOwnerTelegramUserId = 999;

        await SeedClientAsync(
            panel,
            configuration,
            email,
            enable: false,
            quotaBytes: 30 * Gib,
            expiryTimeMs: PastExpiryMs(),
            ownerTelegramUserId: panelOwnerTelegramUserId);

        var result = await ActivateAsync(panel, configuration, email, renewalApplied: true);

        Assert.True(result.IsActive);
        var stored = panel.ReadClient(email);
        Assert.Equal(panelOwnerTelegramUserId, stored["tgId"]!.Value<long>());
        var metadata = JObject.Parse(stored["comment"]!.Value<string>()!);
        Assert.Equal(panelOwnerTelegramUserId, metadata["telegramUserId"]!.Value<long>());
    }

    /// <summary>
    /// The activation step reports the disabled and restored states through the caller's logger.
    /// </summary>
    /// <returns>A task completing after the log assertions.</returns>
    /// <remarks>
    /// Operators diagnose a renewal that left an account off from these lines alone, so the disabled-client repair, the
    /// restored state, and the rejection warning are asserted instead of being left to chance.
    /// </remarks>
    [Fact]
    public async Task Activation_logs_the_disabled_and_restored_states()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        var configuration = BuildConfiguration();
        const string email = "renew-logs";

        await SeedClientAsync(panel, configuration, email, enable: false, quotaBytes: 30 * Gib, expiryTimeMs: PastExpiryMs());
        var repairLogger = new RecordingLogger();

        var repaired = await ActivateAsync(panel, configuration, email, renewalApplied: true, repairLogger);

        Assert.Equal(XuiV3RenewalActivationStatus.Enabled, repaired.Status);
        Assert.Contains(repairLogger.Entries, entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains("Client was disabled after previous expiration", StringComparison.Ordinal));
        Assert.Contains(repairLogger.Entries, entry =>
            entry.Level == LogLevel.Information &&
            entry.Message.Contains("Client enable status restored successfully", StringComparison.Ordinal));

        // A skipped renewal must stay silent about the panel and only record the decision.
        var skippedLogger = new RecordingLogger();
        var skipped = await ActivateAsync(panel, configuration, "unused-client", renewalApplied: false, skippedLogger);
        Assert.Equal(XuiV3RenewalActivationStatus.SkippedRenewalNotApplied, skipped.Status);
        Assert.DoesNotContain(skippedLogger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    /// <summary>
    /// A panel transport failure inside the activation step is contained instead of breaking an already-applied renewal.
    /// </summary>
    /// <returns>A task completing after the containment assertions.</returns>
    /// <remarks>
    /// The step runs after the renewal is durable and after the customer paid. A panel outage must therefore surface as
    /// an unproven activation with an operator warning, not as an exception that would fail the renewal or its
    /// settlement, and it must not attempt the enable write without a verified state.
    /// </remarks>
    [Fact]
    public async Task Panel_transport_failure_is_reported_as_unverified()
    {
        var configuration = BuildConfiguration();
        var unreachablePanel = new ServerInfo { Url = "http://127.0.0.1:1", ApiToken = "test-only" };
        var logger = new RecordingLogger();

        var result = await XuiV3RenewalClientActivation.EnsureClientEnabledAfterRenewalAsync(
            new XuiV3RenewalActivationRequest(
                unreachablePanel,
                configuration,
                "unreachable@example.test",
                RenewalApplied: true,
                RenewalKind: "user-renew",
                ActorTelegramUserId: ActorTelegramUserId,
                Logger: logger));

        Assert.Equal(XuiV3RenewalActivationStatus.ActivationUnverified, result.Status);
        Assert.False(result.IsActive);
        Assert.False(result.MutationIssued);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    /// <summary>Creates the panel connection settings used by these tests, with retries disabled for determinism.</summary>
    /// <returns>In-memory configuration with no live panel or provider credentials.</returns>
    private static IConfiguration BuildConfiguration()
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["XuiV3ApiToken"] = "test-only",
            ["xuiV3TransientRetryCount"] = "0",
            ["xuiV3RequestTimeoutSeconds"] = "5"
        }).Build();

    /// <summary>Writes a realistic panel client record through the panel's own update contract.</summary>
    /// <param name="panel">Started fake panel.</param>
    /// <param name="configuration">Configuration passed to the API layer.</param>
    /// <param name="email">Client email to create.</param>
    /// <param name="enable">Whether the client starts admitted.</param>
    /// <param name="quotaBytes">Exact quota in bytes the client carries.</param>
    /// <param name="expiryTimeMs">Absolute expiry in Unix milliseconds, or a negative first-connection duration.</param>
    /// <param name="usedBytes">Consumed upload plus download bytes to record.</param>
    /// <param name="ownerTelegramUserId">Panel owner id to seed, or <c>0</c> when the account has no owner.</param>
    /// <returns>A task completing after the seed write is accepted.</returns>
    /// <remarks>
    /// Seeding through the panel contract rather than by writing the dictionary directly keeps the tests honest about
    /// the payload shape a real panel stores, including the owner id and the JSON metadata comment.
    /// </remarks>
    private static async Task SeedClientAsync(
        FakeUpstreamPanel panel,
        IConfiguration configuration,
        string email,
        bool enable,
        long quotaBytes,
        long expiryTimeMs,
        long usedBytes = 0,
        long ownerTelegramUserId = 0)
    {
        panel.SeedClient(email, up: usedBytes, down: 0, enable: enable);
        panel.SetClientIdentity(email, $"uuid-{email}", $"sub-{email}");

        var seedPayload = new XuiV3ClientPayload
        {
            Email = email,
            Uuid = $"uuid-{email}",
            SubId = $"sub-{email}",
            TotalGB = quotaBytes,
            ExpiryTime = expiryTimeMs,
            Enable = enable,
            TgId = ownerTelegramUserId,
            Comment = ownerTelegramUserId > 0
                ? $"{{\"telegramUserId\":{ownerTelegramUserId}}}"
                : null
        };

        var response = await ApiServicev3.UpdateClientAsync(panel.ServerInfo, configuration, email, seedPayload);
        Assert.True(response.Success, response.Msg);
    }

    /// <summary>Applies the exact payload the production renewal path sends for a metered account.</summary>
    /// <param name="panel">Started fake panel.</param>
    /// <param name="configuration">Configuration passed to the API layer.</param>
    /// <param name="email">Client email being renewed.</param>
    /// <returns>A task completing after the renewal update is accepted.</returns>
    /// <remarks>
    /// The payload comes from the shared <see cref="XuiV3RenewalPolicy" />, so these tests exercise the same payload
    /// that production sends, including its own <c>enable = true</c> intent. That is what makes the disabled-client
    /// scenarios meaningful: the panel can accept that payload and still leave the account off.
    /// </remarks>
    private static async Task ApplyRenewalMutationAsync(FakeUpstreamPanel panel, IConfiguration configuration, string email)
    {
        var clientResponse = await ApiServicev3.GetClientAsync(panel.ServerInfo, configuration, email);
        Assert.True(clientResponse.Success, clientResponse.Msg);

        var renewal = XuiV3RenewalPolicy.CalculateAdmin(
            clientResponse.Obj,
            service: null,
            addTrafficGb: 50,
            addDays: 30,
            action: "admin-renew",
            actorTelegramUserId: ActorTelegramUserId);

        var update = await ApiServicev3.UpdateClientAsync(panel.ServerInfo, configuration, email, renewal.Payload);
        Assert.True(update.Success, update.Msg);
    }

    /// <summary>Runs the shared activation step for one client.</summary>
    /// <param name="panel">Started fake panel.</param>
    /// <param name="configuration">Configuration passed to the API layer.</param>
    /// <param name="email">Client email the step should act on.</param>
    /// <param name="renewalApplied">Panel verdict passed by the renewal entry point.</param>
    /// <param name="logger">Optional recording logger; a null logger is used when absent.</param>
    /// <returns>The activation result.</returns>
    private static Task<XuiV3RenewalActivationResult> ActivateAsync(
        FakeUpstreamPanel panel,
        IConfiguration configuration,
        string email,
        bool renewalApplied,
        ILogger? logger = null)
        => XuiV3RenewalClientActivation.EnsureClientEnabledAfterRenewalAsync(
            new XuiV3RenewalActivationRequest(
                panel.ServerInfo,
                configuration,
                email,
                RenewalApplied: renewalApplied,
                RenewalKind: "admin-renew",
                ActorTelegramUserId: ActorTelegramUserId,
                Logger: logger ?? NullLogger.Instance));

    /// <summary>Reads the panel's admission flag for a client.</summary>
    /// <param name="panel">Started fake panel.</param>
    /// <param name="email">Client email.</param>
    /// <returns><c>true</c> when the panel holds the client enabled.</returns>
    private static bool ReadEnabled(FakeUpstreamPanel panel, string email)
        => panel.ReadClient(email)["enable"]!.Value<bool>();

    /// <summary>Reads the panel's stored quota for a client.</summary>
    /// <param name="panel">Started fake panel.</param>
    /// <param name="email">Client email.</param>
    /// <returns>Quota in bytes.</returns>
    private static long ReadQuota(FakeUpstreamPanel panel, string email)
        => panel.ReadClient(email)["totalGB"]!.Value<long>();

    /// <summary>Reads the panel's stored expiry for a client.</summary>
    /// <param name="panel">Started fake panel.</param>
    /// <param name="email">Client email.</param>
    /// <returns>Expiry in milliseconds or a negative first-connection duration.</returns>
    private static long ReadExpiry(FakeUpstreamPanel panel, string email)
        => panel.ReadClient(email)["expiryTime"]!.Value<long>();

    /// <summary>Builds an expiry one day in the future.</summary>
    /// <returns>Absolute Unix milliseconds.</returns>
    private static long FutureExpiryMs() => DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();

    /// <summary>Builds an expiry one day in the past.</summary>
    /// <returns>Absolute Unix milliseconds.</returns>
    private static long PastExpiryMs() => DateTimeOffset.UtcNow.AddDays(-1).ToUnixTimeMilliseconds();

    /// <summary>Captures log entries so the operator-visible activation messages can be asserted.</summary>
    private sealed class RecordingLogger : ILogger
    {
        /// <summary>Gets every captured entry in order, with its level and formatted message.</summary>
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        /// <summary>Disposable returned for scopes, which these tests do not inspect.</summary>
        private sealed class NullScope : IDisposable
        {
            /// <summary>Shared instance; nothing is stored per scope.</summary>
            public static readonly NullScope Instance = new();

            /// <inheritdoc />
            public void Dispose()
            {
            }
        }
    }
}
