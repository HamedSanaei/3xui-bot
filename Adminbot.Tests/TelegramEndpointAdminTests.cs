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

    /// <summary>Requires confirmation and publishes non-success progress before the coordinator can fence the host, even after an earlier successful migration.</summary>
    /// <returns>A task completing after one accepted intent, truthful pre-registration output and replay rejection.</returns>
    /// <remarks>Earlier activation evidence cannot be reused for a newly registering request, and accepted registration requires no post-fence edit.</remarks>
    [Fact]
    public async Task Migration_requires_confirmation_and_progress_precedes_queue()
    {
        var fixture = new Fixture();
        var previous = fixture.Backend.States["owned"];
        previous.OperationId = "previous-success";
        previous.MigrationStartedAtUtc = fixture.Clock.GetUtcNow().UtcDateTime.AddMinutes(-2);
        previous.LastMigrationAtUtc = fixture.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1);
        fixture.Backend.AddActivation("owned");
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
            Assert.DoesNotContain("نتیجه انتقال: موفق", fixture.Control.Text);
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

    /// <summary>Enumerates owned, tenant, assistant and disabled current identities through bounded readable pages.</summary>
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

    /// <summary>Initial inventory totals cover all pages and actual routes across bot types, then change on a read-only refresh.</summary>
    /// <returns>A task after the first sent message, next page and fresh runtime observations are checked.</returns>
    /// <remarks>Saved Local preferences do not count a fenced source as active; disabled and unobserved routes occupy separate buckets.</remarks>
    [Fact]
    public async Task Inventory_totals_cover_all_pages_and_refresh_actual_routes()
    {
        var fixture = new Fixture(extraBots: 13);
        foreach (var id in new[] { "tenant", "assistant", "owned-assistant" })
            fixture.Backend.States[id].RuntimeEndpoint = TelegramEndpointType.Local;
        var paused = fixture.Backend.States["extra0"];
        paused.DesiredEndpoint = paused.EffectiveEndpoint = TelegramEndpointType.Local;
        paused.RuntimeEndpoint = TelegramEndpointType.Local;
        paused.RuntimeAvailable = false;
        paused.MigrationState = TelegramEndpointMigrationState.CloudWait;
        var unknown = fixture.Backend.States["extra12"];
        unknown.RuntimeAvailable = null;
        unknown.RuntimeEndpoint = null;
        unknown.RuntimeGeneration = null;

        await fixture.OpenAsync();
        var initial = Assert.Single(fixture.Client.Sends).Text!;
        Assert.Contains("📊 مجموع ربات‌ها: 19", initial);
        Assert.Contains("☁️ CLOUD: 13 | 🏠 LOCAL: 3", initial);
        Assert.Contains("⛔ متوقف/غیرفعال: 2 | ❔ نامشخص: 1", initial);
        Assert.True(initial.IndexOf("☁️ CLOUD: 13", StringComparison.Ordinal) <
            initial.IndexOf("فهرست ربات‌ها — صفحه", StringComparison.Ordinal));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "صفحه بعد ▶️"));
        Assert.Contains("☁️ CLOUD: 13 | 🏠 LOCAL: 3", fixture.Control.Text);

        fixture.Backend.States["tenant"].RuntimeEndpoint = TelegramEndpointType.Cloud;
        unknown.RuntimeAvailable = true;
        unknown.RuntimeEndpoint = TelegramEndpointType.Cloud;
        unknown.RuntimeGeneration = 2;
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🔄 تازه‌سازی فهرست"));
        Assert.Contains("☁️ CLOUD: 15 | 🏠 LOCAL: 2", fixture.Control.Text);
        Assert.Contains("⛔ متوقف/غیرفعال: 2 | ❔ نامشخص: 0", fixture.Control.Text);
        Assert.Empty(fixture.Backend.Migrations);
        Assert.Empty(fixture.Backend.Batches);
    }

    /// <summary>An incomplete observed generation cannot enter active Local counts even when durable preferences and endpoint say Local.</summary>
    /// <returns>A task after the first inventory message reports unknown rather than fabricated active routing.</returns>
    [Fact]
    public async Task Inventory_totals_do_not_fill_missing_generation_from_saved_endpoint()
    {
        var fixture = new Fixture();
        var state = fixture.Backend.States["tenant"];
        state.DesiredEndpoint = state.EffectiveEndpoint = TelegramEndpointType.Local;
        state.RuntimeEndpoint = TelegramEndpointType.Local;
        state.RuntimeGeneration = null;
        await fixture.OpenAsync();
        var initial = Assert.Single(fixture.Client.Sends).Text!;
        Assert.Contains("☁️ CLOUD: 4 | 🏠 LOCAL: 0", initial);
        Assert.Contains("⛔ متوقف/غیرفعال: 1 | ❔ نامشخص: 1", initial);
    }

    /// <summary>Keeps operational safety readable in the main screen while moving timestamps, mapping and safe history to complete technical pages.</summary>
    /// <returns>A task completing after actual SDK output proves the separation and secret-free technical navigation.</returns>
    [Fact]
    public async Task Detail_exposes_safety_prerequisites_and_sanitizes_history()
    {
        var fixture = new Fixture();
        var state = fixture.Backend.States["owned"];
        state.MigrationState = TelegramEndpointMigrationState.CloudLogoutUncertain;
        state.OperationId = "current-operation";
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
        AssertMainSurface(fixture.Control.Text);
        Assert.Contains("نیازمند بررسی دستی", fixture.Control.Text);
        var text = await fixture.ReadTechnicalAsync();
        Assert.Contains("600 ثانیه", text);
        Assert.Contains("UTC", text);
        Assert.Contains("نگاشت مطمئن", text);
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
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "جزئیات «tenant_bot»"));
        Assert.StartsWith("❔ اتصال نامشخص", fixture.Control.Text);
        Assert.DoesNotContain("☁️ CLOUD", fixture.Control.Text);
        AssertMainSurface(fixture.Control.Text);
        Assert.DoesNotContain(Buttons(fixture.Control), x => x.Text.StartsWith("انتقال به", StringComparison.Ordinal));
        Assert.DoesNotContain(Buttons(fixture.Control), x => x.Text.Contains("بازگشت اضطراری", StringComparison.Ordinal));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🔄 تازه‌سازی وضعیت"));
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
        state.RuntimeEndpoint = TelegramEndpointType.Local;
        state.RuntimeAvailable = true;
        state.LastMigrationAtUtc = fixture.Clock.GetUtcNow().UtcDateTime.AddSeconds(1);
        fixture.Backend.AddActivation("owned");
        await fixture.TapAsync(progress, Button(progress, "🔄 تازه‌سازی وضعیت و سلامت"));
        Assert.Single(fixture.Backend.Migrations);
        Assert.StartsWith("🏠 LOCAL", fixture.Control.Text);
        Assert.Contains("نتیجه انتقال: موفق", fixture.Control.Text);
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

    /// <summary>Uses actual request admission, not preference or last activation, and keeps protocol metadata behind technical navigation.</summary>
    /// <param name="available">Observed runtime admission, including unknown.</param>
    /// <returns>A task completing after the consumer-visible active route badge is checked.</returns>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Active_badge_uses_runtime_not_desired_or_last_activated(bool? available)
    {
        var fixture = new Fixture();
        var state = fixture.Backend.States["owned"];
        state.DesiredEndpoint = TelegramEndpointType.Local;
        state.EffectiveEndpoint = TelegramEndpointType.Local;
        state.RuntimeEndpoint = available.HasValue ? TelegramEndpointType.Cloud : null;
        state.RuntimeAvailable = available;
        state.RuntimeGeneration = available.HasValue ? 8 : null;
        await fixture.OpenDetailAsync();
        Assert.StartsWith(available == true ? "☁️ CLOUD" :
            available == false ? "⛔ اتصال متوقف" : "❔ اتصال نامشخص", fixture.Control.Text);
        Assert.DoesNotContain("🏠 LOCAL", fixture.Control.Text);
        AssertMainSurface(fixture.Control.Text);
        await fixture.OpenAsync();
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "صفحه بعد ▶️"));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "جزئیات «disabled_bot»"));
        Assert.StartsWith("⛔ اتصال غیرفعال؛ ربات غیرفعال", fixture.Control.Text);
    }

    /// <summary>Old activation, restored source and ambiguous cleanup never count as successful destination activation.</summary>
    /// <param name="scenario">Current operation phase or evidence boundary.</param>
    /// <returns>A task completing after truthful outcome and required action are visible ahead of telemetry.</returns>
    [Theory]
    [InlineData("pending")]
    [InlineData("cooldown")]
    [InlineData("refused")]
    [InlineData("failed")]
    [InlineData("uncertain")]
    [InlineData("preference")]
    [InlineData("old_receipt")]
    public async Task Latest_outcome_never_reuses_old_activation(string scenario)
    {
        var fixture = new Fixture();
        var state = fixture.Backend.States["owned"];
        state.OperationId = "current-operation";
        state.MigrationStartedAtUtc = fixture.Clock.GetUtcNow().UtcDateTime;
        state.LastMigrationAtUtc = state.MigrationStartedAtUtc.Value.AddMinutes(-1);
        state.DesiredEndpoint = TelegramEndpointType.Local;
        state.MigrationState = TelegramEndpointMigrationState.CheckingLocal;
        if (scenario == "cooldown") state.MigrationState = TelegramEndpointMigrationState.CloudWait;
        if (scenario == "refused")
        {
            state.MigrationState = TelegramEndpointMigrationState.Cloud;
            state.LastFailureCategory = "logout_refused";
            fixture.Backend.History.Add(new TelegramEndpointHistory { OperationId = state.OperationId, Reason = "logout_refused" });
        }
        if (scenario == "failed") state.MigrationState = TelegramEndpointMigrationState.MigrationFailed;
        if (scenario == "uncertain") state.MigrationState = TelegramEndpointMigrationState.CloudLogoutUncertain;
        if (scenario is "preference" or "old_receipt")
        {
            state.EffectiveEndpoint = TelegramEndpointType.Local;
            state.MigrationState = TelegramEndpointMigrationState.Local;
            state.LastMigrationAtUtc = state.MigrationStartedAtUtc;
            fixture.Backend.History.Add(new TelegramEndpointHistory { OperationId = "earlier-operation",
                Reason = "migration_succeeded", ToEffectiveEndpoint = TelegramEndpointType.Local });
            state.LastFailureCategory = "network";
        }
        await fixture.OpenDetailAsync();
        Assert.DoesNotContain("✅ نتیجه انتقال: موفق", fixture.Control.Text);
        if (scenario is "preference" or "old_receipt") Assert.DoesNotContain("نتیجه انتقال: ناموفق", fixture.Control.Text);
        Assert.Contains(scenario switch
        {
            "refused" => "خروج رد شد",
            "failed" => "نتیجه انتقال: ناموفق",
            "uncertain" => "نیازمند بررسی دستی",
            "cooldown" => "مهلت رسمی CLOUD",
            "preference" or "old_receipt" => "برای همین عملیات اثبات نشده",
            _ => "ثبت درخواست به معنی تکمیل نیست"
        }, fixture.Control.Text);
        AssertMainSurface(fixture.Control.Text);
    }

    /// <summary>A committed destination remains a successful migration even if its later health degrades or admission closes.</summary>
    /// <returns>A task completing after success evidence and separate current degradation are both visible.</returns>
    [Fact]
    public async Task Successful_migration_and_later_health_failure_are_separate()
    {
        var fixture = new Fixture();
        var state = fixture.Backend.States["owned"];
        state.OperationId = "current-operation";
        state.MigrationStartedAtUtc = fixture.Clock.GetUtcNow().UtcDateTime;
        state.LastMigrationAtUtc = state.MigrationStartedAtUtc.Value.AddSeconds(1);
        state.DesiredEndpoint = state.EffectiveEndpoint = TelegramEndpointType.Local;
        state.RuntimeEndpoint = TelegramEndpointType.Local;
        state.RuntimeAvailable = false;
        state.MigrationState = TelegramEndpointMigrationState.LocalUnavailable;
        state.LastFailureCategory = "network";
        fixture.Backend.AddActivation("owned");
        await fixture.OpenDetailAsync();
        Assert.StartsWith("⛔ اتصال متوقف", fixture.Control.Text);
        Assert.Contains("✅ نتیجه انتقال: موفق", fixture.Control.Text);
        Assert.Contains("پس از انتقال موفق", fixture.Control.Text);
        Assert.DoesNotContain("🏠 LOCAL", fixture.Control.Text);
    }

    /// <summary>Bulk confirmation freezes every inventory page, excludes later additions and lets CAS refuse replacements rather than silently retargeting them.</summary>
    /// <returns>A task completing after one full frozen bulk API call, readable pagination and replay rejection.</returns>
    [Fact]
    public async Task Bulk_confirmation_freezes_full_inventory_and_never_replays()
    {
        var fixture = new Fixture(extraBots: 13);
        await fixture.OpenAsync();
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "صفحه بعد ▶️"));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🏠 انتقال همه ربات‌ها به Local…"));
        Assert.Empty(fixture.Backend.Batches);
        var confirmation = fixture.Control;
        Assert.Contains("19 ربات", confirmation.Text);
        Assert.Contains("آخرین مسیر مستقل Cloud دور زده نمی‌شود", confirmation.Text);
        var originals = fixture.Backend.States.Values.Select(x => (x.BotId, x.TelegramBotId, x.ControlRevision)).ToArray();
        fixture.Backend.States["tenant"].TelegramBotId++;
        fixture.Backend.States["extra12"].ControlRevision++;
        fixture.Backend.States.Add("new", new TelegramEndpointState { BotId = "new", TelegramBotId = 5000 });
        fixture.Backend.BulkCodes["owned"] = "retained_cloud_control";
        fixture.Backend.BeforeBulk = () =>
        {
            Assert.Contains("ثبت درخواست‌ها در جریان", fixture.Control.Text);
            Assert.Contains(Buttons(fixture.Control), x => x.Text == "🔄 تازه‌سازی گزارش (بدون ثبت مجدد)");
        };
        var data = Button(confirmation, "✅ تأیید انتقال دسته‌ای به Local (محلی)");
        var edits = fixture.Client.Edits.Count;
        await fixture.TapAsync(confirmation, data);
        Assert.Equal(edits + 1, fixture.Client.Edits.Count);
        var batch = Assert.Single(fixture.Backend.Batches);
        Assert.Equal(originals, batch.Targets.Select(x => (x.BotId, x.Identity, x.Revision)).ToArray());
        Assert.Equal(("owned", 1000L, TelegramEndpointType.Local), (batch.Host, batch.Identity, batch.Target));
        await fixture.TapAsync(confirmation, data);
        Assert.Single(fixture.Backend.Batches);
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🔄 تازه‌سازی گزارش (بدون ثبت مجدد)"));
        Assert.Contains("روی Cloud نگه داشته شد", fixture.Control.Text);
        Assert.Contains("هویت/عملیات تغییر کرده", fixture.Control.Text);
        var observed = new List<string>();
        while (true)
        {
            var text = Assert.IsType<string>(fixture.Control.Text);
            Assert.InRange(text.Length, 1, 3900);
            observed.AddRange(text.Split('\n').Where(x => x.StartsWith("ربات:", StringComparison.Ordinal)));
            var next = Buttons(fixture.Control).SingleOrDefault(x => x.Text == "صفحه بعد گزارش ▶️");
            if (next == null) break;
            await fixture.TapAsync(fixture.Control, next.CallbackData!);
        }
        Assert.Equal(19, observed.Count);
        Assert.DoesNotContain(observed, x => x.Contains("ربات: new", StringComparison.Ordinal));
        Assert.Single(fixture.Backend.Batches);
    }

    /// <summary>Per-bot API partial results survive refresh; acceptance and proven execution completion have separate aggregate counts.</summary>
    /// <returns>A task completing after partial registration, execution, supersession and absolute report expiry without re-registration.</returns>
    [Fact]
    public async Task Partial_bulk_results_refresh_execution_without_re_registration()
    {
        var fixture = new Fixture();
        fixture.Backend.BulkCodes["alternate"] = "retained_cloud_control";
        fixture.Backend.BulkCodes["tenant"] = "not_submitted";
        fixture.Backend.BulkCodes["assistant"] = "registration_uncertain";
        fixture.Backend.BulkCodes["owned-assistant"] = "disabled";
        fixture.Backend.BulkCodes["disabled"] = "disabled";
        await fixture.OpenAsync();
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🏠 انتقال همه ربات‌ها به Local…"));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "✅ تأیید انتقال دسته‌ای به Local (محلی)"));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🔄 تازه‌سازی گزارش (بدون ثبت مجدد)"));
        Assert.Contains("ثبت پذیرفته: 1 | در انتظار اجرا: 1 | انتقال موفق: 0 | رد/ناموفق: 2", fixture.Control.Text);
        Assert.Contains("ثبت نامطمئن", fixture.Control.Text);
        Assert.Contains("ادامه دسته پس از توقف/لغو", fixture.Control.Text);
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "صفحه بعد گزارش ▶️"));
        Assert.Contains("ممکن است درخواست پایدار شده باشد", fixture.Control.Text);
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "◀️ صفحه قبل گزارش"));
        var state = fixture.Backend.States["owned"];
        state.EffectiveEndpoint = TelegramEndpointType.Local;
        state.MigrationState = TelegramEndpointMigrationState.Local;
        state.LastMigrationAtUtc = fixture.Clock.GetUtcNow().UtcDateTime.AddSeconds(1);
        state.RuntimeEndpoint = TelegramEndpointType.Local;
        fixture.Backend.AddActivation("owned");
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🔄 تازه‌سازی گزارش (بدون ثبت مجدد)"));
        Assert.Contains("در انتظار اجرا: 0 | انتقال موفق: 1", fixture.Control.Text);
        state.LastFailureCategory = "local_file_mapping_missing";
        fixture.Backend.History.Add(new TelegramEndpointHistory { BotId = state.BotId, TelegramBotId = state.TelegramBotId,
            OperationId = state.OperationId, Reason = "migration_admission_failed", CreatedAtUtc = state.LastMigrationAtUtc.Value.AddSeconds(1) });
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🔄 تازه‌سازی گزارش (بدون ثبت مجدد)"));
        Assert.Contains("انتقال موفق: 1", fixture.Control.Text);
        state.OperationId = "superseded";
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🔄 تازه‌سازی گزارش (بدون ثبت مجدد)"));
        Assert.Contains("انتقال موفق: 0", fixture.Control.Text);
        Assert.Contains("درخواست دیگری جایگزین", fixture.Control.Text);
        fixture.Clock.Advance(TimeSpan.FromMinutes(59));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🔄 تازه‌سازی گزارش (بدون ثبت مجدد)"));
        await fixture.OpenAsync();
        await fixture.TapAsync(fixture.Control, Buttons(fixture.Control).Single(x => x.Text.StartsWith("📊 گزارش دسته‌ای", StringComparison.Ordinal)).CallbackData!);
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🔄 تازه‌سازی گزارش (بدون ثبت مجدد)"));
        Assert.Contains("گزارش دسته‌ای منقضی", fixture.Control.Text);
        Assert.Single(fixture.Backend.Batches);
    }

    /// <summary>Cloud cooldown outlives confirmation controls but must not erase read-only batch reporting before destination activation can finish.</summary>
    /// <returns>A task completing after reopening the same actor-scoped report beyond ten minutes without another registration call.</returns>
    [Fact]
    public async Task Bulk_report_survives_cloud_cooldown_without_extending_confirmation_authority()
    {
        var fixture = new Fixture();
        await fixture.OpenAsync();
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "☁️ انتقال همه ربات‌ها به Cloud…"));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "✅ تأیید انتقال دسته‌ای به Cloud (ابری)"));
        var state = fixture.Backend.States["owned"];
        state.MigrationState = TelegramEndpointMigrationState.CloudWait;
        state.RuntimeAvailable = false;
        fixture.Clock.Advance(TimeSpan.FromMinutes(11));
        await fixture.OpenAsync();
        await fixture.TapAsync(fixture.Control, Buttons(fixture.Control).Single(x => x.Text.StartsWith("📊 گزارش دسته‌ای", StringComparison.Ordinal)).CallbackData!);
        Assert.Contains("در انتظار مهلت رسمی CLOUD", fixture.Control.Text);
        Assert.Contains("پذیرش درخواست بسته است", fixture.Control.Text);
        Assert.Single(fixture.Backend.Batches);
    }

    /// <summary>Already-Cloud entries with old proven migrations are not counted as newly successful operations in a no-op Cloud batch.</summary>
    /// <returns>A task completing after the real report distinguishes unchanged destinations from the new batch's successful migrations.</returns>
    [Fact]
    public async Task Bulk_unchanged_old_activation_is_not_new_batch_success()
    {
        var fixture = new Fixture();
        foreach (var state in fixture.Backend.States.Values)
        {
            fixture.Backend.BulkCodes[state.BotId] = "unchanged";
            state.OperationId = Guid.NewGuid().ToString("N");
            state.MigrationStartedAtUtc = fixture.Clock.GetUtcNow().UtcDateTime.AddDays(-1);
            state.LastMigrationAtUtc = state.MigrationStartedAtUtc.Value.AddMinutes(1);
            fixture.Backend.AddActivation(state.BotId);
        }
        await fixture.OpenAsync();
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "☁️ انتقال همه ربات‌ها به Cloud…"));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "✅ تأیید انتقال دسته‌ای به Cloud (ابری)"));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🔄 تازه‌سازی گزارش (بدون ثبت مجدد)"));
        Assert.Contains("انتقال موفق: 0", fixture.Control.Text);
        Assert.Contains("از قبل روی مقصد: 6", fixture.Control.Text);
        Assert.Single(fixture.Backend.Batches);
    }

    /// <summary>The host may close immediately after Cloud batch registration; pre-existing progress is usable from another authorized owned entry.</summary>
    /// <returns>A task completing after recovery-host report access with no promised post-fence edit or repeated API call.</returns>
    [Fact]
    public async Task Bulk_host_migration_pre_renders_progress_and_allows_other_owned_entry()
    {
        var fixture = new Fixture();
        await fixture.OpenAsync();
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "☁️ انتقال همه ربات‌ها به Cloud…"));
        Assert.Contains("مهلت رسمی Cloud", fixture.Control.Text);
        fixture.Backend.BeforeBulk = () => fixture.Backend.States["owned"].RuntimeAvailable = false;
        var edits = fixture.Client.Edits.Count;
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "✅ تأیید انتقال دسته‌ای به Cloud (ابری)"));
        Assert.Equal(edits + 1, fixture.Client.Edits.Count);
        Assert.Contains("ارسال به‌روزرسانی نهایی تضمین نمی‌شود", fixture.Control.Text);
        await fixture.Panel.TryHandleAsync("alternate", fixture.Client, Entry(Admin, "/telegram_api"), default);
        var reportButton = Buttons(fixture.Control).Single(x => x.Text.StartsWith("📊 گزارش دسته‌ای", StringComparison.Ordinal));
        await fixture.Panel.TryHandleAsync("alternate", fixture.Client, Callback(fixture.Control, reportButton.CallbackData!), default);
        Assert.Contains("📊 گزارش انتقال دسته‌ای", fixture.Control.Text);
        Assert.Single(fixture.Backend.Batches);
    }

    /// <summary>Bulk controls share exact actor/chat/message/host bindings, fresh authorization, strict expiry and malformed-payload rejection.</summary>
    /// <param name="attack">The callback security boundary or stale host to exercise.</param>
    /// <returns>A task completing with no bulk API request or single migration intent.</returns>
    [Theory]
    [InlineData("actor")]
    [InlineData("host")]
    [InlineData("tenant")]
    [InlineData("chat")]
    [InlineData("message")]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("malformed")]
    [InlineData("identity")]
    [InlineData("revision")]
    public async Task Bulk_confirmation_rejects_forged_stale_or_expired_callbacks(string attack)
    {
        var fixture = new Fixture();
        await fixture.OpenAsync();
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🏠 انتقال همه ربات‌ها به Local…"));
        var confirmation = fixture.Control;
        var data = Button(confirmation, "✅ تأیید انتقال دسته‌ای به Local (محلی)");
        if (attack == "revoked") fixture.Configuration.AdminsUserIds.Clear();
        if (attack == "expired") fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        if (attack == "malformed") data += ":extra";
        if (attack == "identity") fixture.Backend.States["owned"].TelegramBotId++;
        if (attack == "revision") fixture.Backend.States["owned"].ControlRevision++;
        await fixture.Panel.TryHandleAsync(attack == "host" ? "alternate" : attack == "tenant" ? "tenant" : "owned",
            fixture.Client, Callback(confirmation, data, actor: attack == "actor" ? 202 : Admin,
                chatId: attack == "chat" ? Admin + 1 : Admin,
                messageId: attack == "message" ? confirmation.MessageId + 1 : null), default);
        Assert.Empty(fixture.Backend.Batches);
        Assert.Empty(fixture.Backend.Migrations);
    }

    /// <summary>Cancel never submits a batch; an interrupted API call is reported uncertain and refresh cannot repeat it.</summary>
    /// <returns>A task completing after cancel and honest exception-boundary reporting.</returns>
    [Fact]
    public async Task Bulk_cancel_and_registration_exception_never_replay()
    {
        var fixture = new Fixture();
        await fixture.OpenAsync();
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🏠 انتقال همه ربات‌ها به Local…"));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "❌ انصراف"));
        Assert.Empty(fixture.Backend.Batches);
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🏠 انتقال همه ربات‌ها به Local…"));
        fixture.Backend.ThrowBulk = true;
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "✅ تأیید انتقال دسته‌ای به Local (محلی)"));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🔄 تازه‌سازی گزارش (بدون ثبت مجدد)"));
        Assert.Contains("ممکن است درخواست پایدار شده باشد", fixture.Control.Text);
        Assert.Contains("انتقال موفق: 0", fixture.Control.Text);
        Assert.Single(fixture.Backend.Batches);
    }

    /// <summary>Preserves API-supplied committed and unsubmitted results when caller cancellation occurs after progress is already published.</summary>
    /// <returns>A task completing after later read-only refresh exposes honest partial results without retrying cancellation.</returns>
    [Fact]
    public async Task Callback_cancellation_preserves_supplied_partial_registration_results()
    {
        var fixture = new Fixture();
        foreach (var id in fixture.Backend.States.Keys.Where(x => x != "owned"))
            fixture.Backend.BulkCodes[id] = "not_submitted";
        await fixture.OpenAsync();
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "☁️ انتقال همه ربات‌ها به Cloud…"));
        var confirmation = fixture.Control;
        using var cancellation = new CancellationTokenSource();
        fixture.Backend.BeforeBulk = cancellation.Cancel;
        await fixture.Panel.TryHandleAsync("owned", fixture.Client,
            Callback(confirmation, Button(confirmation, "✅ تأیید انتقال دسته‌ای به Cloud (ابری)")), cancellation.Token);
        Assert.True(cancellation.IsCancellationRequested);
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🔄 تازه‌سازی گزارش (بدون ثبت مجدد)"));
        Assert.Contains("ثبت پذیرفته: 1 | در انتظار اجرا: 1 | انتقال موفق: 0", fixture.Control.Text);
        Assert.Contains("ادامه دسته پس از توقف/لغو", fixture.Control.Text);
        Assert.Single(fixture.Backend.Batches);
    }

    /// <summary>Proves a refused validation request survives refresh as a bound diagnostic rather than a transient toast or false migration.</summary>
    /// <returns>A task completing after main and technical SDK screens distinguish refusal from accepted execution.</returns>
    /// <remarks>The receipt remains bound to actor/message session, original bot identity and control revision; technical reads never replay it.</remarks>
    [Fact]
    public async Task Validation_refusal_is_retained_for_read_only_technical_navigation()
    {
        var fixture = new Fixture();
        fixture.Backend.CommandOutcome = "local_file_access_denied";
        await fixture.OpenDetailAsync();
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "انتقال به Local…"));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "✅ تأیید انتقال به Local (محلی)"));
        Assert.Empty(fixture.Backend.Migrations);
        Assert.Contains("هیچ درخواست انتقالی ثبت نشد", fixture.Control.Text);
        AssertMainSurface(fixture.Control.Text);
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🔄 تازه‌سازی وضعیت و سلامت"));
        Assert.Contains("هیچ درخواست انتقالی ثبت نشد", fixture.Control.Text);
        var diagnostic = TelegramEndpointDiagnosticCatalog.Describe("local_file_access_denied");
        var technical = await fixture.ReadTechnicalAsync();
        Assert.Contains(diagnostic.Code, technical);
        Assert.Contains(diagnostic.Stage, technical);
        Assert.Contains(diagnostic.Checked, technical);
        Assert.Contains(diagnostic.Action, technical);
        Assert.Equal(1, fixture.Backend.HealthRefreshes);
        Assert.Empty(fixture.Backend.Migrations);
    }

    /// <summary>A pending operation names historical source and actual current admission separately, including a fully paused connection.</summary>
    /// <param name="admitted">True leaves the observed CLOUD route admitted; false represents a fenced source.</param>
    /// <returns>A task completing after readable non-success progress on real SDK output.</returns>
    /// <remarks>The source is historical protocol metadata; only current runtime admission may be presented as the connection.</remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pending_source_never_impersonates_the_current_connection(bool admitted)
    {
        var fixture = new Fixture();
        var state = fixture.Backend.States["owned"];
        state.OperationId = "pending";
        state.DesiredEndpoint = TelegramEndpointType.Local;
        state.MigrationState = TelegramEndpointMigrationState.SwitchingToLocal;
        state.RuntimeAvailable = admitted;
        state.LogoutEndpoint = TelegramEndpointType.Cloud;
        await fixture.OpenDetailAsync();
        Assert.Contains("⏳ در حال انتقال", fixture.Control.Text);
        Assert.Contains("مبدأ انتقال: CLOUD → مقصد: LOCAL", fixture.Control.Text);
        Assert.Contains(admitted ? "اتصال فعلی: ☁️ CLOUD" : "اتصال فعلی: ⛔ اتصال متوقف", fixture.Control.Text);
        Assert.DoesNotContain("نتیجه انتقال: موفق", fixture.Control.Text);
        AssertMainSurface(fixture.Control.Text);
    }

    /// <summary>Technical callbacks inherit all authorization, identity, revision, expiry and one-use boundaries without invoking probes or mutations.</summary>
    /// <param name="attack">Synthetic callback forgery or freshness/replay violation.</param>
    /// <remarks>Stale navigation may render a fresh snapshot but must acknowledge rejection and never run a mutation or health probe.</remarks>
    /// <returns>A task completing after rejected read-only navigation and zero migration/probe calls.</returns>
    [Theory]
    [InlineData("actor")]
    [InlineData("host")]
    [InlineData("message")]
    [InlineData("chat")]
    [InlineData("expiry")]
    [InlineData("identity")]
    [InlineData("revision")]
    [InlineData("replay")]
    public async Task Technical_navigation_rejects_forged_expired_stale_or_replayed_controls(string attack)
    {
        var fixture = new Fixture();
        await fixture.OpenDetailAsync();
        var control = fixture.Control;
        var data = Button(control, "🔍 جزئیات فنی");
        if (attack == "expiry") fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        if (attack == "identity") fixture.Backend.States["owned"].TelegramBotId++;
        if (attack == "revision") fixture.Backend.States["owned"].ControlRevision++;
        if (attack == "replay") await fixture.TapAsync(control, data);
        var edits = fixture.Client.Edits.Count;
        await fixture.Panel.TryHandleAsync(attack == "host" ? "alternate" : "owned", fixture.Client,
            Callback(control, data, actor: attack == "actor" ? 202 : Admin,
                messageId: attack == "message" ? control.MessageId + 1 : null, chatId: attack == "chat" ? Admin + 1 : Admin), default);
        Assert.Empty(fixture.Backend.Migrations);
        Assert.Empty(fixture.Backend.Batches);
        Assert.Equal(0, fixture.Backend.HealthRefreshes);
        Assert.NotNull(fixture.Client.Answers.Last().Text);
        if (attack is not ("identity" or "revision")) Assert.Equal(edits, fixture.Client.Edits.Count);
        else Assert.Contains("هویت ربات تغییر", fixture.Client.Answers.Last().Text);
    }

    /// <summary>Batch output keeps counts/routes/outcomes concise and exposes retained refusal diagnostics through a secure per-bot technical button.</summary>
    /// <returns>A task completing after bulk technical navigation and return without registering again.</returns>
    /// <remarks>Batch counts remain attributable to frozen identities and exact committed operations, not newly visited technical snapshots.</remarks>
    [Fact]
    public async Task Bulk_technical_details_preserve_refusal_and_return_without_registration()
    {
        var fixture = new Fixture();
        fixture.Backend.BulkCodes["tenant"] = "local_file_mapping_missing";
        await fixture.OpenAsync();
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🏠 انتقال همه ربات‌ها به Local…"));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "✅ تأیید انتقال دسته‌ای به Local (محلی)"));
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🔄 تازه‌سازی گزارش (بدون ثبت مجدد)"));
        AssertMainSurface(fixture.Control.Text);
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "🔍 جزئیات فنی «tenant_bot»"));
        Assert.Contains(TelegramEndpointDiagnosticCatalog.Describe("local_file_mapping_missing").Code, fixture.Control.Text);
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "📊 بازگشت به گزارش"));
        AssertMainSurface(fixture.Control.Text);
        Assert.Single(fixture.Backend.Batches);
        Assert.Equal(0, fixture.Backend.HealthRefreshes);
    }

    /// <summary>Shows authoritative configuration provenance and differing live/startup mapping without hot-changing routing or probing a bot.</summary>
    /// <returns>A task completing after SDK technical output includes both mapping snapshots and restart guidance.</returns>
    /// <remarks>No private configuration file is loaded or modified by this scenario.</remarks>
    [Fact]
    public async Task Technical_mapping_provenance_and_fresh_errors_remain_read_only_and_complete()
    {
        var live = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AdminsUserIds:0"] = Admin.ToString(),
            ["telegramEndpointRouting:localFileServerRoot"] = "/current-server",
            ["telegramEndpointRouting:localFileHostRoot"] = "/current-host"
        }).Build();
        var root = "/startup-" + new string('a', 3400) + "-complete-root";
        var options = new TelegramEndpointRoutingOptions { LocalFileServerRoot = root, LocalFileHostRoot = "/startup-host" };
        var source = new ApplicationConfigurationSource(Path.Combine(Path.GetTempPath(), "telegram-panel-config"));
        var fixture = new Fixture(liveConfiguration: live, options: options, source: source);
        await fixture.OpenDetailAsync();
        AssertMainSurface(fixture.Control.Text);
        fixture.Backend.States["owned"].RuntimeGeneration = 77;
        fixture.Backend.States["owned"].LastFailureCategory = "local_destination_identity_identity_mismatch";
        var text = await fixture.ReadTechnicalAsync();
        Assert.Contains(source.FilePath, text);
        Assert.Contains("/startup-host", text);
        Assert.Contains("/current-server", text);
        Assert.Contains("/current-host", text);
        Assert.Contains("-complete-root", text);
        Assert.Contains("راه‌اندازی مجدد لازم است", text);
        Assert.Contains("Generation: 77", text);
        Assert.Contains(TelegramEndpointDiagnosticCatalog.Describe("local_destination_identity_identity_mismatch").Code, text);
        Assert.Equal(root, options.LocalFileServerRoot);
        Assert.Equal("/startup-host", options.LocalFileHostRoot);
        Assert.Empty(fixture.Backend.Migrations);
        Assert.Empty(fixture.Backend.Batches);
        Assert.Equal(0, fixture.Backend.HealthRefreshes);
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "↩️ بازگشت به وضعیت ربات"));
        AssertMainSurface(fixture.Control.Text);
        Assert.StartsWith("☁️ CLOUD", fixture.Control.Text);
    }

    /// <summary>A persisted admission refusal is visible without any old UI session, independently of an earlier proven migration result.</summary>
    /// <param name="previousSuccess">True supplies an older committed operation; false has never admitted a migration.</param>
    /// <returns>A task completing after real inventory/detail and technical screens distinguish refusal, prior success and no new request.</returns>
    /// <remarks>This models panel reopening after restart using durable metadata only, never a transient registration toast.</remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Persisted_validation_refusal_is_not_prior_migration_success(bool previousSuccess)
    {
        var fixture = new Fixture();
        var state = fixture.Backend.States["owned"];
        state.LastFailureCategory = "local_file_mapping_missing";
        state.LastFailureAtUtc = fixture.Clock.GetUtcNow().UtcDateTime;
        if (previousSuccess)
        {
            state.OperationId = "earlier-operation";
            state.MigrationStartedAtUtc = state.LastFailureAtUtc.Value.AddMinutes(-2);
            state.LastMigrationAtUtc = state.LastFailureAtUtc.Value.AddMinutes(-1);
            fixture.Backend.AddActivation("owned");
        }
        fixture.Backend.History.Add(new TelegramEndpointHistory { BotId = state.BotId, TelegramBotId = state.TelegramBotId,
            OperationId = state.OperationId, Reason = "migration_admission_failed", CreatedAtUtc = state.LastFailureAtUtc.Value });
        await fixture.OpenAsync();
        Assert.Contains("آخرین درخواست انتقال: اعتبارسنجی رد شد؛ هیچ درخواست جدیدی ثبت نشد", fixture.Control.Text);
        AssertMainSurface(fixture.Control.Text);
        await fixture.TapAsync(fixture.Control, Button(fixture.Control, "جزئیات «owned_bot»"));
        Assert.Contains("آخرین درخواست انتقال: اعتبارسنجی رد شد؛ هیچ درخواست جدیدی ثبت نشد", fixture.Control.Text);
        if (previousSuccess) Assert.Contains("آخرین انتقال ثبت‌شدهٔ پیشین:\n✅ نتیجه انتقال: موفق", fixture.Control.Text);
        else Assert.DoesNotContain("نتیجه انتقال: موفق", fixture.Control.Text);
        AssertMainSurface(fixture.Control.Text);
        var technical = await fixture.ReadTechnicalAsync();
        Assert.Contains(TelegramEndpointDiagnosticCatalog.Describe("local_file_mapping_missing").Code, technical);
        Assert.Empty(fixture.Backend.Migrations);
        Assert.Equal(0, fixture.Backend.HealthRefreshes);
    }

    /// <summary>Retains exact historical diagnostic stages after recovery clears the current failure, without deriving old stage from a newer operation.</summary>
    /// <returns>A task completing after full technical SDK output contains historical controlled code, checked boundary and action.</returns>
    /// <remarks>Legacy enum outcomes remain protocol labels; raw arbitrary historical values are never exposed.</remarks>
    [Fact]
    public async Task Historical_failures_keep_precise_diagnostics_after_health_recovers()
    {
        var fixture = new Fixture();
        var state = fixture.Backend.States["owned"];
        state.LastFailureCategory = null;
        fixture.Backend.History.Add(new TelegramEndpointHistory { BotId = state.BotId, TelegramBotId = state.TelegramBotId,
            Reason = "migration_failed", Outcome = "local_root_connection_refused", CreatedAtUtc = fixture.Clock.GetUtcNow().UtcDateTime });
        fixture.Backend.History.Add(new TelegramEndpointHistory { BotId = state.BotId, TelegramBotId = state.TelegramBotId,
            Reason = "logout_uncertain", Outcome = "local_logout_uncertain", CreatedAtUtc = fixture.Clock.GetUtcNow().UtcDateTime });
        await fixture.OpenDetailAsync();
        AssertMainSurface(fixture.Control.Text);
        var text = await fixture.ReadTechnicalAsync();
        foreach (var category in new[] { "local_root_connection_refused", "local_logout_uncertain" })
        {
            var diagnostic = TelegramEndpointDiagnosticCatalog.Describe(category);
            Assert.Contains(diagnostic.Code, text);
            Assert.Contains(diagnostic.Stage, text);
            Assert.Contains(diagnostic.Checked, text);
            Assert.Contains(diagnostic.Action, text);
        }
        Assert.Null(state.LastFailureCategory);
        Assert.Empty(fixture.Backend.Migrations);
        Assert.Equal(0, fixture.Backend.HealthRefreshes);
    }

    /// <summary>Asserts consumer-visible message bounds and the exclusion of technical metadata from the simple screen.</summary>
    /// <param name="text">Nullable actual SDK request text; absence fails the assertion, never substitutes placeholder content.</param>
    /// <remarks>Main screens show fixed Persian actions, with controlled machine codes only on technical pages.</remarks>
    private static void AssertMainSurface(string? text)
    {
        Assert.NotNull(text);
        Assert.InRange(text.Length, 1, 3900);
        foreach (var forbidden in new[] { "Generation", "UTC", "Gate", "gate", "Migration State", "تاریخچه", "شناسه Telegram", "هویت ثابت", "انتخاب ذخیره", "localFileHostRoot", "localFileServerRoot", "کد:", "مرحله:", "بررسی‌شده:" })
            Assert.DoesNotContain(forbidden, text);
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
        /// <param name="options">Optional startup mapping snapshot for provenance and long-path pagination tests.</param>
        /// <param name="source">Optional authoritative configuration location; no file is opened.</param>
        /// <remarks>Metadata and SDK capture are isolated; no network, database, or production mutation occurs.</remarks>
        public Fixture(int extraBots = 0, IConfiguration? liveConfiguration = null, TelegramEndpointRoutingOptions? options = null, ApplicationConfigurationSource? source = null)
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
                Backend.States.Add(id, new TelegramEndpointState { BotId = id, TelegramBotId = 1000 + i,
                    RuntimeEndpoint = TelegramEndpointType.Cloud, RuntimeAvailable = id != "disabled", RuntimeGeneration = 1 });
            }
            var registry = new BotRegistry(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
            Panel = new TelegramEndpointAdminService(Backend, Configuration, registry, options ?? new TelegramEndpointRoutingOptions(),
                NullLogger<TelegramEndpointAdminService>.Instance, timeProvider: Clock, liveConfiguration: liveConfiguration, configurationSource: source);
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

        /// <summary>Visits every technical page from the actual simple-screen button and keeps the combined diagnostics for behavioral assertions.</summary>
        /// <returns>Complete read-only technical output, including history beyond the first message page.</returns>
        /// <remarks>Never invokes a health-refresh control or registers migration intents.</remarks>
        public async Task<string> ReadTechnicalAsync()
        {
            await TapAsync(Control, Button(Control, "🔍 جزئیات فنی"));
            var text = new StringBuilder();
            while (true)
            {
                Assert.NotNull(Control.Text);
                Assert.InRange(Control.Text.Length, 1, 3900);
                text.AppendLine(Control.Text);
                var next = Buttons(Control).SingleOrDefault(x => x.Text == "بخش فنی بعد ▶️");
                if (next == null) break;
                await TapAsync(Control, next.CallbackData!);
            }
            return text.ToString();
        }
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
        /// <summary>Frozen calls observed by the fake bulk boundary, including hosting identity.</summary>
        public List<(string Host, long Identity, TelegramEndpointType Target, IReadOnlyList<TelegramEndpointBulkTarget> Targets)> Batches { get; } = new();
        /// <summary>Optional per-bot registration outcomes for partial, cancelled and safety-refusal scenarios.</summary>
        public Dictionary<string, string> BulkCodes { get; } = new(StringComparer.Ordinal);
        /// <summary>Assertion at the bulk boundary, before the hosting route may close.</summary>
        public Action? BeforeBulk { get; set; }
        /// <summary>Simulates an unreadable registration response; the UI must retain uncertainty and never retry the call.</summary>
        public bool ThrowBulk { get; set; }
        /// <summary>Health probe requests; technical navigation must leave this count unchanged.</summary>
        public int HealthRefreshes { get; private set; }
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
            States[botId].OperationId = Guid.NewGuid().ToString("N");
            States[botId].MigrationStartedAtUtc = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
            States[botId].DesiredEndpoint = target;
            States[botId].MigrationState = target == TelegramEndpointType.Local ? TelegramEndpointMigrationState.CheckingLocal : TelegramEndpointMigrationState.FallbackPending;
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
        public Task RefreshHealthAsync(string botId, CancellationToken cancellationToken)
        {
            HealthRefreshes++;
            return Task.CompletedTask;
        }
        /// <inheritdoc />
        public Task<IReadOnlyList<TelegramEndpointHistory>> GetHistoryAsync(string botId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<TelegramEndpointHistory>>(History.ToArray());

        /// <inheritdoc />
        public Task<IReadOnlyList<TelegramEndpointBulkResult>> RequestBulkMigrationAsync(string hostingBotId, long hostingIdentity,
            TelegramEndpointType target, long actor, IReadOnlyList<TelegramEndpointBulkTarget> targets, CancellationToken cancellationToken)
        {
            BeforeBulk?.Invoke();
            Batches.Add((hostingBotId, hostingIdentity, target, targets.ToArray()));
            if (ThrowBulk) throw new OperationCanceledException();
            var results = targets.Select(snapshot =>
            {
                var state = States.GetValueOrDefault(snapshot.BotId);
                var code = state == null || state.TelegramBotId != snapshot.Identity || state.ControlRevision != snapshot.Revision
                    ? "stale" : BulkCodes.GetValueOrDefault(snapshot.BotId, "accepted");
                if (code == "accepted")
                {
                    state!.ControlRevision++;
                    state.OperationId = Guid.NewGuid().ToString("N");
                    state.MigrationStartedAtUtc = new DateTime(2026, 10, 9, 0, 0, 0, DateTimeKind.Utc);
                    state.DesiredEndpoint = target;
                    state.MigrationState = target == TelegramEndpointType.Local ? TelegramEndpointMigrationState.CheckingLocal : TelegramEndpointMigrationState.FallbackPending;
                }
                return new TelegramEndpointBulkResult(snapshot.BotId, snapshot.Identity, code,
                    code is "accepted" or "unchanged" ? state?.OperationId : null);
            }).ToArray();
            return Task.FromResult<IReadOnlyList<TelegramEndpointBulkResult>>(results);
        }

        /// <summary>Adds exact-operation committed activation evidence used by the real presentation path.</summary>
        /// <param name="botId">Synthetic internal identity whose destination activation has been committed.</param>
        public void AddActivation(string botId)
        {
            var state = States[botId];
            History.Add(new TelegramEndpointHistory { BotId = botId, TelegramBotId = state.TelegramBotId, OperationId = state.OperationId,
                Reason = "migration_succeeded", ToEffectiveEndpoint = state.EffectiveEndpoint, CreatedAtUtc = state.LastMigrationAtUtc!.Value });
        }
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
