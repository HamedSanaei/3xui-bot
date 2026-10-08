using System.Collections.Concurrent;
using System.Text;
using Adminbot.Domain;
using Adminbot.Services.TelegramEndpoints;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

/// <summary>Behavioral endpoint-panel authorization, message binding, confirmation, expiry and pagination regressions without Telegram network access.</summary>
/// <remarks>Drives the real administration service against deterministic metadata and SDK-request capture, not source-text assertions.</remarks>
public sealed class TelegramEndpointAdminTests
{
    private const long Admin = 101;

    /// <summary>Rejects non-admin, tenant/assistant, unknown-host and nonprivate entry attempts before metadata access.</summary>
    /// <param name="host">Synthetic receiving internal bot id.</param>
    /// <param name="actor">Synthetic Telegram sender id.</param>
    /// <param name="chat">Incoming chat type.</param>
    /// <returns>A task completing after authorization refusal is observed.</returns>
    [Theory]
    [InlineData("owned", 202, ChatType.Private)]
    [InlineData("tenant", Admin, ChatType.Private)]
    [InlineData("assistant", Admin, ChatType.Private)]
    [InlineData("owned-assistant", Admin, ChatType.Private)]
    [InlineData("missing", Admin, ChatType.Private)]
    [InlineData("owned", Admin, ChatType.Supergroup)]
    public async Task Entry_requires_global_admin_exact_owned_private_host(string host, long actor, ChatType chat)
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Panel.TryHandleAsync(host, fixture.Client, Entry(actor, TelegramEndpointAdminService.Action, chat), default));
        Assert.Empty(fixture.Client.Edits);
        Assert.Empty(fixture.Client.Sends);
        Assert.Equal(0, fixture.Backend.Reads);
    }

    /// <summary>Allows the same global operator to administer through another healthy owned bot, but not a command addressed elsewhere.</summary>
    /// <returns>A task completing after the alternate entry is rendered.</returns>
    [Fact]
    public async Task Alternate_owned_command_path_opens_inventory()
    {
        var fixture = new Fixture();
        Assert.True(await fixture.Panel.TryHandleAsync("alternate", fixture.Client, Entry(Admin, "/telegram_api@alternate_bot"), default));
        Assert.Contains("فهرست ربات‌ها", fixture.Client.Edits.Last().Text);
        Assert.False(await fixture.Panel.TryHandleAsync("alternate", fixture.Client, Entry(Admin, "/telegram_api@another_bot"), default));
        Assert.All(fixture.Client.Edits.SelectMany(x => ((InlineKeyboardMarkup)x.ReplyMarkup!).InlineKeyboard).SelectMany(x => x),
            button => Assert.InRange(Encoding.UTF8.GetByteCount(button.CallbackData!), 1, 64));
    }

    /// <summary>Requires an explicit confirmation before migration and publishes progress before the coordinator can fence this host.</summary>
    /// <returns>A task completing after one accepted migration intent and replay rejection.</returns>
    [Fact]
    public async Task Migration_requires_confirmation_and_progress_precedes_queue()
    {
        var fixture = new Fixture();
        await fixture.OpenDetailAsync();
        var select = fixture.Control;
        await fixture.TapAsync(select, Button(select, "انتقال به Local…"));
        Assert.Empty(fixture.Backend.Migrations);
        var confirmation = fixture.Control;
        Assert.Contains("تأیید می‌کنید", confirmation.Text);
        var data = Button(confirmation, "✅ تأیید انتقال به Local (محلی)");
        fixture.Backend.BeforeMigration = () =>
        {
            Assert.Contains("در حال ثبت درخواست انتقال", fixture.Control.Text);
            Assert.Contains(Buttons(fixture.Control), x => x.Text == "🔄 تازه‌سازی وضعیت و سلامت");
        };
        var editsBefore = fixture.Client.Edits.Count;
        await fixture.TapAsync(confirmation, data);
        Assert.Equal(editsBefore + 1, fixture.Client.Edits.Count);
        Assert.Equal(("owned", TelegramEndpointType.Local, Admin, 0L, 1000L), Assert.Single(fixture.Backend.Migrations));
        await fixture.TapAsync(confirmation, data);
        Assert.Single(fixture.Backend.Migrations);
        Assert.Contains("مصرف‌شده", fixture.Client.Answers.Last().Text);
    }

    /// <summary>Rejects actor, hosting bot, chat and message forgery without burning the legitimate actor's session.</summary>
    /// <returns>A task completing after forged updates and a valid confirmation.</returns>
    [Fact]
    public async Task Callback_is_bound_to_actor_host_chat_and_message()
    {
        var fixture = new Fixture();
        await fixture.OpenDetailAsync();
        var control = fixture.Control;
        var data = Button(control, "انتقال به Local…");
        foreach (var forged in new[]
        {
            Callback(control, data, actor: 202),
            Callback(control, data, messageId: control.MessageId + 1),
            Callback(control, data, chatId: Admin + 1),
            Callback(control, data, chatType: ChatType.Group)
        }) Assert.True(await fixture.Panel.TryHandleAsync("owned", fixture.Client, forged, default));
        Assert.True(await fixture.Panel.TryHandleAsync("alternate", fixture.Client, Callback(control, data), default));
        Assert.Empty(fixture.Backend.Migrations);
        Assert.Equal(control, fixture.Control);
        await fixture.TapAsync(control, data);
        Assert.Contains("تأیید می‌کنید", fixture.Control.Text);
    }

    /// <summary>Rechecks global authorization on callback even after a valid control message has been issued.</summary>
    /// <returns>A task completing after current-authorization rejection.</returns>
    [Fact]
    public async Task Revoked_admin_cannot_use_old_controls()
    {
        var fixture = new Fixture();
        await fixture.OpenDetailAsync();
        var control = fixture.Control;
        fixture.Configuration.AdminsUserIds.Clear();
        await fixture.TapAsync(control, Button(control, "انتقال به Local…"));
        Assert.Equal(control, fixture.Control);
        Assert.Empty(fixture.Backend.Migrations);
        Assert.Contains("مدیر کل", fixture.Client.Answers.Last().Text);
    }

    /// <summary>Live configuration revocation overrides a stale startup AppConfig allow-list without restarting the panel.</summary>
    /// <returns>A task completing after the removed operator's callback is rejected before mutation.</returns>
    [Fact]
    public async Task Live_global_allowlist_reload_rejects_stale_startup_admin()
    {
        var live = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AdminsUserIds:0"] = Admin.ToString() }).Build();
        var fixture = new Fixture(liveConfiguration: live);
        await fixture.OpenDetailAsync();
        var control = fixture.Control;
        live["AdminsUserIds:0"] = null;
        await fixture.TapAsync(control, Button(control, "انتقال به Local…"));
        Assert.Empty(fixture.Backend.Migrations);
        Assert.Contains("مدیر کل", fixture.Client.Answers.Last().Text);
    }

    /// <summary>Rejects ten-minute expiry, malformed indexes, unknown nonce and oversize callback payloads deterministically.</summary>
    /// <param name="corruption">Which security boundary to exercise.</param>
    /// <returns>A task completing after rejection without a migration request.</returns>
    [Theory]
    [InlineData("expiry")]
    [InlineData("negative")]
    [InlineData("unknown")]
    [InlineData("oversize")]
    [InlineData("extra")]
    public async Task Expired_or_malformed_callbacks_never_execute(string corruption)
    {
        var fixture = new Fixture();
        await fixture.OpenDetailAsync();
        var control = fixture.Control;
        var data = Button(control, "انتقال به Local…");
        data = corruption switch
        {
            "negative" => data[..(data.LastIndexOf(':') + 1)] + "-1",
            "unknown" => "tep:000000000000000000000000:0",
            "oversize" => "tep:" + new string('a', 65) + ":0",
            "extra" => data + ":extra",
            _ => data
        };
        if (corruption == "expiry") fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        await fixture.TapAsync(control, data);
        Assert.Empty(fixture.Backend.Migrations);
        Assert.Equal(control, fixture.Control);
        Assert.Contains("نامعتبر", fixture.Client.Answers.Last().Text);
    }

    /// <summary>Refuses stale operator revision and token-identity replacement at the confirmation boundary.</summary>
    /// <param name="identityChanged">True replaces the configured bot identity; false changes only operator control revision.</param>
    /// <returns>A task completing after stale rejection and fresh identity-appropriate metadata rendering.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stale_confirmation_cannot_migrate(bool identityChanged)
    {
        var fixture = new Fixture();
        await fixture.OpenDetailAsync();
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "انتقال به Local…"));
        var control = fixture.Control;
        if (identityChanged) fixture.Backend.States["owned"].TelegramBotId++;
        else fixture.Backend.States["owned"].ControlRevision++;
        await fixture.TapAsync(control, Button(control, "✅ تأیید انتقال به Local (محلی)"));
        Assert.Empty(fixture.Backend.Migrations);
        Assert.Contains("هویت ربات تغییر", fixture.Client.Answers.Last().Text);
        Assert.Contains(identityChanged ? "فهرست ربات‌ها" : "ربات: owned_bot", fixture.Control.Text);
    }

    /// <summary>Consumes confirmation synchronously before yielding, so concurrent taps cannot queue two migration commands.</summary>
    /// <returns>A task completing after exactly one migration request.</returns>
    [Fact]
    public async Task Concurrent_confirmation_taps_queue_once()
    {
        var fixture = new Fixture();
        await fixture.OpenDetailAsync();
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "انتقال به Local…"));
        var control = fixture.Control;
        var data = Button(control, "✅ تأیید انتقال به Local (محلی)");
        fixture.Backend.MigrationBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = fixture.TapAsync(control, data);
        var second = fixture.TapAsync(control, data);
        await second;
        Assert.Single(fixture.Backend.Migrations);
        fixture.Backend.MigrationBarrier.SetResult();
        await first;
        Assert.Single(fixture.Backend.Migrations);
    }

    /// <summary>Enumerates all owned, tenant, assistant and disabled current identities through bounded six-row pages.</summary>
    /// <returns>A task completing after all configured bots have been visited.</returns>
    [Fact]
    public async Task Inventory_pagination_includes_every_current_identity()
    {
        var fixture = new Fixture(extraBots: 13);
        await fixture.OpenAsync();
        var observed = new HashSet<string>(StringComparer.Ordinal);
        while (true)
        {
            var control = fixture.Control;
            var buttons = Buttons(control);
            var details = buttons.Where(x => x.Text.StartsWith("جزئیات «", StringComparison.Ordinal)).ToArray();
            Assert.InRange(details.Length, 1, 6);
            foreach (var button in details) Assert.True(observed.Add(button.Text));
            var next = buttons.SingleOrDefault(x => x.Text == "صفحه بعد ▶️");
            if (next == null) break;
            await fixture.TapAsync(control, next.CallbackData!);
        }
        Assert.Equal(fixture.Backend.States.Count, observed.Count);
        Assert.Contains("جزئیات «tenant_bot»", observed);
        Assert.Contains("جزئیات «assistant_bot»", observed);
        Assert.Contains("جزئیات «disabled_bot»", observed);
    }

    /// <summary>Exposes cooldown, unsafe logout, timestamps, independent notification prerequisites and safe numeric audit actors.</summary>
    /// <returns>A task completing after detail rendering and secret-free history assertions.</returns>
    [Fact]
    public async Task Detail_exposes_safety_prerequisites_and_sanitizes_history()
    {
        var fixture = new Fixture();
        var state = fixture.Backend.States["owned"];
        state.MigrationState = TelegramEndpointMigrationState.CloudLogoutUncertain;
        state.LogoutAttemptedAtUtc = fixture.Clock.GetUtcNow().UtcDateTime;
        state.CloudReuseEligibleAtUtc = fixture.Clock.GetUtcNow().UtcDateTime.AddMinutes(10);
        state.LastSuccessfulHealthAtUtc = fixture.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1);
        fixture.Backend.History.Add(new TelegramEndpointHistory
        {
            CreatedAtUtc = fixture.Clock.GetUtcNow().UtcDateTime,
            ActorTelegramUserId = 123,
            Reason = "secret-token-that-must-never-be-rendered",
            Outcome = "raw-provider-error-must-never-be-rendered",
            MigrationState = TelegramEndpointMigrationState.CloudLogoutUncertain
        });
        await fixture.OpenDetailAsync();
        var text = fixture.Control.Text;
        Assert.Contains("600 ثانیه", text);
        Assert.Contains("UTC", text);
        Assert.Contains("خروج نامطمئن", text);
        Assert.Contains("نگاشت مطمئن", text);
        Assert.Contains("اعلان مستقل تضمین‌شده نیست", text);
        Assert.Contains("عامل: 123", text);
        Assert.DoesNotContain("secret-token", text);
        Assert.DoesNotContain("raw-provider-error", text);
        Assert.DoesNotContain("synthetic-not-a-token", text);
    }

    /// <summary>Renders missing current identity as unknown without assuming Cloud or offering unsafe mutations.</summary>
    /// <returns>A task completing after inventory and detail unknown-route assertions.</returns>
    [Fact]
    public async Task Missing_identity_never_assumes_cloud_or_offers_migration()
    {
        var fixture = new Fixture();
        var state = fixture.Backend.States["tenant"];
        state.TelegramBotId = 0;
        state.MigrationState = TelegramEndpointMigrationState.ManualInterventionRequired;
        state.LastFailureCategory = "configuration_missing";
        await fixture.OpenAsync();
        Assert.Contains("tenant_bot | انتخاب: نامشخص؛ هویت موجود نیست | مؤثر: نامشخص؛ مسیر منتشر نشده", fixture.Control.Text);
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "جزئیات «tenant_bot»"));
        Assert.Contains("مسیر مؤثر: نامشخص؛ مسیر منتشر نشده", fixture.Control.Text);
        Assert.Contains("نسل مسیر: نامشخص", fixture.Control.Text);
        Assert.Contains("هیچ هویتی از سوابق قدیمی حدس زده نمی‌شود", fixture.Control.Text);
        Assert.DoesNotContain(Buttons(fixture.Control), x => x.Text.StartsWith("انتقال به", StringComparison.Ordinal));
        Assert.DoesNotContain(Buttons(fixture.Control), x => x.Text.Contains("بازگشت اضطراری", StringComparison.Ordinal));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🔄 تازه‌سازی وضعیت"));
        Assert.Contains("مسیر مؤثر: نامشخص؛ مسیر منتشر نشده", fixture.Control.Text);
        Assert.Empty(fixture.Backend.Migrations);
    }

    /// <summary>Refreshes the final selected bot state after a queued migration changes control revision, without replaying the intent.</summary>
    /// <returns>A task completing after final Local status is visible through the original progress control.</returns>
    [Fact]
    public async Task Progress_refresh_reads_final_result_without_replaying_migration()
    {
        var fixture = new Fixture();
        await fixture.OpenDetailAsync();
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "انتقال به Local…"));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "✅ تأیید انتقال به Local (محلی)"));
        var progress = fixture.Control;
        var state = fixture.Backend.States["owned"];
        state.DesiredEndpoint = TelegramEndpointType.Local;
        state.EffectiveEndpoint = TelegramEndpointType.Local;
        state.MigrationState = TelegramEndpointMigrationState.Local;
        await fixture.TapAsync(progress, Button(progress, "🔄 تازه‌سازی وضعیت و سلامت"));
        Assert.Single(fixture.Backend.Migrations);
        Assert.Contains("مسیر مؤثر: Local (محلی)", fixture.Control.Text);
        Assert.Contains("وضعیت: Local فعال", fixture.Control.Text);
    }

    /// <summary>Bounds total process-local sessions while keeping newest controls usable.</summary>
    /// <returns>A task completing after oldest-session eviction and newest-session navigation.</returns>
    [Fact]
    public async Task Sessions_are_bounded_and_oldest_controls_are_evicted()
    {
        var fixture = new Fixture();
        await fixture.OpenAsync();
        var oldest = fixture.Control;
        for (var i = 0; i < 512; i++)
        {
            fixture.Clock.Advance(TimeSpan.FromMilliseconds(1));
            await fixture.OpenAsync();
        }
        var newest = fixture.Control;
        await fixture.TapAsync(oldest, Button(oldest, "جزئیات «owned_bot»"));
        Assert.Equal(newest, fixture.Control);
        Assert.Contains("منقضی", fixture.Client.Answers.Last().Text);
        await fixture.TapAsync(newest, Button(newest, "جزئیات «owned_bot»"));
        Assert.Contains("ربات: owned_bot", fixture.Control.Text);
    }

    /// <summary>Cancellation abandons confirmation and explicit fallback targets cannot be applied twice by replay.</summary>
    /// <returns>A task completing after cancellation and one single-use fallback update.</returns>
    [Fact]
    public async Task Confirmation_cancel_and_failover_target_are_single_use()
    {
        var fixture = new Fixture();
        await fixture.OpenDetailAsync();
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "انتقال به Local…"));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "❌ انصراف"));
        Assert.Empty(fixture.Backend.Migrations);
        var control = fixture.Control;
        var data = Button(control, "خاموش‌کردن بازگشت اضطراری");
        await fixture.TapAsync(control, data);
        Assert.False(fixture.Backend.States["owned"].AutoFailoverEnabled);
        var revision = fixture.Backend.States["owned"].ControlRevision;
        await fixture.TapAsync(control, data);
        Assert.Equal(revision, fixture.Backend.States["owned"].ControlRevision);
        Assert.False(fixture.Backend.States["owned"].AutoFailoverEnabled);
    }

    /// <summary>Passes the originally confirmed identity to the atomic backend boundary even if the registry changes after UI validation.</summary>
    /// <returns>A task completing after race-time replacement is rejected rather than migrating the replacement identity.</returns>
    [Fact]
    public async Task Identity_replacement_at_queue_boundary_cannot_retarget_confirmation()
    {
        var fixture = new Fixture();
        await fixture.OpenDetailAsync();
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "انتقال به Local…"));
        var confirmation = fixture.Control;
        fixture.Backend.BeforeMigration = () => fixture.Backend.States["owned"].TelegramBotId++;
        await fixture.TapAsync(confirmation, Button(confirmation, "✅ تأیید انتقال به Local (محلی)"));
        Assert.Empty(fixture.Backend.Migrations);
        Assert.Contains("پنل قدیمی است", fixture.Control.Text);
    }

    /// <summary>Finds a rendered button by its exact Persian label.</summary>
    /// <param name="control">Captured actual SDK edit request.</param>
    /// <param name="label">Exact expected rendered label.</param>
    /// <returns>The opaque callback data emitted by production UI.</returns>
    private static string Button(EditMessageTextRequest control, string label) => Buttons(control).Single(x => x.Text == label).CallbackData!;

    /// <summary>Flattens the real rendered inline keyboard.</summary>
    /// <param name="control">Captured production control edit.</param>
    /// <returns>Rendered buttons without source-text inspection.</returns>
    private static InlineKeyboardButton[] Buttons(EditMessageTextRequest control)
        => ((InlineKeyboardMarkup)control.ReplyMarkup!).InlineKeyboard.SelectMany(x => x).ToArray();

    /// <summary>Builds an authenticated synthetic private or group entry update.</summary>
    /// <param name="actor">Synthetic numeric Telegram actor id.</param>
    /// <param name="text">Panel entry or command.</param>
    /// <param name="chatType">Incoming chat type under authorization test.</param>
    /// <returns>A text-message update.</returns>
    private static Update Entry(long actor, string text, ChatType chatType = ChatType.Private) => new()
    {
        Message = new Message { Id = 1, Text = text, Chat = new Chat { Id = actor, Type = chatType }, From = new Telegram.Bot.Types.User { Id = actor, FirstName = "test" } }
    };

    /// <summary>Builds a callback from a captured actual control message, with optional forged security bindings.</summary>
    /// <param name="control">Rendered control supplying the message id.</param>
    /// <param name="data">Opaque emitted payload or malformed attack payload.</param>
    /// <param name="actor">Callback sender id.</param>
    /// <param name="messageId">Optional forged message id.</param>
    /// <param name="chatId">Optional forged private chat id.</param>
    /// <param name="chatType">Optional forged group type.</param>
    /// <returns>A callback update handled by the real service.</returns>
    private static Update Callback(EditMessageTextRequest control, string data, long actor = Admin,
        int? messageId = null, long chatId = Admin, ChatType chatType = ChatType.Private) => new()
    {
        CallbackQuery = new CallbackQuery
        {
            Id = Guid.NewGuid().ToString("N"), Data = data, From = new Telegram.Bot.Types.User { Id = actor, FirstName = "test" },
            Message = new Message { Id = messageId ?? control.MessageId, Chat = new Chat { Id = chatId, Type = chatType } }
        }
    };

    /// <summary>Owns isolated configured identities, deterministic clock and request capture for one behavioral test.</summary>
    private sealed class Fixture
    {
        /// <summary>Mutable allow-list for revocation tests.</summary>
        public AppConfig Configuration { get; } = new() { AdminsUserIds = new List<long> { Admin } };
        /// <summary>Detached metadata backend; no hosted migration worker is started.</summary>
        public Backend Backend { get; } = new();
        /// <summary>SDK requests captured without a network transport.</summary>
        public RecordingClient Client { get; } = new();
        /// <summary>Deterministic injected UTC clock.</summary>
        public Clock Clock { get; } = new();
        /// <summary>Production panel under test.</summary>
        public TelegramEndpointAdminService Panel { get; }
        /// <summary>Most recently rendered control message.</summary>
        public EditMessageTextRequest Control => Client.Edits.Last();

        /// <summary>Creates owned, tenant, assistant and disabled registry entries with synthetic credentials only.</summary>
        /// <param name="extraBots">Number of additional identities used for multi-page inventory tests.</param>
        /// <param name="liveConfiguration">Optional live production-style allow-list used to verify reload revocation independently of startup options.</param>
        public Fixture(int extraBots = 0, IConfiguration? liveConfiguration = null)
        {
            var values = new Dictionary<string, string?>();
            var names = new[] { "owned", "alternate", "tenant", "assistant", "owned-assistant", "disabled" }
                .Concat(Enumerable.Range(0, extraBots).Select(x => "extra" + x)).ToArray();
            for (var i = 0; i < names.Length; i++)
            {
                var id = names[i];
                values[$"Bots:{i}:Id"] = id;
                values[$"Bots:{i}:Username"] = id.Replace('-', '_') + "_bot";
                values[$"Bots:{i}:Token"] = "synthetic-not-a-token";
                values[$"Bots:{i}:Type"] = id == "tenant" ? BotInstanceTypes.Tenant : id == "assistant" ? BotInstanceTypes.SalesAssistant : BotInstanceTypes.Owned;
                values[$"Bots:{i}:Enabled"] = (id != "disabled").ToString();
                values[$"Bots:{i}:IsSalesAssistant"] = (id is "assistant" or "owned-assistant").ToString();
                Backend.States.Add(id, new TelegramEndpointState { BotId = id, TelegramBotId = 1000 + i });
            }
            var registry = new BotRegistry(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
            Panel = new TelegramEndpointAdminService(Backend, Configuration, registry, new TelegramEndpointRoutingOptions(),
                NullLogger<TelegramEndpointAdminService>.Instance, timeProvider: Clock, liveConfiguration: liveConfiguration);
        }

        /// <summary>Opens the production entry on the primary owned host.</summary>
        /// <returns>A task completing after inventory rendering.</returns>
        public Task<bool> OpenAsync() => Panel.TryHandleAsync("owned", Client, Entry(Admin, TelegramEndpointAdminService.Action), default);
        /// <summary>Opens inventory then selects the primary bot using its real emitted nonce.</summary>
        /// <returns>A task completing after detail rendering.</returns>
        public async Task OpenDetailAsync()
        {
            await OpenAsync();
            await TapAsync(Control, Button(Control, "جزئیات «owned_bot»"));
        }
        /// <summary>Taps a control through its exact receiving owned host.</summary>
        /// <param name="control">Captured displayed message supplying binding.</param>
        /// <param name="data">Actual rendered or intentionally corrupted payload.</param>
        /// <returns>The real panel handler's consumed-update result.</returns>
        public Task<bool> TapAsync(EditMessageTextRequest control, string data) => Panel.TryHandleAsync("owned", Client, Callback(control, data), default);
    }

    /// <summary>Supplies deterministic metadata and records intents without pretending to implement the migration protocol.</summary>
    private sealed class Backend : ITelegramEndpointAdministration
    {
        /// <summary>Current independently mutable identity metadata.</summary>
        public Dictionary<string, TelegramEndpointState> States { get; } = new(StringComparer.Ordinal);
        /// <summary>Queued migration requests observed at the UI boundary.</summary>
        public ConcurrentQueue<(string, TelegramEndpointType, long, long, long)> Migrations { get; } = new();
        /// <summary>Safe or deliberately hostile history rows for UI output testing.</summary>
        public List<TelegramEndpointHistory> History { get; } = new();
        /// <summary>Number of metadata reads, proving denied entries stop before backend access.</summary>
        public int Reads { get; private set; }
        /// <summary>Optional assertion called at the migration queue boundary.</summary>
        public Action? BeforeMigration { get; set; }
        /// <summary>Optional closed rejection outcome returned before recording a queued migration.</summary>
        public string? CommandOutcome { get; set; }
        /// <summary>Optional barrier keeping one request in flight while its duplicate is tested.</summary>
        public TaskCompletionSource? MigrationBarrier { get; set; }
        /// <inheritdoc />
        public TelegramEndpointSharedHealth SharedLocalHealth => new(false, null, null, null);
        /// <inheritdoc />
        public int PendingAlertCount => 2;
        /// <inheritdoc />
        public Task<int> GetPendingAlertCountAsync(CancellationToken cancellationToken) => Task.FromResult(PendingAlertCount);
        /// <inheritdoc />
        public Task<IReadOnlyList<TelegramEndpointState>> GetInventoryAsync(CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<IReadOnlyList<TelegramEndpointState>>(States.Values.Select(x => x.Copy()).ToArray());
        }
        /// <inheritdoc />
        public Task<TelegramEndpointState> GetStatusAsync(string botId, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(States[botId].Copy());
        }
        /// <inheritdoc />
        public async Task<string> RequestMigrationAsync(string botId, TelegramEndpointType target, long actor, long expectedControlRevision, long expectedTelegramBotId, CancellationToken cancellationToken)
        {
            BeforeMigration?.Invoke();
            if (States[botId].TelegramBotId != expectedTelegramBotId || States[botId].ControlRevision != expectedControlRevision) return "stale";
            if (CommandOutcome != null) return CommandOutcome;
            Migrations.Enqueue((botId, target, actor, expectedControlRevision, expectedTelegramBotId));
            if (MigrationBarrier != null) await MigrationBarrier.Task;
            States[botId].ControlRevision++;
            return "accepted";
        }
        /// <inheritdoc />
        public Task<string> SetAutoFailoverAsync(string botId, bool enabled, long actor, long expectedControlRevision, long expectedTelegramBotId, CancellationToken cancellationToken)
        {
            if (States[botId].TelegramBotId != expectedTelegramBotId || States[botId].ControlRevision != expectedControlRevision) return Task.FromResult("stale");
            States[botId].AutoFailoverEnabled = enabled;
            States[botId].ControlRevision++;
            return Task.FromResult("accepted");
        }
        /// <inheritdoc />
        public Task RefreshHealthAsync(string botId, CancellationToken cancellationToken) => Task.CompletedTask;
        /// <inheritdoc />
        public Task<IReadOnlyList<TelegramEndpointHistory>> GetHistoryAsync(string botId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<TelegramEndpointHistory>>(History.ToArray());
    }

    /// <summary>Provides an independently advanceable UTC clock without sleeping or global time mutation.</summary>
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => _now;
        /// <summary>Moves test time forward for strict absolute session-expiry checks.</summary>
        /// <param name="elapsed">Positive duration to advance this fixture's clock.</param>
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    /// <summary>Records actual SDK request objects and returns deterministic Bot API-shaped message identities.</summary>
    private sealed class RecordingClient : ITelegramBotClient
    {
        /// <summary>All produced control-message edits.</summary>
        public List<EditMessageTextRequest> Edits { get; } = new();
        /// <summary>All produced initial message sends.</summary>
        public List<SendMessageRequest> Sends { get; } = new();
        /// <summary>All authorization/error/success callback acknowledgements.</summary>
        public List<AnswerCallbackQueryRequest> Answers { get; } = new();
        /// <inheritdoc />
        public bool LocalBotServer => false;
        /// <inheritdoc />
        public long BotId => 1000;
        /// <inheritdoc />
        public TimeSpan Timeout { get; set; }
        /// <inheritdoc />
        public IExceptionParser ExceptionsParser { get; set; } = null!;
        /// <inheritdoc />
        public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest { add { } remove { } }
        /// <inheritdoc />
        public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived { add { } remove { } }
        /// <inheritdoc />
        public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);
        /// <inheritdoc />
        public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default) => throw new InvalidOperationException("File access is outside panel scope.");
        /// <inheritdoc />
        public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default) => throw new InvalidOperationException("File access is outside panel scope.");
        /// <inheritdoc />
        public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (request)
            {
                case SendMessageRequest send:
                    Sends.Add(send);
                    return Task.FromResult((TResponse)(object)new Message { Id = Sends.Count, Chat = new Chat { Id = Admin, Type = ChatType.Private } });
                case EditMessageTextRequest edit:
                    Edits.Add(edit);
                    return Task.FromResult((TResponse)(object)new Message { Id = edit.MessageId, Chat = new Chat { Id = Admin, Type = ChatType.Private } });
                case AnswerCallbackQueryRequest answer:
                    Answers.Add(answer);
                    return Task.FromResult((TResponse)(object)true);
                default: throw new InvalidOperationException("Unexpected SDK request in panel test.");
            }
        }
    }
}
