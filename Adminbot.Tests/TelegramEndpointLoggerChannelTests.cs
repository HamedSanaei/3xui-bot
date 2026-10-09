using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Text;
using System.Text.Json;
using Adminbot.Domain;
using Adminbot.Domain.Logging;
using Adminbot.Services.TelegramEndpoints;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>The existing global or default logger destination delivers one incident with no notifier id or private-admin addressing.</summary>
    /// <param name="defaultFallback">Whether the root logger is absent and the existing default-owned logger supplies the target.</param>
    /// <returns>A task completing after actual SDK Cloud routing, negative destination, acknowledgment and incident-only receipt are asserted.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Endpoint_logger_existing_global_or_default_channel_delivers_without_extra_id(bool defaultFallback)
    {
        using var harness = new LoggerChannelHarness(defaultFallback ? null : "@existing_logger",
            defaultFallback ? "-100711000" : "@unused_logger");
        await harness.InitializeAsync();
        await harness.QueueAsync();
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        Assert.False(await worker.ProcessOneAsync(default));
        var row = await harness.AlertAsync();
        Assert.Equal(TelegramEndpointAlertStatus.Delivered, row.Status);
        Assert.Equal(-100711000, row.DestinationChatId);
        Assert.NotNull(row.DeliveredAtUtc);
        Assert.NotNull(row.SendStartedAtUtc);
        var send = Assert.Single(harness.Http.Calls, call => call.Method == "sendMessage");
        Assert.Equal(123456, send.BotIdentity);
        Assert.Equal("api.telegram.org", send.Host);
        Assert.Equal("-100711000", send.Chat);
        Assert.All(harness.Http.Calls, call => Assert.Equal("api.telegram.org", call.Host));
        Assert.NotEqual("711", send.Chat);
        Assert.Equal(3, harness.Http.Calls.Count(call => call.Method != "sendMessage"));
        await using var db = harness.Databases.Users.CreateDbContext();
        Assert.Single(await db.TelegramEndpointAlertReceipts.ToListAsync());
        Assert.Empty(await db.WalletLedgerEntries.ToListAsync());
        Assert.Empty(await db.ReferralPaymentEvents.ToListAsync());
    }

    /// <summary>Only an actual channel and the exact bot's explicit posting administrator or owner permission authorize a send.</summary>
    /// <param name="mode">The channel/member denial or accepted-owner fixture mode.</param>
    /// <returns>A task completing after the genuine SDK membership response is consumed without private or group delivery.</returns>
    [Theory]
    [InlineData("owner")]
    [InlineData("member")]
    [InlineData("restricted")]
    [InlineData("denied_admin")]
    [InlineData("wrong_member")]
    [InlineData("wrong_identity")]
    [InlineData("supergroup")]
    [InlineData("private")]
    [InlineData("positive_channel")]
    [InlineData("positive_config")]
    public async Task Endpoint_logger_requires_actual_channel_and_exact_bot_post_authority(string mode)
    {
        using var harness = new LoggerChannelHarness(mode == "positive_config" ? "711" : "@existing_logger", senderCount: 1);
        await harness.InitializeAsync();
        harness.Http.Mode = mode;
        await harness.QueueAsync();
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        var row = await harness.AlertAsync();
        Assert.Equal(mode == "owner" ? TelegramEndpointAlertStatus.Delivered : TelegramEndpointAlertStatus.Pending, row.Status);
        Assert.Equal(mode == "owner" ? 1 : 0, harness.Http.Calls.Count(call => call.Method == "sendMessage"));
        if (mode != "owner") Assert.Null(row.SendStartedAtUtc);
        Assert.All(harness.Http.Calls.Where(call => call.Method == "sendMessage"), call => Assert.StartsWith("-", call.Chat));
    }

    /// <summary>A denied or definitively inaccessible default-owned sender does not prevent a later authorized Cloud sender from delivering.</summary>
    /// <param name="readRejection">Whether getChat returns a definitive 403 instead of an explicit membership permission denial.</param>
    /// <returns>A task completing after the second bot sends once and every actual API request remains on Cloud.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Endpoint_logger_denied_default_tries_authorized_cloud_alternate(bool readRejection)
    {
        using var harness = new LoggerChannelHarness();
        await harness.InitializeAsync();
        harness.Http.DeniedBots.Add(123456);
        if (readRejection) harness.Http.ReadDeniedBots.Add(123456);
        await harness.QueueAsync();
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        Assert.Equal(654321, Assert.Single(harness.Http.Calls, call => call.Method == "sendMessage").BotIdentity);
        Assert.Equal(TelegramEndpointAlertStatus.Delivered, (await harness.AlertAsync()).Status);
    }

    /// <summary>Bounded deterministic sender pages rotate rather than allowing eight permanently denied early candidates to starve an authorized bot.</summary>
    /// <returns>A task completing after a later page reaches the authorized sender without exceeding eight getMe probes per claim.</returns>
    [Fact]
    public async Task Endpoint_logger_bounded_candidate_scan_does_not_starve_later_sender()
    {
        using var harness = new LoggerChannelHarness(senderCount: 20);
        await harness.InitializeAsync();
        foreach (var bot in harness.Registry.Bots.Take(19))
            harness.Http.DeniedBots.Add(TelegramBotTokenIdentity.ExtractBotId(bot.Token)!.Value);
        await harness.QueueAsync();
        using var worker = harness.Worker();
        for (var attempt = 0; attempt < 4 && (await harness.AlertAsync()).Status != TelegramEndpointAlertStatus.Delivered; attempt++)
        {
            var before = harness.Http.Calls.Count(call => call.Method == "getMe");
            Assert.True(await worker.ProcessOneAsync(default));
            Assert.InRange(harness.Http.Calls.Count(call => call.Method == "getMe") - before, 1, 8);
            harness.Advance(TimeSpan.FromMinutes(10));
        }
        Assert.Equal(TelegramEndpointAlertStatus.Delivered, (await harness.AlertAsync()).Status);
        Assert.Single(harness.Http.Calls, call => call.Method == "sendMessage");
    }

    /// <summary>Local, migrating, fenced and unhydrated tokens never receive a Cloud probe; verified Cloud recovery is eligible despite previous Local history.</summary>
    /// <param name="mode">Current runtime authority state under test.</param>
    /// <returns>A task completing after the genuine worker either sends through verified Cloud or safely retains an untouched Pending intent.</returns>
    [Theory]
    [InlineData("local")]
    [InlineData("migrating")]
    [InlineData("fenced")]
    [InlineData("unhydrated")]
    [InlineData("recovered")]
    public async Task Endpoint_logger_obeys_current_cloud_runtime_authority(string mode)
    {
        using var harness = new LoggerChannelHarness(senderCount: 1);
        if (mode != "unhydrated") await harness.InitializeAsync();
        if (mode == "local") harness.Gate.Publish(EndpointRuntimeState("logger-000", 123456, TelegramEndpointType.Local, 2));
        if (mode == "migrating")
        {
            var state = EndpointRuntimeState("logger-000", 123456, TelegramEndpointType.Cloud, 2);
            state.MigrationState = TelegramEndpointMigrationState.CheckingLocal;
            harness.Gate.Publish(state);
        }
        if (mode == "fenced") harness.Gate.Fence("logger-000", 123456);
        if (mode == "recovered")
        {
            var state = await harness.Store.GetOrCreateAsync("logger-000", 123456, default);
            state.DesiredEndpoint = state.EffectiveEndpoint = TelegramEndpointType.Local;
            state.MigrationState = TelegramEndpointMigrationState.Local;
            state.Generation = 2;
            Assert.True(await harness.Store.TrySaveAsync(state, state.Revision, "migration_succeeded"));
            harness.Gate.Publish(state);
            state.EffectiveEndpoint = TelegramEndpointType.Cloud;
            state.MigrationState = TelegramEndpointMigrationState.CloudRecovered;
            state.Generation = 3;
            state.OperationId = Guid.NewGuid().ToString("N");
            Assert.True(await harness.Store.TrySaveAsync(state, state.Revision, "cloud_recovered"));
            harness.Gate.Publish(state);
        }
        await harness.QueueAsync();
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        var row = await harness.AlertAsync();
        Assert.Equal(mode == "recovered" ? TelegramEndpointAlertStatus.Delivered : TelegramEndpointAlertStatus.Pending, row.Status);
        if (mode != "recovered")
        {
            Assert.Empty(harness.Http.Calls);
            Assert.Equal(0, row.Attempts);
            Assert.Equal("transport_unavailable", row.ErrorCategory);
        }
    }

    /// <summary>Tenant, assistant, disabled and tokenless candidates cannot borrow a default token or authorize endpoint incident delivery.</summary>
    /// <returns>A task completing after conservative sender filtering issues no actual SDK request.</returns>
    [Fact]
    public async Task Endpoint_logger_accepts_enabled_owned_nonassistant_senders_only()
    {
        using var harness = new LoggerChannelHarness(senderCount: 4);
        await harness.InitializeAsync();
        harness.Replace("logger-000", 123456, enabled: false);
        harness.Replace("logger-001", 654321, type: BotInstanceTypes.Tenant);
        harness.Replace("logger-002", 654322, type: BotInstanceTypes.SalesAssistant);
        harness.Registry.Upsert(new BotInstance { Id = "logger-003", Token = "", Enabled = true, Type = BotInstanceTypes.Owned });
        await harness.QueueAsync();
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        Assert.Empty(harness.Http.Calls);
        Assert.Equal(0, (await harness.AlertAsync()).Attempts);
    }

    /// <summary>Exact identity, same-identity token rotation, disablement and migration races after a read prevent the durable send marker.</summary>
    /// <param name="mode">Registry or generation race triggered by an actual identity or permission response.</param>
    /// <param name="readMethod">The actual SDK read boundary after which current authority changes.</param>
    /// <returns>A task completing after a genuine consumer recheck blocks the marker and every send.</returns>
    [Theory]
    [InlineData("identity", "getMe")]
    [InlineData("token", "getMe")]
    [InlineData("disabled", "getMe")]
    [InlineData("generation", "getMe")]
    [InlineData("identity", "getChatMember")]
    [InlineData("token", "getChatMember")]
    [InlineData("disabled", "getChatMember")]
    [InlineData("generation", "getChatMember")]
    public async Task Endpoint_logger_rechecks_sender_after_actual_read(string mode, string readMethod)
    {
        using var harness = new LoggerChannelHarness(senderCount: 1);
        await harness.InitializeAsync();
        harness.Http.AfterRead = method =>
        {
            if (method != readMethod) return;
            if (mode == "identity") harness.Replace("logger-000", 999888);
            if (mode == "token") harness.Replace("logger-000", 123456, tokenSuffix: 'z');
            if (mode == "disabled") harness.Replace("logger-000", 123456, enabled: false);
            if (mode == "generation")
            {
                var state = EndpointRuntimeState("logger-000", 123456, TelegramEndpointType.Local, 2);
                state.MigrationState = TelegramEndpointMigrationState.SwitchingToLocal;
                harness.Gate.Publish(state);
            }
        };
        await harness.QueueAsync();
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        Assert.Equal(readMethod == "getMe" ? 1 : 3, harness.Http.Calls.Count);
        var row = await harness.AlertAsync();
        Assert.Equal(TelegramEndpointAlertStatus.Pending, row.Status);
        Assert.Null(row.SendStartedAtUtc);
        Assert.Equal(0, row.Attempts);
    }

    /// <summary>Live logger edits during channel membership verification invalidate the resolved username before it is frozen.</summary>
    /// <returns>A task completing after no marker or send is produced for the stale target, followed by one fresh authorized resolution.</returns>
    [Fact]
    public async Task Endpoint_logger_live_target_change_before_marker_requires_new_resolution()
    {
        using var harness = new LoggerChannelHarness(senderCount: 1);
        await harness.InitializeAsync();
        harness.Http.AfterRead = method => { if (method == "getChatMember") harness.Live["loggerChannel"] = "@changed_logger"; };
        await harness.QueueAsync();
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        var before = await harness.AlertAsync();
        Assert.Equal(TelegramEndpointAlertStatus.Pending, before.Status);
        Assert.Null(before.DestinationChatId);
        Assert.Null(before.SendStartedAtUtc);
        harness.Http.AfterRead = null;
        harness.Advance(TimeSpan.FromMinutes(10));
        Assert.True(await worker.ProcessOneAsync(default));
        Assert.Equal(-100722000, Assert.Single(harness.Http.Calls, call => call.Method == "sendMessage").NumericChat);
    }

    /// <summary>A prerequisite lost across the SQLite send-marker await is still proven pre-dispatch and must return Pending rather than uncertainty.</summary>
    /// <param name="targetChange">Whether to change the live target instead of fencing sender admission after the real SQLite update.</param>
    /// <returns>A task completing after the exclusive durable marker is safely released without any SDK send.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Endpoint_logger_post_marker_predispatch_loss_is_pending(bool targetChange)
    {
        using var harness = new LoggerChannelHarness(senderCount: 1);
        await harness.InitializeAsync();
        harness.Boundary.AfterMarker = () =>
        {
            if (targetChange) harness.Live["loggerChannel"] = "@changed_logger";
            else harness.Gate.Fence("logger-000", 123456);
        };
        await harness.QueueAsync();
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        Assert.Equal(1, harness.Boundary.Markers);
        Assert.DoesNotContain(harness.Http.Calls, call => call.Method == "sendMessage");
        var row = await harness.AlertAsync();
        Assert.Equal(TelegramEndpointAlertStatus.Pending, row.Status);
        Assert.Null(row.SendStartedAtUtc);
        Assert.Null(row.SendDeadlineAtUtc);
        Assert.Equal(-100711000, row.DestinationChatId);
        Assert.Equal(0, row.Attempts);
    }

    /// <summary>Missing logger, unavailable channel and missing Cloud sender survive many scans and restart without consuming attempts.</summary>
    /// <param name="mode">The closed missing-prerequisite condition.</param>
    /// <returns>A task completing after durable Pending intent and fixed local-only diagnostics survive beyond the configured real-failure cap.</returns>
    [Theory]
    [InlineData("logger_unconfigured")]
    [InlineData("logger_unavailable")]
    [InlineData("transport_unavailable")]
    public async Task Endpoint_logger_missing_prerequisites_remain_pending_indefinitely(string mode)
    {
        using var harness = new LoggerChannelHarness(mode == "logger_unconfigured" ? null : "@existing_logger", senderCount: 1, maxAttempts: 2);
        await harness.InitializeAsync();
        if (mode == "logger_unavailable") harness.Http.DeniedBots.Add(123456);
        if (mode == "transport_unavailable") harness.Gate.Fence("logger-000", 123456);
        await harness.QueueAsync();
        for (var scan = 0; scan < 20; scan++)
        {
            using var worker = harness.Worker();
            Assert.True(await worker.ProcessOneAsync(default));
            var row = await harness.AlertAsync();
            Assert.Equal(TelegramEndpointAlertStatus.Pending, row.Status);
            Assert.Equal(mode, row.ErrorCategory);
            Assert.Equal(0, row.Attempts);
            Assert.True(row.NextAttemptAtUtc > harness.Now);
            harness.Advance(TimeSpan.FromMinutes(10));
        }
        Assert.DoesNotContain(harness.Http.Calls, call => call.Method == "sendMessage");
        Assert.Equal(20, harness.Log.Messages.Count);
        Assert.All(harness.Log.Messages, message => Assert.True(TelegramLogSuppression.ShouldSuppress(message, null!)));
        if (mode != "logger_unavailable") Assert.Empty(harness.Http.Calls);
    }

    /// <summary>Concurrent genuine consumers, repeated incidents, restart and delivered-detail retention never recreate an acknowledged incident.</summary>
    /// <returns>A task completing after one Cloud send and a permanent incident-only deduplication receipt remain authoritative.</returns>
    [Fact]
    public async Task Endpoint_logger_concurrent_consumers_ack_and_receipt_deduplicate_one_incident()
    {
        using var harness = new LoggerChannelHarness();
        await harness.InitializeAsync();
        await harness.QueueAsync();
        await harness.QueueAsync();
        using var first = harness.Worker();
        using var second = harness.Worker();
        Assert.Single(await Task.WhenAll(first.ProcessOneAsync(default), second.ProcessOneAsync(default)), processed => processed);
        Assert.Single(harness.Http.Calls, call => call.Method == "sendMessage");
        harness.Advance(TimeSpan.FromDays(100));
        await harness.Store.PruneDeliveredAlertsAsync(harness.Now, default);
        await harness.QueueAsync();
        using var restarted = harness.Worker();
        Assert.False(await restarted.ProcessOneAsync(default));
        Assert.Single(harness.Http.Calls, call => call.Method == "sendMessage");
        await using var db = harness.Databases.Users.CreateDbContext();
        Assert.Single(await db.TelegramEndpointAlertReceipts.ToListAsync());
    }

    /// <summary>Typed 429 proves no delivery and retries the same frozen negative channel even after live configuration changes; other failures never redirect or replay.</summary>
    /// <param name="mode">Typed 403/429, lost response or server-side ambiguity fixture.</param>
    /// <returns>A task completing after the durable retry or terminal outcome is consumed through the actual SDK.</returns>
    [Theory]
    [InlineData("403")]
    [InlineData("429")]
    [InlineData("lost")]
    [InlineData("500")]
    public async Task Endpoint_logger_send_rejections_and_ambiguity_preserve_frozen_destination(string mode)
    {
        using var harness = new LoggerChannelHarness();
        await harness.InitializeAsync();
        harness.Http.SendFailure = mode;
        await harness.QueueAsync();
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        var row = await harness.AlertAsync();
        Assert.Equal(mode == "429" ? TelegramEndpointAlertStatus.Pending : mode == "403" ? TelegramEndpointAlertStatus.ManualReview : TelegramEndpointAlertStatus.DeliveryUncertain, row.Status);
        Assert.Equal(-100711000, row.DestinationChatId);
        harness.Http.SendFailure = null;
        harness.Live["loggerChannel"] = "@changed_logger";
        harness.Advance(TimeSpan.FromMinutes(10));
        using var restarted = harness.Worker();
        Assert.Equal(mode == "429", await restarted.ProcessOneAsync(default));
        var sends = harness.Http.Calls.Where(call => call.Method == "sendMessage").ToArray();
        Assert.Equal(mode == "429" ? 2 : 1, sends.Length);
        Assert.All(sends, call => Assert.Equal(-100711000, call.NumericChat));
        Assert.DoesNotContain(harness.Http.Calls, call => call.Method == "getChat" && call.Chat == "@changed_logger");
    }

    /// <summary>Read failures and explicit rate-limit retries, unlike missing prerequisites, eventually exhaust the unchanged real-attempt cap.</summary>
    /// <param name="rateLimit">Whether genuine SDK sends reject with 429 instead of getMe failing before dispatch.</param>
    /// <returns>A task completing after two real failures retain the incident in ManualReview without further provider activity.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Endpoint_logger_real_failures_retain_existing_bounded_retry_cap(bool rateLimit)
    {
        using var harness = new LoggerChannelHarness(senderCount: 1, maxAttempts: 2);
        await harness.InitializeAsync();
        if (rateLimit) harness.Http.SendFailure = "429";
        else harness.Http.FailReads = true;
        await harness.QueueAsync();
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        harness.Advance(TimeSpan.FromMinutes(10));
        Assert.True(await worker.ProcessOneAsync(default));
        Assert.Equal(TelegramEndpointAlertStatus.ManualReview, (await harness.AlertAsync()).Status);
        Assert.Equal(2, (await harness.AlertAsync()).Attempts);
        harness.Advance(TimeSpan.FromDays(1));
        Assert.False(await worker.ProcessOneAsync(default));
    }

    /// <summary>An in-flight incident owns counted ordinary admission so fencing cannot finish the migration drain or log out its session before ACK.</summary>
    /// <returns>A task completing after a deterministic actual HTTP send barrier releases the original Cloud attempt and then permits drain.</returns>
    [Fact]
    public async Task Endpoint_logger_send_holds_normal_request_lease_through_ack_and_drain()
    {
        using var harness = new LoggerChannelHarness(senderCount: 1);
        await harness.InitializeAsync();
        await harness.QueueAsync();
        harness.Http.BlockSend = true;
        using var worker = harness.Worker();
        var processing = worker.ProcessOneAsync(default);
        await harness.Http.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Gate.Fence("logger-000", 123456);
        var drain = harness.Gate.DrainAsync("logger-000", 123456, TimeSpan.FromSeconds(5), default);
        Assert.False(drain.IsCompleted);
        harness.Http.SendRelease.TrySetResult();
        Assert.True(await processing);
        await drain;
        Assert.Equal(TelegramEndpointAlertStatus.Delivered, (await harness.AlertAsync()).Status);
        Assert.Single(harness.Http.Calls, call => call.Method == "sendMessage");
        Assert.DoesNotContain(harness.Http.Calls, call => call.Method == "logOut");
    }

    /// <summary>Shutdown after dispatch remains durable uncertainty and is never replayed by a fresh consumer.</summary>
    /// <returns>A task completing after actual SDK cancellation and one terminal retained send boundary are asserted.</returns>
    [Fact]
    public async Task Endpoint_logger_shutdown_after_dispatch_is_uncertain_without_replay()
    {
        using var harness = new LoggerChannelHarness(senderCount: 1);
        await harness.InitializeAsync();
        await harness.QueueAsync();
        harness.Http.BlockSend = true;
        using var cancellation = new CancellationTokenSource();
        using var worker = harness.Worker();
        var processing = worker.ProcessOneAsync(cancellation.Token);
        await harness.Http.SendStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        Assert.True(await processing);
        Assert.Equal(TelegramEndpointAlertStatus.DeliveryUncertain, (await harness.AlertAsync()).Status);
        harness.Advance(TimeSpan.FromDays(1));
        using var restarted = harness.Worker();
        Assert.False(await restarted.ProcessOneAsync(default));
        Assert.Single(harness.Http.Calls, call => call.Method == "sendMessage");
    }

    /// <summary>Read-only authorization is counted during migration drain, and a newly fenced route never crosses the send boundary after the read completes.</summary>
    /// <returns>A task completing after a real getChatMember barrier demonstrates safe Pending deferral and drain release.</returns>
    [Fact]
    public async Task Endpoint_logger_permission_read_is_counted_and_fencing_defers_before_send()
    {
        using var harness = new LoggerChannelHarness(senderCount: 1);
        await harness.InitializeAsync();
        await harness.QueueAsync();
        harness.Http.BlockMembership = true;
        using var worker = harness.Worker();
        var processing = worker.ProcessOneAsync(default);
        await harness.Http.MembershipStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        harness.Gate.Fence("logger-000", 123456);
        var drain = harness.Gate.DrainAsync("logger-000", 123456, TimeSpan.FromSeconds(5), default);
        Assert.False(drain.IsCompleted);
        harness.Http.MembershipRelease.TrySetResult();
        Assert.True(await processing);
        await drain;
        var row = await harness.AlertAsync();
        Assert.Equal(TelegramEndpointAlertStatus.Pending, row.Status);
        Assert.Equal(0, row.Attempts);
        Assert.Null(row.SendStartedAtUtc);
        Assert.DoesNotContain(harness.Http.Calls, call => call.Method == "sendMessage");
    }

    /// <summary>Shutdown during a bounded read safely releases the pre-send claim without freezing a destination or dispatching a message.</summary>
    /// <returns>A task completing after actual SDK cancellation leaves a restart-safe Pending incident.</returns>
    [Fact]
    public async Task Endpoint_logger_shutdown_during_read_is_pending_without_send_marker()
    {
        using var harness = new LoggerChannelHarness(senderCount: 1);
        await harness.InitializeAsync();
        await harness.QueueAsync();
        harness.Http.BlockMembership = true;
        using var cancellation = new CancellationTokenSource();
        using var worker = harness.Worker();
        var processing = worker.ProcessOneAsync(cancellation.Token);
        await harness.Http.MembershipStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processing);
        var row = await harness.AlertAsync();
        Assert.Equal(TelegramEndpointAlertStatus.Pending, row.Status);
        Assert.Null(row.SendStartedAtUtc);
        Assert.Null(row.DestinationChatId);
        Assert.DoesNotContain(harness.Http.Calls, call => call.Method == "sendMessage");
    }

    /// <summary>Raw provider errors and credentials never reach persisted categories, rendered incident text or recursive Telegram logger fallback.</summary>
    /// <returns>A task completing after fixed warnings are locally captured and only the exact retained-worker warning is channel-suppressed.</returns>
    [Fact]
    public async Task Endpoint_logger_failures_are_secret_free_and_suppression_is_narrow()
    {
        using var harness = new LoggerChannelHarness(senderCount: 1);
        await harness.InitializeAsync();
        await harness.QueueAsync();
        harness.Http.SendFailure = "lost";
        using var worker = harness.Worker();
        Assert.True(await worker.ProcessOneAsync(default));
        var row = await harness.AlertAsync();
        Assert.Equal("send_uncertain", row.ErrorCategory);
        Assert.DoesNotContain("private fixture error", row.ErrorCategory);
        Assert.All(harness.Log.Messages, message =>
        {
            Assert.DoesNotContain(EndpointRuntimeToken(123456), message);
            Assert.DoesNotContain("private fixture error", message);
            Assert.True(TelegramLogSuppression.ShouldSuppress(message, null!));
        });
        Assert.DoesNotContain(EndpointRuntimeToken(123456), Assert.Single(harness.Http.Messages));
        Assert.False(TelegramLogSuppression.ShouldSuppress("Telegram endpoint receiver cleanup deferred. Category=receiver_join_timeout", null!));
        Assert.False(TelegramLogSuppression.ShouldSuppress("Telegram endpoint operator alert retained. category=send_uncertain", new InvalidOperationException("real failure")));
        Assert.False(TelegramLogSuppression.ShouldSuppress("Customer delivery failed", null!));
    }

    /// <summary>A worker cannot count one gate while borrowing a different transport's uncounted Cloud authority.</summary>
    [Fact]
    public void Endpoint_logger_requires_provider_and_worker_to_share_the_same_gate()
    {
        using var harness = new LoggerChannelHarness();
        var otherGate = new TelegramEndpointRuntimeGate(harness.Registry, harness.Store);
        Assert.Throws<ArgumentException>(() => new TelegramEndpointNotificationWorker(harness.Store, harness.Options,
            harness.Registry, harness.Clients, otherGate, harness.Log));
    }

    /// <summary>Owns real SQLite, current runtime authority and the actual shared pooled SDK over fake HTTP.</summary>
    private sealed class LoggerChannelHarness : IDisposable
    {
        /// <summary>Existing temporary database fixture; production databases are never opened.</summary>
        public Databases Databases { get; } = new();
        /// <summary>Mutable root configuration used by the existing destination resolver.</summary>
        public IConfigurationRoot Live { get; }
        /// <summary>Exact enabled owned sender registry, independent of global Super Admin recipients.</summary>
        public BotRegistry Registry { get; }
        /// <summary>Transactional incident store with the genuine logger-channel resolver.</summary>
        public TelegramEndpointStore Store { get; }
        /// <summary>Current durable runtime gate shared by the worker and provider.</summary>
        public TelegramEndpointRuntimeGate Gate { get; }
        /// <summary>Actual pooled SDK construction over the isolated wire fixture.</summary>
        public BotClientProvider Clients { get; }
        /// <summary>Actual Bot API HTTP envelope fixture and deterministic request barriers.</summary>
        public LoggerChannelHttp Http { get; } = new();
        /// <summary>Local-only fixed warning capture; no Telegram logger or network is attached.</summary>
        public LoggerChannelLog Log { get; } = new();
        /// <summary>SQLite marker observation seam, not a mocked worker or store.</summary>
        public LoggerChannelBoundary Boundary { get; } = new();
        /// <summary>Unchanged bounded endpoint policy without any notifier identity setting.</summary>
        public TelegramEndpointRoutingOptions Options { get; }
        /// <summary>Controllable UTC time for durable retries and leases.</summary>
        private readonly LoggerChannelClock _clock = new();
        /// <summary>Stable incident id shared by repeated queue operations.</summary>
        private readonly string _incident = Guid.NewGuid().ToString("N");
        /// <summary>Current fixture UTC instant.</summary>
        public DateTime Now => _clock.GetUtcNow().UtcDateTime;

        /// <summary>Constructs no-network real collaborators with optional existing logger targets.</summary>
        /// <param name="rootLogger">Existing root logger address; null leaves it absent.</param>
        /// <param name="defaultLogger">Existing default-owned logger fallback; null leaves it absent.</param>
        /// <param name="senderCount">Positive count of deterministic owned sender candidates.</param>
        /// <param name="maxAttempts">Positive genuine-failure cap; missing prerequisites must not consume it.</param>
        public LoggerChannelHarness(string? rootLogger = "@existing_logger", string? defaultLogger = null,
            int senderCount = 2, int maxAttempts = 12)
        {
            var entries = new Dictionary<string, string?> { ["loggerChannel"] = rootLogger };
            for (var index = 0; index < senderCount; index++)
            {
                var prefix = $"bots:{index}:";
                entries[prefix + "id"] = $"logger-{index:000}";
                entries[prefix + "token"] = EndpointRuntimeToken(index == 0 ? 123456 : 654320 + index);
                entries[prefix + "type"] = BotInstanceTypes.Owned;
                entries[prefix + "enabled"] = "true";
                entries[prefix + "isDefault"] = index == 0 ? "true" : "false";
                if (index == 0) entries[prefix + "loggerChannel"] = defaultLogger;
            }
            Live = new ConfigurationBuilder().AddInMemoryCollection(entries).Build();
            Registry = new BotRegistry(Live);
            Options = new TelegramEndpointRoutingOptions { NotificationMaxAttempts = maxAttempts };
            using var db = Databases.Users.CreateDbContext();
            var factory = new UserDbContextFactory(new DbContextOptionsBuilder<UserDbContext>()
                .UseSqlite(db.Database.GetConnectionString()).AddInterceptors(Boundary).Options);
            Store = new TelegramEndpointStore(factory, new AppConfig { AdminsUserIds = [] }, Options, Live);
            Gate = new TelegramEndpointRuntimeGate(Registry, Store);
            Clients = new BotClientProvider(Registry, Gate, Options, Http);
        }

        /// <summary>Hydrates every configured sender from actual durable Cloud-default authority before ordinary admission.</summary>
        /// <returns>A task completing after safe current-identity publication without network requests.</returns>
        public async Task InitializeAsync()
        {
            foreach (var bot in Registry.Bots)
                await Gate.HydrateAsync(bot.Id, TelegramBotTokenIdentity.ExtractBotId(bot.Token)!.Value, default);
        }

        /// <summary>Queues or deduplicates the same frozen synthetic migration incident transactionally.</summary>
        /// <returns>A task completing after the SQLite CAS and incident-only outbox receipt commit.</returns>
        public async Task QueueAsync()
        {
            var state = await Store.GetOrCreateAsync("affected-owned", 777888, default);
            state.OperationId = _incident;
            state.MigrationState = TelegramEndpointMigrationState.CheckingLocal;
            Assert.True(await Store.TrySaveAsync(state, state.Revision, "migration_requested", "migration_started"));
        }

        /// <summary>Creates a fresh real consumer sharing the current gate, pooled transport and durable outbox.</summary>
        /// <returns>A disposable worker with no hosted timer or external network.</returns>
        public TelegramEndpointNotificationWorker Worker() => new(Store, Options, Registry, Clients, Gate, Log, _clock);

        /// <summary>Reads the detached sole incident intent through an independent actual SQLite context.</summary>
        /// <returns>The fixture's secret-free outbox row.</returns>
        public async Task<TelegramEndpointAlert> AlertAsync()
        {
            await using var db = Databases.Users.CreateDbContext();
            return await db.TelegramEndpointAlerts.AsNoTracking().SingleAsync();
        }

        /// <summary>Changes exact current registry authority without bypassing the gate's identity-incarnation notification.</summary>
        /// <param name="id">Exact internal owned sender id.</param>
        /// <param name="identity">Positive synthetic BotFather id.</param>
        /// <param name="enabled">Whether new worker selection is allowed.</param>
        /// <param name="type">Owned, tenant or assistant runtime family.</param>
        /// <param name="tokenSuffix">Synthetic secret rotation character; never a production token.</param>
        public void Replace(string id, long identity, bool enabled = true, string type = BotInstanceTypes.Owned, char tokenSuffix = 'x') =>
            Registry.Upsert(new BotInstance { Id = id, Token = identity + ":" + new string(tokenSuffix, 35), Enabled = enabled, Type = type });

        /// <summary>Advances persisted deadline time without waiting or changing request cancellation behavior.</summary>
        /// <param name="duration">Positive synthetic duration.</param>
        public void Advance(TimeSpan duration) => _clock.Advance(duration);

        /// <summary>Disposes shared transport before removing only this fixture's temporary databases.</summary>
        public void Dispose() { Clients.Dispose(); Http.Dispose(); Databases.Dispose(); }
    }

    /// <summary>Controllable UTC clock for genuine store claim and retry transitions.</summary>
    private sealed class LoggerChannelClock : TimeProvider
    {
        /// <summary>Initial UTC instant lies just after actual intent creation.</summary>
        private DateTimeOffset _now = DateTimeOffset.UtcNow.AddSeconds(1);
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => _now;
        /// <summary>Moves only this fixture's durable deadline clock.</summary>
        /// <param name="duration">Positive synthetic elapsed duration.</param>
        public void Advance(TimeSpan duration) => _now += duration;
    }

    /// <summary>Observes the real marker update and injects deterministic pre-dispatch prerequisite loss.</summary>
    private sealed class LoggerChannelBoundary : DbCommandInterceptor
    {
        /// <summary>One-shot authority mutation after the genuine marker statement has completed.</summary>
        public Action? AfterMarker { get; set; }
        /// <summary>Actual marker update count, excluding claim and finalization statements.</summary>
        public int Markers { get; private set; }
        /// <inheritdoc />
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (result == 1 && command.CommandText.StartsWith("UPDATE", StringComparison.Ordinal) &&
                command.CommandText.Contains("DestinationChatId", StringComparison.Ordinal) &&
                command.CommandText.Contains("SendStartedAtUtc", StringComparison.Ordinal))
            {
                Markers++;
                var callback = AfterMarker;
                AfterMarker = null;
                callback?.Invoke();
            }
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Captures safe rendered local warnings without exceptions, credentials or a Telegram provider.</summary>
    private sealed class LoggerChannelLog : ILogger<TelegramEndpointNotificationWorker>
    {
        /// <summary>Actual formatted fixed-category warning messages.</summary>
        public ConcurrentQueue<string> Messages { get; } = new();
        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;
        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Messages.Enqueue(formatter(state, exception));
        }
    }

    /// <summary>Real SDK wire fixture; no interface mock, live URL, production secret or token-bearing request path is retained.</summary>
    private sealed class LoggerChannelHttp : HttpMessageHandler
    {
        /// <summary>Safe request metadata proving exact Cloud identity, API method and destination.</summary>
        public ConcurrentQueue<(long BotIdentity, string Host, string Method, string Chat, long? NumericChat)> Calls { get; } = new();
        /// <summary>Rendered nonsecret incident messages inspected only by the dedicated secret-safety regression.</summary>
        public ConcurrentQueue<string> Messages { get; } = new();
        /// <summary>Bot ids whose membership explicitly denies channel posting.</summary>
        public HashSet<long> DeniedBots { get; } = new();
        /// <summary>Bot ids whose actual getChat reply is a definitive 403.</summary>
        public HashSet<long> ReadDeniedBots { get; } = new();
        /// <summary>Channel or member response scenario; normal returns a posting administrator.</summary>
        public string? Mode { get; set; }
        /// <summary>Typed rejection or ambiguity applied to actual sendMessage calls.</summary>
        public string? SendFailure { get; set; }
        /// <summary>Whether every actual getMe throws a safe pre-send transport failure.</summary>
        public bool FailReads { get; set; }
        /// <summary>Authority mutation invoked after actual read-only SDK HTTP handling.</summary>
        public Action<string>? AfterRead { get; set; }
        /// <summary>Whether the one actual send awaits a release or shutdown cancellation barrier.</summary>
        public bool BlockSend { get; set; }
        /// <summary>Signals actual HTTP dispatch, not the durable SQLite marker.</summary>
        public TaskCompletionSource SendStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Releases a blocked actual HTTP send with a deterministic acknowledgment.</summary>
        public TaskCompletionSource SendRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Whether the real read-only membership request waits on an explicit release or cancellation barrier.</summary>
        public bool BlockMembership { get; set; }
        /// <summary>Signals actual read-only SDK dispatch while its normal request lease is held.</summary>
        public TaskCompletionSource MembershipStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Releases the actual membership request without replacing the SDK response or worker path.</summary>
        public TaskCompletionSource MembershipRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Consumes actual SDK JSON and returns real Bot API envelopes over the existing Cloud pooling path.</summary>
        /// <param name="request">Actual SDK request inspected transiently; its token-bearing URI is never captured.</param>
        /// <param name="cancellationToken">Actual request/host cancellation propagated by the pooled SDK.</param>
        /// <returns>A real serialized Bot API reply or controlled post-dispatch transport failure.</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            var segments = uri.AbsolutePath.Split('/');
            var identity = long.Parse(segments[1].AsSpan(3, segments[1].IndexOf(':') - 3));
            var method = segments[^1];
            var body = request.Content == null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
            var fields = json.RootElement;
            var chat = fields.TryGetProperty("chat_id", out var chatValue) ? chatValue.ToString() : "";
            var numericChat = long.TryParse(chat, out var parsedChat) ? parsedChat : (long?)null;
            Calls.Enqueue((identity, uri.Host, method, chat, numericChat));
            if (method == "getMe" && FailReads) throw new HttpRequestException("private fixture error " + EndpointRuntimeToken(identity));
            if (method == "getChat" && ReadDeniedBots.Contains(identity)) return Failure(403);
            var channelId = numericChat ?? (chat == "@changed_logger" ? -100722000 : -100711000);
            object result;
            switch (method)
            {
                case "getMe":
                    result = BotUser(Mode == "wrong_identity" ? identity + 1 : identity);
                    break;
                case "getChat":
                    result = new { id = Mode == "positive_channel" ? 711 : channelId,
                        type = Mode == "private" ? "private" : Mode == "supergroup" ? "supergroup" : "channel",
                        title = "synthetic logger", username = "existing_logger", accent_color_id = 0 };
                    break;
                case "getChatMember":
                    MembershipStarted.TrySetResult();
                    if (BlockMembership) await MembershipRelease.Task.WaitAsync(cancellationToken);
                    var status = Mode == "owner" ? "creator" : Mode == "member" ? "member" : Mode == "restricted" ? "restricted" : "administrator";
                    result = new { status, user = BotUser(Mode == "wrong_member" ? identity + 1 : identity),
                        can_post_messages = Mode != "denied_admin" && !DeniedBots.Contains(identity),
                        can_manage_chat = true, can_delete_messages = true, can_manage_video_chats = true,
                        can_restrict_members = true, can_promote_members = true, can_change_info = true,
                        can_invite_users = true, is_anonymous = false, is_member = true, until_date = 0 };
                    break;
                case "sendMessage":
                    Assert.True(numericChat is < 0);
                    Messages.Enqueue(fields.GetProperty("text").GetString()!);
                    SendStarted.TrySetResult();
                    if (BlockSend) await SendRelease.Task.WaitAsync(cancellationToken);
                    if (SendFailure == "lost") throw new HttpRequestException("private fixture error " + EndpointRuntimeToken(identity));
                    if (SendFailure is "403" or "429" or "500") return Failure(int.Parse(SendFailure));
                    result = new { message_id = 1, date = 1700000000, chat = new { id = channelId, type = "channel", title = "synthetic logger" }, text = "ack" };
                    break;
                default:
                    throw new InvalidOperationException("Unexpected SDK method.");
            }
            if (method != "sendMessage") AfterRead?.Invoke(method);
            return new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, result }), Encoding.UTF8, "application/json") };
        }

        /// <summary>Returns a genuine typed Telegram rejection envelope rather than simulating an SDK exception.</summary>
        /// <param name="status">Explicit synthetic HTTP and Telegram API error code.</param>
        /// <returns>A response parsed by the actual SDK; only 429 carries a usable RetryAfter proof.</returns>
        private static HttpResponseMessage Failure(int status) => new((HttpStatusCode)status)
        { Content = new StringContent(JsonSerializer.Serialize(new { ok = false, error_code = status,
            description = "private fixture error", parameters = new { retry_after = 5 } }), Encoding.UTF8, "application/json") };

        /// <summary>Constructs a valid-shaped nonproduction bot identity response.</summary>
        /// <param name="identity">Synthetic positive BotFather id, never a Telegram customer id.</param>
        /// <returns>SDK-compatible user metadata declaring the exact bot identity.</returns>
        private static object BotUser(long identity) => new { id = identity, is_bot = true, first_name = "fixture", username = "synthetic_bot" };
    }
}
