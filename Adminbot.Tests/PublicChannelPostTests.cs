using Adminbot.Domain;
using Adminbot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Text;
using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using System.Collections;
using System.Reflection;
using Telegram.Bot.Types.Enums;
using Xunit;
using TelegramMessage = Telegram.Bot.Types.Message;
using TelegramUser = Telegram.Bot.Types.User;

/// <summary>Exercises public-channel composition through real SDK serialization, isolated production services and captured uploads.</summary>
public sealed partial class ConcurrencyTests
{
    /// <summary>The consent migration leaves historical financial evidence and bot-scoped conversation state byte-for-byte equivalent as detached JSON.</summary>
    /// <returns>A task after the real historical migration upgrades an existing store/order/workflow fixture.</returns>
    [Fact]
    public async Task Public_channel_posts_preference_upgrade_preserves_order_and_conversation_evidence()
    {
        using var databases = new Databases(initialize: false);
        await using var db = databases.Users.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync("20261001120000_AddColleagueTrialGrants");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO BotInstances (Id, Type, OwnerTelegramUserId, Enabled, IsDefault, TenantPriceMarkupPercent,
                TenantMandatoryJoinEnabled, TenantChannelIdsJson, TenantCardPaymentEnabled, TenantHooshPayEnabled,
                TenantNowPaymentsEnabled, CreatedAtUtc)
            VALUES ('historical-public-post', 'tenant', 711, 1, 0, 31, 0, '["@historical_channel"]', 1, 0, 0, '2025-01-01 00:00:00');
            """);
        var order = new TenantBotOrder
        {
            OrderId = "historical-public-post-order", TenantBotId = "historical-public-post",
            OwnerTelegramUserId = 711, CustomerTelegramUserId = 9001, CustomerChatId = 9001,
            SalePriceToman = 130000, BaseCostToman = 100000, ProfitToman = 30000,
            PaymentProvider = "card", PaymentStatus = TenantBotOrderStatuses.AwaitingReceipt,
            OwnerWalletDelta = 0, OwnerBalanceBefore = 500000, OwnerBalanceAfter = 500000,
            ServiceKey = "historical-service", UserComment = "اطلاعات سفارش 🌷"
        };
        db.TenantBotOrders.Add(order);
        db.BotUserStates.Add(new()
        {
            BotId = "historical-public-post", TelegramUserId = 9001, LastStep = "awaiting-receipt",
            Email = "historical@example.invalid", Flow = "renew", OwnerStoreId = "historical-public-post",
            _ConfigPrice = "130000", RenewalSessionId = "historical-session"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var originalOrder = JsonSerializer.Serialize(await db.TenantBotOrders.AsNoTracking().SingleAsync(x => x.OrderId == order.OrderId));
        var originalState = JsonSerializer.Serialize(await db.BotUserStates.AsNoTracking().SingleAsync(x => x.BotId == "historical-public-post" && x.TelegramUserId == 9001));
        await db.Database.MigrateAsync();
        Assert.Equal(originalOrder, JsonSerializer.Serialize(await db.TenantBotOrders.AsNoTracking().SingleAsync(x => x.OrderId == order.OrderId)));
        Assert.Equal(originalState, JsonSerializer.Serialize(await db.BotUserStates.AsNoTracking().SingleAsync(x => x.BotId == "historical-public-post" && x.TelegramUserId == 9001)));
        var store = await db.BotInstances.AsNoTracking().SingleAsync(x => x.Id == "historical-public-post");
        Assert.True(store.TenantPublicChannelPostsEnabled);
        Assert.Equal(711, store.OwnerTelegramUserId);
        Assert.Equal("[\"@historical_channel\"]", store.TenantChannelIdsJson);
        Assert.True(store.Enabled);
        Assert.False(store.TenantMandatoryJoinEnabled);
    }

    /// <summary>Actual manager admission requires exact source authority and a successfully visible revision-bound preview.</summary>
    /// <returns>A task after authorization, preserved content and one-way publication are checked.</returns>
    [Fact]
    public async Task Public_channel_posts_manager_authority_preview_and_duplicate_confirmation()
    {
        await using var f = await PublicPostFixture.CreateAsync();
        Assert.Null(f.Manager.StartDraft(new("main", 999, 999)));
        Assert.Null(f.Manager.StartDraft(new("main", 711, -711)));
        Assert.Null(f.Manager.StartDraft(new("missing", 711, 711)));
        Assert.Null(f.Manager.StartDraft(new("assistant", 711, 711)));
        var draft = f.Begin();
        Assert.False(f.Manager.QueuePublish(f.Key, draft.Id, draft.Revision, draft.ControlMessageId).Accepted);
        draft = f.Add(PublicPostFixture.Text(1, "سلام <>& 🌷"));
        Assert.False(f.Manager.QueuePreview(f.Key, draft.Id, draft.Revision, 999, 0).Accepted);
        await f.PreviewAsync();
        Assert.Empty(f.Http.ChannelPosts);
        draft = f.Manager.GetDraft(f.Key)!;
        Assert.Equal(PublicChannelPostPhase.PreviewReady, draft.Phase);
        var admitted = f.Manager.QueuePublish(f.Key, draft.Id, draft.Revision, draft.ControlMessageId);
        Assert.True(admitted.Accepted);
        Assert.False(f.Manager.QueuePublish(f.Key, draft.Id, draft.Revision, draft.ControlMessageId).Accepted);
        Assert.False(f.Manager.AddMessage(f.Key, PublicPostFixture.Text(2, "replacement")).Accepted);
        f.Manager.CancelDraft("main", 711);
        await f.CompletedAsync();
        var sent = Assert.Single(f.Http.ChannelPosts);
        Assert.Equal(-1001, sent.ChatId);
        Assert.Equal(10001, sent.BotId);
        Assert.Equal("سلام <>& 🌷\n\n🤖 ربات: @bot10001\n📣 کانال: @channel1001", sent.Body);
        Assert.All(f.Http.ChannelPosts, x => Assert.True(x.ChatId < 0));
        sent.AssertFooter();
    }

    /// <summary>Fresh dispatch scopes collect separate album updates and consume only callbacks from the recorded control.</summary>
    /// <returns>A task after real private preview, forged actions and duplicate confirmation have been exercised.</returns>
    [Fact]
    public async Task Public_channel_posts_dispatch_album_and_forged_callbacks()
    {
        await using var f = await PublicPostFixture.CreateAsync();
        await f.DispatchAsync(PublicPostFixture.Text(1, "📣 پست عمومی کانال‌ها"));
        await f.DispatchAsync(PublicPostFixture.Photo(30, "three", "album"));
        await f.DispatchAsync(PublicPostFixture.Photo(10, "one", "album"));
        await f.DispatchAsync(PublicPostFixture.Photo(20, "two", "album"));
        await f.DispatchAsync(PublicPostFixture.Text(40, "کپشن مشترک 🌷"));
        Assert.Equal(3, f.Manager.GetDraft(f.Key)!.PhotoCount);
        await f.CallbackAsync("preview");
        await f.ReadyAsync();
        Assert.Empty(f.Http.ChannelPosts);
        var preview = f.Http.Requests.Last(x => x.Method == "sendMediaGroup" && x.ChatId == 711);
        Assert.Equal(new[] { "one", "two", "three" }, preview.MediaIds);
        var draft = f.Manager.GetDraft(f.Key)!;
        await f.CallbackAsync("send", actor: 999);
        await f.CallbackAsync("send", controlOverride: draft.ControlMessageId + 1);
        await f.CallbackAsync("send", chatId: -711);
        f.AddOwned("forged-source", 10003, "@channel1002");
        await f.CallbackAsync("send", botId: "forged-source");
        await f.CallbackAsync("send", dataOverride: "cpp:invalid:0:send");
        await f.CallbackAsync("send", dataOverride: $"cpp:{draft.Id}:0:send");
        foreach (var malformed in new[] { $"cpp:{draft.Id}:XYZ:send", $"cpp:{draft.Id}:{draft.Revision:X}:unknown", $"cpp:{draft.Id}:{draft.Revision:X}:pFFFFFFFFFFFFFFFF" })
            await f.CallbackAsync("send", dataOverride: malformed);
        Assert.Equal(draft, f.Manager.GetDraft(f.Key));
        Assert.Empty(f.Http.ChannelPosts);
        await f.CallbackAsync("send");
        await f.CallbackAsync("send");
        await f.CompletedAsync();
        var post = Assert.Single(f.Http.ChannelPosts);
        Assert.Equal("sendMediaGroup", post.Method);
        Assert.Equal(new[] { PublicPostHttp.Image("one"), PublicPostHttp.Image("two"), PublicPostHttp.Image("three") }, post.Uploads);
        Assert.Single(post.Captions, x => !string.IsNullOrEmpty(x));
        post.AssertFooter();
    }

    /// <summary>Customer/colleague/Tenant contexts cannot compose, and normal privileged navigation abandons only unpublished content.</summary>
    /// <returns>A task after full fresh-scope authorization and navigation routes are checked.</returns>
    /// <remarks>The colleague-only actor is seeded with its persisted role; profile refresh APIs deliberately cannot grant roles.</remarks>
    [Fact]
    public async Task Public_channel_posts_dispatch_authority_and_navigation()
    {
        await using var f = await PublicPostFixture.CreateAsync();
        await using (var db = f.Provider.GetRequiredService<CredentialsDbContextFactory>().CreateDbContext())
        {
            db.Users.Add(new CredUser { TelegramUserId = 999, IsColleague = true });
            await db.SaveChangesAsync();
        }
        foreach (var actor in new[] { 998L, 999L })
        {
            var input = PublicPostFixture.Text((int)actor, "📣 پست عمومی کانال‌ها");
            input.From!.Id = actor;
            input.Chat.Id = actor;
            await f.DispatchAsync(input);
            Assert.Null(f.Manager.GetDraft(new("main", actor, actor)));
        }
        await f.AddTenantAsync("tenant-source", 20001, 711, "@channel1007");
        await f.DispatchAsync(PublicPostFixture.Text(100, "📣 پست عمومی کانال‌ها"), "tenant-source");
        Assert.Null(f.Manager.GetDraft(new("tenant-source", 711, 711)));
        foreach (var navigation in new[] { "/start", "/refresh", "🗽 Admin", "📑 Menu", "❌ لغو", "⚙️ مدیریت درگاه‌ها" })
        {
            await f.DispatchAsync(PublicPostFixture.Text(f.Http.NextId(), "📣 پست عمومی کانال‌ها"));
            Assert.NotNull(f.Manager.GetDraft(f.Key));
            await f.DispatchAsync(PublicPostFixture.Text(f.Http.NextId(), navigation));
            Assert.Null(f.Manager.GetDraft(f.Key));
        }
        Assert.Empty(f.Http.ChannelPosts);
    }

    /// <summary>Real cpp destination navigation and edit/cancel controls disarm stale confirmation while preserving the photo.</summary>
    /// <returns>A task after private one-photo previews render both footers and no stale button causes publication.</returns>
    [Fact]
    public async Task Public_channel_posts_dispatch_preview_navigation_edit_and_cancel()
    {
        await using var f = await PublicPostFixture.CreateAsync();
        f.AddOwned("owned-b", 10003, "@channel1002");
        await f.DispatchAsync(PublicPostFixture.Text(1, "📣 پست عمومی کانال‌ها"));
        await f.DispatchAsync(PublicPostFixture.Photo(2, "navigation", caption: "پیش‌نمایش 🌷"));
        await f.CallbackAsync("preview");
        await f.ReadyAsync();
        var oldSend = f.Http.Requests.SelectMany(x => x.Callbacks).Last(x => x.EndsWith(":send", StringComparison.Ordinal));
        var previous = f.Manager.GetDraft(f.Key)!;
        await f.CallbackAsync("p1");
        await f.ReadyAsync();
        var selected = f.Manager.GetDraft(f.Key)!;
        Assert.True(selected.Revision > previous.Revision);
        Assert.Equal(previous.ControlMessageId, selected.ControlMessageId);
        var privatePreview = f.Http.Requests.Last(x => x.Method == "sendPhoto" && x.ChatId == 711);
        Assert.Contains("@bot10003", privatePreview.Body);
        Assert.Contains("@channel1002", privatePreview.Body);
        await f.CallbackAsync("send", dataOverride: oldSend);
        Assert.Equal(selected, f.Manager.GetDraft(f.Key));
        await f.CallbackAsync("edit");
        Assert.Equal(PublicChannelPostPhase.Editing, f.Manager.GetDraft(f.Key)!.Phase);
        Assert.Equal(1, f.Manager.GetDraft(f.Key)!.PhotoCount);
        await f.DispatchAsync(PublicPostFixture.Text(3, "کپشن جایگزین"));
        await f.CallbackAsync("send", dataOverride: oldSend);
        await f.CallbackAsync("preview");
        await f.ReadyAsync();
        await f.CallbackAsync("cancel");
        Assert.Null(f.Manager.GetDraft(f.Key));
        Assert.Empty(f.Http.ChannelPosts);
        Assert.Equal(1, f.Http.DownloadCount);
    }

    /// <summary>Durable duplicate inbox updates execute the publication confirmation once through the real scheduler and fresh executor scopes.</summary>
    /// <returns>A task after one terminal inbox record and one channel delivery are observed.</returns>
    [Fact]
    public async Task Public_channel_posts_duplicate_inbox_confirmation_is_one_job()
    {
        await using var f = await PublicPostFixture.CreateAsync();
        await f.DispatchAsync(PublicPostFixture.Text(1, "📣 پست عمومی کانال‌ها"));
        await f.DispatchAsync(PublicPostFixture.Text(2, "durable confirmation"));
        await f.CallbackAsync("preview");
        await f.ReadyAsync();
        var d = f.Manager.GetDraft(f.Key)!;
        var data = f.Http.Requests.SelectMany(x => x.Callbacks).Last(x => x.EndsWith(":send", StringComparison.Ordinal));
        var update = new Update { Id = 99001, CallbackQuery = new CallbackQuery { Id = "duplicate-inbox", From = new TelegramUser { Id = 711, FirstName = "admin" }, Data = data, Message = new TelegramMessage { Id = d.ControlMessageId, Chat = new Chat { Id = 711, Type = ChatType.Private } } } };
        var scheduler = f.Provider.GetRequiredService<TelegramUpdateScheduler>();
        await scheduler.StartAsync(CancellationToken.None);
        try
        {
            await scheduler.EnqueueAsync("main", update, CancellationToken.None);
            await scheduler.EnqueueAsync("main", update, CancellationToken.None);
            await f.CompletedAsync();
            await using var db = f.Databases.Users.CreateDbContext();
            Assert.Equal(1, await db.TelegramUpdateInbox.CountAsync(x => x.UpdateId == 99001));
            Assert.Single(f.Http.ChannelPosts);
        }
        finally { await scheduler.StopAsync(CancellationToken.None); }
    }

    /// <summary>One, two and ten photo posts use the exact Telegram representation and transfer retained source bytes only once.</summary>
    /// <param name="count">Accepted number of photo occurrences, from one to ten.</param>
    /// <returns>A task after preview navigation, caption-only edit and destination uploads are checked.</returns>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(10)]
    public async Task Public_channel_posts_media_counts_cache_entities_and_uploads(int count)
    {
        await using var f = await PublicPostFixture.CreateAsync();
        f.AddOwned("owned-b", 10003, "@channel1002");
        f.Begin();
        for (var i = 0; i < count; i++) f.Add(PublicPostFixture.Photo(i + 1, "image" + i));
        var text = "فارسی 🌷 <>& لینک \n  ";
        var message = PublicPostFixture.Text(30, text);
        message.Entities = new[]
        {
            new MessageEntity { Type = MessageEntityType.Bold, Offset = 0, Length = 5 },
            new MessageEntity { Type = MessageEntityType.TextLink, Offset = text.IndexOf("لینک", StringComparison.Ordinal), Length = 4, Url = "https://example.org/original" }
        };
        f.Add(message);
        var before = f.Manager.GetDraft(f.Key)!;
        if (count == 10)
        {
            Assert.False(f.Manager.AddMessage(f.Key, PublicPostFixture.Photo(99, "overflow")).Accepted);
            Assert.Equal(before, f.Manager.GetDraft(f.Key));
        }
        await f.PreviewAsync();
        await f.PreviewAsync(1);
        var ready = f.Manager.GetDraft(f.Key)!;
        Assert.True(f.Manager.ResumeEditing(f.Key, ready.Id, ready.Revision, ready.ControlMessageId).Accepted);
        message.Id = 31;
        f.Add(message);
        await f.PreviewAsync();
        Assert.Equal(count, f.Http.Requests.Count(x => x.Method == "getFile"));
        Assert.Equal(count, f.Http.DownloadCount);
        f.Publish();
        await f.CompletedAsync();
        Assert.Equal(2, f.Http.ChannelPosts.Count);
        foreach (var post in f.Http.ChannelPosts)
        {
            Assert.Equal(count == 1 ? "sendPhoto" : "sendMediaGroup", post.Method);
            Assert.Equal(Enumerable.Range(0, count).Select(i => PublicPostHttp.Image("image" + i)).ToArray(), post.Uploads);
            Assert.StartsWith(text, post.Body);
            Assert.Equal("bold", post.Entities[0].GetProperty("type").GetString());
            Assert.Equal(0, post.Entities[0].GetProperty("offset").GetInt32());
            Assert.Contains(post.Entities, x => x.TryGetProperty("url", out var url) && url.GetString() == "https://example.org/original");
            post.AssertFooter();
        }
    }

    /// <summary>Intake conflicts and stale work never authorize changed content, while resume/cancel preserve their documented boundary.</summary>
    /// <returns>A task after unsupported media, duplicate intake, conflicting captions and superseded preview are checked.</returns>
    [Fact]
    public async Task Public_channel_posts_late_media_caption_conflict_edit_and_cancel()
    {
        await using var f = await PublicPostFixture.CreateAsync();
        var draft = f.Begin();
        Assert.False(f.Manager.QueuePreview(f.Key, draft.Id, draft.Revision, draft.ControlMessageId, 0).Accepted);
        var first = PublicPostFixture.Photo(1, "first", caption: "caption A");
        f.Add(first);
        var revision = f.Manager.GetDraft(f.Key)!.Revision;
        f.Manager.AddMessage(f.Key, first);
        Assert.Equal(1, f.Manager.GetDraft(f.Key)!.PhotoCount);
        Assert.Equal(revision, f.Manager.GetDraft(f.Key)!.Revision);
        f.Add(PublicPostFixture.Photo(2, "second", caption: "caption B"));
        Assert.True(f.Manager.GetDraft(f.Key)!.HasCaptionConflict);
        draft = f.Manager.GetDraft(f.Key)!;
        Assert.False(f.Manager.QueuePreview(f.Key, draft.Id, draft.Revision, draft.ControlMessageId, 0).Accepted);
        var unsupported = f.Manager.AddMessage(f.Key, new TelegramMessage { Id = 3, From = new TelegramUser { Id = 711, FirstName = "admin" }, Chat = new Chat { Id = 711, Type = ChatType.Private }, Sticker = new Sticker { FileId = "sticker", FileUniqueId = "unique", Width = 1, Height = 1 } });
        Assert.False(unsupported.Accepted);
        Assert.Equal("unsupported_message", unsupported.ReasonCode);
        Assert.Equal(draft, f.Manager.GetDraft(f.Key));
        foreach (var kind in new[] { "video", "document" })
        {
            var input = PublicPostFixture.Text(60, "");
            input.Text = null;
            if (kind == "video") input.Video = new Video { FileId = "video", FileUniqueId = "video", Width = 1, Height = 1, Duration = 1 };
            else input.Document = new Document { FileId = "document", FileUniqueId = "document" };
            var rejected = f.Manager.AddMessage(f.Key, input);
            Assert.False(rejected.Accepted);
            Assert.Equal("unsupported_message", rejected.ReasonCode);
            Assert.Equal(draft, f.Manager.GetDraft(f.Key));
        }
        f.Add(PublicPostFixture.Text(4, "resolved"));
        var gate = f.Http.HoldNext("getFile");
        draft = f.Manager.GetDraft(f.Key)!;
        Assert.True(f.Manager.QueuePreview(f.Key, draft.Id, draft.Revision, draft.ControlMessageId, 0).Accepted);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Add(PublicPostFixture.Photo(5, "late"));
        gate.Release.TrySetResult();
        await f.PreviewAsync();
        var ready = f.Manager.GetDraft(f.Key)!;
        Assert.Equal(3, ready.PhotoCount);
        Assert.False(f.Manager.QueuePublish(f.Key, ready.Id, draft.Revision, ready.ControlMessageId).Accepted);
        Assert.True(f.Manager.ResumeEditing(f.Key, ready.Id, ready.Revision, ready.ControlMessageId).Accepted);
        Assert.Equal(3, f.Manager.GetDraft(f.Key)!.PhotoCount);
        Assert.False(f.Manager.QueuePublish(f.Key, ready.Id, ready.Revision, ready.ControlMessageId).Accepted);
        var withAlbum = f.Add(PublicPostFixture.Photo(6, "one-album", "album-a"));
        var secondAlbum = f.Manager.AddMessage(f.Key, PublicPostFixture.Photo(7, "another-album", "album-b"));
        Assert.False(secondAlbum.Accepted);
        Assert.Equal("multiple_albums", secondAlbum.ReasonCode);
        Assert.Equal(withAlbum, f.Manager.GetDraft(f.Key));
        f.Manager.CancelDraft("main", 711);
        Assert.Null(f.Manager.GetDraft(f.Key));
        Assert.Empty(f.Http.ChannelPosts);
    }

    /// <summary>Footer-inclusive UTF-16 limits block the complete preview rather than truncate content or silently drop a target.</summary>
    /// <param name="photo">True tests the 1024-unit caption limit; false tests the 4096-unit text limit.</param>
    /// <returns>A task after exact limit acceptance and one-unit overflow rejection.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_channel_posts_footer_inclusive_limits(bool photo)
    {
        await using var f = await PublicPostFixture.CreateAsync();
        f.Begin();
        if (photo) f.Add(PublicPostFixture.Photo(1, "limit"));
        const string footer = "\n\n🤖 ربات: @bot10001\n📣 کانال: @channel1001";
        var body = new string('س', (photo ? 1024 : 4096) - footer.Length);
        f.Add(PublicPostFixture.Text(2, body));
        await f.PreviewAsync();
        var draft = f.Manager.GetDraft(f.Key)!;
        Assert.True(f.Manager.ResumeEditing(f.Key, draft.Id, draft.Revision, draft.ControlMessageId).Accepted);
        f.Add(PublicPostFixture.Text(3, body + "س"));
        draft = f.Manager.GetDraft(f.Key)!;
        Assert.True(f.Manager.QueuePreview(f.Key, draft.Id, draft.Revision, draft.ControlMessageId, 0).Accepted);
        await f.WaitAsync(() => f.Manager.GetDraft(f.Key)?.Phase == PublicChannelPostPhase.Editing);
        draft = f.Manager.GetDraft(f.Key)!;
        Assert.False(f.Manager.QueuePublish(f.Key, draft.Id, draft.Revision, draft.ControlMessageId).Accepted);
        Assert.Empty(f.Http.ChannelPosts);
        f.Add(PublicPostFixture.Text(4, body));
        await f.PreviewAsync();
        f.Publish();
        await f.CompletedAsync();
        Assert.Equal(photo ? 1024 : 4096, Assert.Single(f.Http.ChannelPosts).Body.Length);
    }

    /// <summary>Prepared inventory preserves per-bot associations, resolves numeric/alias settings and rechecks persisted opt-out before delivery.</summary>
    /// <returns>A task after exact target identity, excluded targets and live consent changes are checked.</returns>
    [Fact]
    public async Task Public_channel_posts_destination_inventory_and_late_owner_opt_out()
    {
        await using var f = await PublicPostFixture.CreateAsync();
        f.AddOwned("main", 10001, "@channel1001", "channel1001", "https://t.me/channel1001", "-1001", "@private1003", "@group1004", "@denied1005", "https://t.me/+invite", "https://evil.example/channel1001", "https://t.me/channel1001/3");
        f.AddOwned("owned-b", 10003, "@channel1001", "-1002");
        f.AddOwned("disabled", 10004, "@channel1006", enabled: false);
        f.Registry.Upsert(new BotInstance { Id = "tokenless", Type = BotInstanceTypes.Owned, Enabled = true, ChannelIdsJson = "[\"@channel1006\"]" });
        f.Registry.Bots.Single(x => x.Id == "assistant").ChannelIds = new List<string> { "@channel1014" };
        f.Registry.Upsert(new BotInstance { Id = "runtime-only-tenant", Type = BotInstanceTypes.Tenant, Enabled = true, Token = Token(20006), TenantChannelIdsJson = "[\"@channel1015\"]" });
        await f.AddTenantAsync("tenant-a", 20001, 711, "@channel1007");
        await f.AddTenantAsync("tenant-b", 20002, 711, "@channel1008");
        await f.AddTenantAsync("tenant-c", 20003, 712, "@channel1009");
        await f.AddTenantAsync("opted-out", 20004, 712, "@channel1010", consent: false);
        await f.AddTenantAsync("absent-runtime", 20005, 712, "@channel1011", runtime: false);
        await using (var db = f.Databases.Users.CreateDbContext())
        {
            var tenantA = await db.BotInstances.SingleAsync(x => x.Id == "tenant-a");
            tenantA.TenantChannelIdsJson = "[\"@channel1007\",\"https://telegram.me/channel1013\"]";
            db.BotInstances.Add(new BotInstance { Id = "main", Type = BotInstanceTypes.Owned, Enabled = true, Token = Token(10007), ChannelIdsJson = "[\"@channel1016\"]" });
            db.Users.AddRange(new global::User { Id = 9001 }, new global::User { Id = 9002 });
            db.BotUserStates.Add(new() { BotId = "main", TelegramUserId = 9001 });
            db.BotUserStates.Add(new() { BotId = "tenant-a", TelegramUserId = 9002 });
            await db.SaveChangesAsync();
        }
        f.Begin();
        f.Add(PublicPostFixture.Text(1, "inventory"));
        await f.PreviewAsync();
        var gate = f.Http.HoldNext("sendMessage", channelsOnly: true);
        f.Publish();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var db = f.Databases.Users.CreateDbContext())
        {
            var row = await db.BotInstances.SingleAsync(x => x.Id == "tenant-b");
            row.TenantPublicChannelPostsEnabled = false;
            await db.SaveChangesAsync();
        }
        gate.Release.TrySetResult();
        await f.CompletedAsync();
        Assert.Equal(new[] { (10001L, -1001L), (10003L, -1002L), (10003L, -1001L), (20001L, -1007L), (20001L, -1013L), (20003L, -1009L) }.OrderBy(x => x),
            f.Http.ChannelPosts.Select(x => (x.BotId, x.ChatId)).OrderBy(x => x));
        Assert.All(f.Http.ChannelPosts, x => x.AssertFooter());
        Assert.DoesNotContain(f.Http.ChannelPosts, x => x.BotId == 20002 || x.BotId == 10002 || x.ChatId > 0);
        Assert.DoesNotContain(f.Http.Requests, x => x.Method.StartsWith("send", StringComparison.Ordinal) && x.ChatId > 0 && x.ChatId != 711);
        var progress = f.Progress();
        Assert.Equal(7, progress.EligibleTotal);
        Assert.Equal(7, progress.Processed);
        Assert.Equal(6, progress.Sent);
        Assert.Equal(1, progress.SkippedAfterPreparation);
        Assert.True(progress.SkippedDuringPreparation > 0);
        Assert.Contains(progress.Details, x => x.ReasonCode == "owner_opted_out" && x.BotId == "tenant-b");
        Assert.Contains(progress.Details, x => x.ReasonCode == "channel_has_no_public_username");
        Assert.Contains(progress.Details, x => x.ReasonCode == "bot_transport_unavailable");
        Assert.Equal(progress.Processed, progress.Sent + progress.Failed + progress.Uncertain + progress.SkippedAfterPreparation);
    }

    /// <summary>Explicit rate limiting may retry, ambiguous acceptance never retries, and a failing status edit cannot repeat delivery.</summary>
    /// <returns>A task after real SDK failures, continuing fan-out and captured-byte preservation.</returns>
    /// <remarks>The two-photo album protects retry stream reopening, exact bytes/order and first-item caption placement.</remarks>
    [Fact]
    public async Task Public_channel_posts_rate_limit_uncertain_and_status_failure()
    {
        await using var f = await PublicPostFixture.CreateAsync();
        f.AddOwned("main", 10001, "@channel1001", "@channel1002", "@channel1006", "@channel1012");
        f.Begin();
        f.Add(PublicPostFixture.Photo(1, "retry"));
        f.Add(PublicPostFixture.Photo(2, "retry-second"));
        await f.PreviewAsync();
        f.Http.RateLimitChat = -1001;
        f.Http.AmbiguousChat = -1002;
        f.Http.FailedChat = -1012;
        f.Http.FailStatus = true;
        f.Publish();
        await f.CompletedAsync();
        Assert.Equal(2, f.Http.ChannelPosts.Count(x => x.ChatId == -1001));
        Assert.Equal(1, f.Http.ChannelPosts.Count(x => x.ChatId == -1002));
        Assert.Equal(1, f.Http.ChannelPosts.Count(x => x.ChatId == -1006));
        Assert.All(f.Http.ChannelPosts, x =>
        {
            Assert.Equal("sendMediaGroup", x.Method);
            Assert.Equal(new[] { PublicPostHttp.Image("retry"), PublicPostHttp.Image("retry-second") }, x.Uploads);
            Assert.Single(x.Captions, caption => !string.IsNullOrEmpty(caption));
        });
        f.Http.FailStatus = false;
        var draft = f.Manager.GetDraft(f.Key)!;
        await f.Manager.RefreshStatusAsync(f.Key, draft.Id, f.Client, draft.ControlMessageId, CancellationToken.None);
        var progress = f.Progress();
        Assert.Equal(4, progress.EligibleTotal);
        Assert.Equal(4, progress.Processed);
        Assert.Equal(2, progress.Sent);
        Assert.Equal(1, progress.Failed);
        Assert.Equal(1, progress.Uncertain);
        Assert.Equal(0, progress.SkippedAfterPreparation);
        Assert.Contains(progress.Details, x => x.Outcome == "uncertain" && x.Channel == "@channel1002");
        Assert.Contains(f.Http.Requests.Where(x => x.ChatId == 711).Select(x => x.Body), x => x.Contains("نتیجه نامطمئن: 1", StringComparison.Ordinal));
        Assert.Equal(5, f.Http.ChannelPosts.Count);
    }

    /// <summary>A timed-out real preview never arms publication and stopping a worker never replays its memory-only job.</summary>
    /// <returns>A task after preview cancellation and a fresh registered manager remain inert.</returns>
    [Fact]
    public async Task Public_channel_posts_preview_timeout_and_shutdown_have_no_replay()
    {
        await using var f = await PublicPostFixture.CreateAsync();
        f.Begin();
        f.Add(PublicPostFixture.Photo(1, "timeout"));
        f.Http.TimeoutPreview = true;
        var draft = f.Manager.GetDraft(f.Key)!;
        Assert.True(f.Manager.QueuePreview(f.Key, draft.Id, draft.Revision, draft.ControlMessageId, 0).Accepted);
        await f.WaitAsync(() => f.Manager.GetDraft(f.Key)?.Phase == PublicChannelPostPhase.Editing);
        draft = f.Manager.GetDraft(f.Key)!;
        Assert.False(f.Manager.QueuePublish(f.Key, draft.Id, draft.Revision, draft.ControlMessageId).Accepted);
        f.Http.TimeoutPreview = false;
        await f.PreviewAsync();
        var assetDirectory = f.AssetDirectory(f.Manager.GetDraft(f.Key)!.Id);
        Assert.True(Directory.Exists(assetDirectory));
        var gate = f.Http.HoldNext("sendPhoto", channelsOnly: true);
        f.Publish();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await f.Manager.StopAsync(CancellationToken.None);
        gate.Release.TrySetResult();
        Assert.False(Directory.Exists(assetDirectory));
        using var restarted = ActivatorUtilities.CreateInstance<PublicChannelPostManager>(f.Provider);
        await restarted.StartAsync(CancellationToken.None);
        try
        {
            Assert.Null(restarted.GetDraft(f.Key));
            Assert.Single(f.Http.ChannelPosts);
        }
        finally { await restarted.StopAsync(CancellationToken.None); }
    }

    /// <summary>A full admission queue leaves the armed revision intact and allows exactly one later retry.</summary>
    /// <returns>A task after deterministic queue saturation under a held metadata probe.</returns>
    [Fact]
    public async Task Public_channel_posts_manager_queue_full_preserves_confirmation()
    {
        await using var f = await PublicPostFixture.CreateAsync();
        f.Begin();
        f.Add(PublicPostFixture.Text(1, "confirmed"));
        await f.PreviewAsync();
        var confirmed = f.Manager.GetDraft(f.Key)!;
        var blockingKey = new PublicChannelPostKey("main", 712, 712);
        var queuedKey = new PublicChannelPostKey("main", 713, 713);
        var blocking = f.BeginFor(blockingKey);
        var queued = f.BeginFor(queuedKey);
        var gate = f.Http.HoldNext("getMe");
        Assert.True(f.Manager.QueuePreview(blockingKey, blocking.Id, blocking.Revision, blocking.ControlMessageId, 0).Accepted);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        for (var i = 0; i < 100; i++)
        {
            queued = f.Manager.GetDraft(queuedKey)!;
            Assert.True(f.Manager.QueuePreview(queuedKey, queued.Id, queued.Revision, queued.ControlMessageId, 0).Accepted);
        }
        var full = f.Manager.QueuePublish(f.Key, confirmed.Id, confirmed.Revision, confirmed.ControlMessageId);
        Assert.False(full.Accepted);
        Assert.Equal("queue_full", full.ReasonCode);
        Assert.Equal(confirmed, f.Manager.GetDraft(f.Key));
        gate.Release.TrySetResult();
        await f.WaitAsync(() => f.Manager.GetDraft(queuedKey)?.Phase == PublicChannelPostPhase.PreviewReady);
        Assert.True(f.Manager.QueuePublish(f.Key, confirmed.Id, confirmed.Revision, confirmed.ControlMessageId).Accepted);
        Assert.False(f.Manager.QueuePublish(f.Key, confirmed.Id, confirmed.Revision, confirmed.ControlMessageId).Accepted);
        await f.CompletedAsync();
        Assert.Single(f.Http.ChannelPosts);
    }

    /// <summary>Expired IDs cannot publish or refresh a replacement composition, and retained jobs retain their exact status binding.</summary>
    /// <returns>A task after forcing only in-memory deadlines without sleeping or changing production clocks.</returns>
    [Fact]
    public async Task Public_channel_posts_manager_expiry_and_replaced_job_binding()
    {
        await using var f = await PublicPostFixture.CreateAsync();
        var expired = f.Begin();
        f.Add(PublicPostFixture.Text(1, "expires"));
        await f.PreviewAsync();
        expired = f.Manager.GetDraft(f.Key)!;
        f.Expire(expired.Id);
        Assert.Null(f.Manager.GetDraft(f.Key));
        Assert.False(f.Manager.QueuePublish(f.Key, expired.Id, expired.Revision, expired.ControlMessageId).Accepted);
        f.Begin();
        f.Add(PublicPostFixture.Text(2, "frozen"));
        await f.PreviewAsync();
        var admitted = f.Manager.GetDraft(f.Key)!;
        var gate = f.Http.HoldNext("sendMessage", channelsOnly: true);
        f.Publish();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var replacement = f.Begin();
        f.Add(PublicPostFixture.Text(3, "new composition"));
        Assert.True(f.Manager.IsStatusBindingCurrent(f.Key, admitted.Id, admitted.Revision, admitted.ControlMessageId));
        Assert.False(f.Manager.IsStatusBindingCurrent(f.Key, admitted.Id, admitted.Revision, admitted.ControlMessageId + 1));
        Assert.False(f.Manager.QueuePublish(f.Key, admitted.Id, admitted.Revision, admitted.ControlMessageId).Accepted);
        gate.Release.TrySetResult();
        await f.WaitAsync(() => f.JobPhase(admitted.Id) == PublicChannelPostPhase.Completed && f.JobOperations(admitted.Id) == 0);
        Assert.StartsWith("frozen", Assert.Single(f.Http.ChannelPosts).Body);
        Assert.Equal(replacement.Id, f.Manager.GetDraft(f.Key)!.Id);
        await f.Manager.RefreshStatusAsync(f.Key, admitted.Id, f.Client, admitted.ControlMessageId, CancellationToken.None);
        f.Expire(admitted.Id);
        Assert.False(f.Manager.IsStatusBindingCurrent(f.Key, admitted.Id, admitted.Revision, admitted.ControlMessageId));
    }

    /// <summary>Private file download errors and declared oversize files preserve editable photos without text-only fallback.</summary>
    /// <returns>A task after rejected file preparation leaves no publication permission.</returns>
    [Fact]
    public async Task Public_channel_posts_media_download_failure_and_size_limit()
    {
        await using var f = await PublicPostFixture.CreateAsync();
        f.Begin();
        var huge = PublicPostFixture.Photo(1, "huge");
        huge.Photo![1].FileSize = 10 * 1024 * 1024 + 1;
        Assert.False(f.Manager.AddMessage(f.Key, huge).Accepted);
        Assert.Equal(0, f.Manager.GetDraft(f.Key)!.PhotoCount);
        f.Add(PublicPostFixture.Photo(2, "broken"));
        f.Http.FailDownload = true;
        var d = f.Manager.GetDraft(f.Key)!;
        Assert.True(f.Manager.QueuePreview(f.Key, d.Id, d.Revision, d.ControlMessageId, 0).Accepted);
        await f.WaitAsync(() => f.Manager.GetDraft(f.Key)?.Phase == PublicChannelPostPhase.Editing);
        d = f.Manager.GetDraft(f.Key)!;
        Assert.Equal(1, d.PhotoCount);
        Assert.False(f.Manager.QueuePublish(f.Key, d.Id, d.Revision, d.ControlMessageId).Accepted);
        Assert.Empty(f.Http.ChannelPosts);
        Assert.DoesNotContain(f.Http.Requests, x => x.ChatId == 711 && x.Method == "sendPhoto");
        f.Http.FailDownload = false;
        f.Http.OversizeDownload = true;
        f.Begin();
        f.Add(PublicPostFixture.Photo(3, "stream-too-large"));
        d = f.Manager.GetDraft(f.Key)!;
        Assert.True(f.Manager.QueuePreview(f.Key, d.Id, d.Revision, d.ControlMessageId, 0).Accepted);
        await f.WaitAsync(() => f.Manager.GetDraft(f.Key)?.Phase == PublicChannelPostPhase.Editing);
        Assert.Empty(f.Http.ChannelPosts);
        Assert.DoesNotContain(f.Http.Requests, x => x.ChatId == 711 && x.Method == "sendPhoto");
    }

    /// <summary>Repeated image occurrences are intentional while file downloads are unique, and failed control delivery cannot arm a preview.</summary>
    /// <returns>A task after identical bytes occur twice in one actual album with one download.</returns>
    [Fact]
    public async Task Public_channel_posts_repeated_photo_and_failed_control_do_not_arm()
    {
        await using var f = await PublicPostFixture.CreateAsync();
        f.Begin();
        f.Add(PublicPostFixture.Photo(1, "same", caption: "یک کپشن"));
        f.Add(PublicPostFixture.Photo(2, "same", caption: "یک کپشن"));
        Assert.False(f.Manager.GetDraft(f.Key)!.HasCaptionConflict);
        f.Http.FailStatus = true;
        var d = f.Manager.GetDraft(f.Key)!;
        Assert.True(f.Manager.QueuePreview(f.Key, d.Id, d.Revision, d.ControlMessageId, 0).Accepted);
        await f.WaitAsync(() => f.Manager.GetDraft(f.Key)?.Phase == PublicChannelPostPhase.Editing);
        d = f.Manager.GetDraft(f.Key)!;
        Assert.False(f.Manager.QueuePublish(f.Key, d.Id, d.Revision, d.ControlMessageId).Accepted);
        Assert.Empty(f.Http.ChannelPosts);
        f.Http.FailStatus = false;
        await f.PreviewAsync();
        Assert.Equal(1, f.Http.DownloadCount);
        Assert.Equal(1, f.Http.Requests.Count(x => x.Method == "getFile"));
        var assetDirectory = f.AssetDirectory(f.Manager.GetDraft(f.Key)!.Id);
        Assert.True(Directory.Exists(assetDirectory));
        f.Publish();
        await f.CompletedAsync();
        var post = Assert.Single(f.Http.ChannelPosts);
        Assert.Equal(2, post.Uploads.Count);
        Assert.All(post.Uploads, x => Assert.Equal(PublicPostHttp.Image("same"), x));
        await f.WaitAsync(() => !Directory.Exists(assetDirectory));
    }

    /// <summary>A preview with no eligible public channel cannot queue a publication even when settings are malformed or private.</summary>
    /// <returns>A task after zero-target preparation leaves an editable composition and reports exclusions.</returns>
    [Fact]
    public async Task Public_channel_posts_zero_eligible_and_invalid_channel_lists()
    {
        await using var f = await PublicPostFixture.CreateAsync();
        f.AddOwned("main", 10001, "@private1003", "@group1004", "@denied1005");
        await using (var db = f.Databases.Users.CreateDbContext())
        {
            var row = new BotInstance { Id = "invalid-json", Type = BotInstanceTypes.Tenant, Enabled = true, Token = Token(20001), TenantChannelIdsJson = "not json" };
            db.BotInstances.Add(row);
            await db.SaveChangesAsync();
            f.Registry.Upsert(row);
        }
        f.Begin();
        f.Add(PublicPostFixture.Text(1, "no targets"));
        var d = f.Manager.GetDraft(f.Key)!;
        Assert.True(f.Manager.QueuePreview(f.Key, d.Id, d.Revision, d.ControlMessageId, 0).Accepted);
        await f.WaitAsync(() => f.Manager.GetDraft(f.Key)?.Phase == PublicChannelPostPhase.Editing);
        d = f.Manager.GetDraft(f.Key)!;
        Assert.False(f.Manager.QueuePublish(f.Key, d.Id, d.Revision, d.ControlMessageId).Accepted);
        Assert.Empty(f.Http.ChannelPosts);
        Assert.DoesNotContain(f.Http.Requests.SelectMany(x => x.Callbacks), x => x.EndsWith(":send", StringComparison.Ordinal));
    }

    /// <summary>Replacing source identity invalidates bot-scoped file references; token rotation with the same identity preserves them.</summary>
    /// <returns>A task after both identity gates are checked with no destination traffic before confirmation.</returns>
    [Fact]
    public async Task Public_channel_posts_source_identity_rotation()
    {
        await using var f = await PublicPostFixture.CreateAsync();
        f.Begin();
        f.Add(PublicPostFixture.Photo(1, "identity"));
        var d = f.Manager.GetDraft(f.Key)!;
        f.Registry.Upsert(new BotInstance { Id = "main", Type = BotInstanceTypes.Owned, Enabled = true, Token = "10001:" + new string('b', 35), ChannelIdsJson = "[\"@channel1001\"]" });
        await f.PreviewAsync();
        d = f.Manager.GetDraft(f.Key)!;
        f.AddOwned("main", 10006, "@channel1001");
        Assert.False(f.Manager.QueuePublish(f.Key, d.Id, d.Revision, d.ControlMessageId).Accepted);
        Assert.False(f.Manager.QueuePreview(f.Key, d.Id, d.Revision, d.ControlMessageId, 0).Accepted);
        Assert.Empty(f.Http.ChannelPosts);
    }

    /// <summary>Every frozen destination is live-checked without substituting new identities, channels or unpreviewed footer text.</summary>
    /// <param name="change">The exact live eligibility gate changed after the real preview.</param>
    /// <returns>A task after an unaffected sibling publishes and the changed target is skipped.</returns>
    [Theory]
    [InlineData("disabled")]
    [InlineData("identity")]
    [InlineData("association")]
    [InlineData("permission")]
    [InlineData("username")]
    public async Task Public_channel_posts_live_destination_changes_skip_only_affected_target(string change)
    {
        await using var f = await PublicPostFixture.CreateAsync();
        f.AddOwned("owned-b", 10003, "@channel1002");
        f.Begin();
        f.Add(PublicPostFixture.Text(1, "frozen destination"));
        await f.PreviewAsync();
        switch (change)
        {
            case "disabled": f.AddOwned("owned-b", 10003, "@channel1002", enabled: false); break;
            case "identity": f.AddOwned("owned-b", 10004, "@channel1002"); break;
            case "association": f.AddOwned("owned-b", 10003, "@channel1006"); break;
            case "permission": f.Http.DeniedChannels[-1002] = true; break;
            case "username": f.Http.Usernames[-1002] = "changed_channel"; break;
        }
        f.Publish();
        await f.CompletedAsync();
        Assert.Equal(10001, Assert.Single(f.Http.ChannelPosts).BotId);
        Assert.Equal(2, f.Progress().EligibleTotal);
        Assert.Equal(2, f.Progress().Processed);
        Assert.Equal(1, f.Progress().SkippedAfterPreparation);
    }

    /// <summary>A 429 retry re-reads committed consent instead of repeating a now-opted-out store's request.</summary>
    /// <returns>A task after a held authoritative rejection and explicit persisted opt-out.</returns>
    [Fact]
    public async Task Public_channel_posts_rate_limit_retry_rechecks_live_opt_out()
    {
        await using var f = await PublicPostFixture.CreateAsync();
        await f.AddTenantAsync("tenant-a", 20001, 711, "@channel1007");
        f.Begin();
        f.Add(PublicPostFixture.Photo(1, "live-retry"));
        await f.PreviewAsync();
        f.Http.RateLimitChat = -1007;
        var gate = f.Http.HoldNext("sendPhoto", channelsOnly: true, chatId: -1007);
        f.Publish();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var db = f.Databases.Users.CreateDbContext())
        {
            var row = await db.BotInstances.SingleAsync(x => x.Id == "tenant-a");
            row.TenantPublicChannelPostsEnabled = false;
            await db.SaveChangesAsync();
        }
        gate.Release.TrySetResult();
        await f.CompletedAsync();
        Assert.Single(f.Http.ChannelPosts, x => x.ChatId == -1007);
        Assert.Single(f.Http.ChannelPosts, x => x.ChatId == -1001);
        Assert.Equal(PublicPostHttp.Image("live-retry"), Assert.Single(f.Http.ChannelPosts.Single(x => x.ChatId == -1007).Uploads));
        Assert.Equal(1, f.Progress().Sent);
        Assert.Equal(1, f.Progress().SkippedAfterPreparation);
        Assert.Equal(0, f.Progress().Failed);
        Assert.Equal(0, f.Progress().Uncertain);
    }

    /// <summary>One otherwise eligible destination exceeding the footer-inclusive limit blocks the entire revision.</summary>
    /// <param name="photo">True selects the caption limit; false selects the text-message limit.</param>
    /// <returns>A task after no destination and no real private content preview is sent.</returns>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Public_channel_posts_one_long_destination_blocks_all(bool photo)
    {
        await using var f = await PublicPostFixture.CreateAsync();
        f.AddOwned("owned-b", 100003, "@channel1002");
        f.Begin();
        if (photo) f.Add(PublicPostFixture.Photo(1, "global-limit"));
        const string shortFooter = "\n\n🤖 ربات: @bot10001\n📣 کانال: @channel1001";
        f.Add(PublicPostFixture.Text(2, new string('س', (photo ? 1024 : 4096) - shortFooter.Length)));
        var d = f.Manager.GetDraft(f.Key)!;
        Assert.True(f.Manager.QueuePreview(f.Key, d.Id, d.Revision, d.ControlMessageId, 0).Accepted);
        await f.WaitAsync(() => f.Manager.GetDraft(f.Key)?.Phase == PublicChannelPostPhase.Editing);
        d = f.Manager.GetDraft(f.Key)!;
        Assert.False(f.Manager.QueuePublish(f.Key, d.Id, d.Revision, d.ControlMessageId).Accepted);
        Assert.Empty(f.Http.ChannelPosts);
        Assert.DoesNotContain(f.Http.Requests, x => x.ChatId == 711 && x.Body.Contains("🤖 ربات:", StringComparison.Ordinal) && x.Method.StartsWith("send", StringComparison.Ordinal));
    }

    /// <summary>Photos with no administrator caption get only the clickable footer and retain valid album caption placement.</summary>
    /// <param name="count">One-photo versus album representation.</param>
    /// <returns>A task after exact footer-only publication and transferred photo bytes.</returns>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Public_channel_posts_captionless_photos_get_footer_only(int count)
    {
        await using var f = await PublicPostFixture.CreateAsync();
        f.Begin();
        for (var i = 0; i < count; i++) f.Add(PublicPostFixture.Photo(i + 1, "captionless" + i));
        await f.PreviewAsync();
        f.Publish();
        await f.CompletedAsync();
        var post = Assert.Single(f.Http.ChannelPosts);
        Assert.Equal("🤖 ربات: @bot10001\n📣 کانال: @channel1001", post.Body);
        Assert.Equal(count, post.Uploads.Count);
        Assert.Single(post.Captions, x => !string.IsNullOrEmpty(x));
        post.AssertFooter();
    }

    /// <summary>Large sanitized exclusion reports are split into consecutive bounded private messages without new callback pages.</summary>
    /// <returns>A task after actual SDK status refresh emits every excluded detail and no additional publication.</returns>
    [Fact]
    public async Task Public_channel_posts_progress_details_are_bounded_private_messages()
    {
        await using var f = await PublicPostFixture.CreateAsync();
        f.AddOwned("main", 10001, new[] { "@channel1001" }.Concat(Enumerable.Range(0, 200).Select(i => "https://evil.example/" + i)).ToArray());
        f.Begin();
        f.Add(PublicPostFixture.Text(1, "progress"));
        await f.PreviewAsync();
        f.Publish();
        await f.CompletedAsync();
        var d = f.Manager.GetDraft(f.Key)!;
        var before = f.Http.Requests.Count;
        await f.Manager.RefreshStatusAsync(f.Key, d.Id, f.Client, d.ControlMessageId, CancellationToken.None);
        var report = f.Http.Requests.Skip(before).Where(x => x.Method == "sendMessage" && x.ChatId == 711).ToArray();
        Assert.True(report.Length > 1);
        Assert.All(report, x => Assert.InRange(x.Body.Length, 1, 4096));
        Assert.Equal(200, string.Join("", report.Select(x => x.Body)).Split("invalid_channel_setting", StringSplitOptions.None).Length - 1);
        Assert.Equal(1, f.Progress().EligibleTotal);
        Assert.Equal(200, f.Progress().SkippedDuringPreparation);
        Assert.Single(f.Http.ChannelPosts);
    }

    /// <summary>Owns isolated production DI services, real SDK transports and only the public-post background worker.</summary>
    private sealed class PublicPostFixture : IAsyncDisposable
    {
        /// <summary>Fixture-owned databases cleared only after the worker and provider are stopped.</summary>
        public Databases Databases { get; } = new();
        /// <summary>Recorded HTTP transport; never connects to Telegram.</summary>
        public PublicPostHttp Http { get; } = new();
        /// <summary>Full registered production services.</summary>
        public ServiceProvider Provider { get; private set; } = null!;
        /// <summary>Current authoritative bot registry.</summary>
        public BotRegistry Registry { get; private set; } = null!;
        /// <summary>Actual registered state machine and worker.</summary>
        public PublicChannelPostManager Manager => Provider.GetRequiredService<PublicChannelPostManager>();
        /// <summary>Source SDK transport bound to its numeric identity.</summary>
        public ITelegramBotClient Client => Provider.GetRequiredService<BotClientProvider>().GetClient("main", 10001);
        /// <summary>Authorized private composer identity.</summary>
        public PublicChannelPostKey Key { get; } = new("main", 711, 711);
        /// <summary>Creates a production registration isolated from all live paths and starts only the manager.</summary>
        /// <returns>A fixture whose owner must asynchronously dispose it.</returns>
        public static async Task<PublicPostFixture> CreateAsync()
        {
            var f = new PublicPostFixture();
            var config = new ConfigurationBuilder().AddConfiguration(IncidentConfiguration()).AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AdminsUserIds:0"] = "711", ["AdminsUserIds:1"] = "712", ["AdminsUserIds:2"] = "713", ["bots:0:channelIds:0"] = "@channel1001",
                ["UserActivityLogEnabled"] = "false", ["BroadcastDelayMs"] = "50", ["BroadcastQueueCapacity"] = "100", ["BroadcastMaxRetryCount"] = "1",
                ["UserDatabasePath"] = Path.Combine(f.Databases.DirectoryPath, "users.db"),
                ["CredentialsDatabasePath"] = Path.Combine(f.Databases.DirectoryPath, "credentials.db"),
                ["UserActivityLogFilePath"] = Path.Combine(f.Databases.DirectoryPath, "activity.jsonl"),
                ["ErrorFileLogFilePath"] = Path.Combine(f.Databases.DirectoryPath, "errors.log")
            }).Build();
            var settings = config.Get<AppConfig>()!;
            var services = new ServiceCollection();
            Program.RegisterApplicationServices(services, config, settings, f.Databases.DirectoryPath);
            services.TryAddSingleton<PublicChannelPostManager>();
            f.Registry = new BotRegistry(config);
            services.AddSingleton(f.Registry);
            services.AddSingleton(new BotClientProvider(f.Registry, bot => new TelegramBotClient(new TelegramBotClientOptions(bot.Token) { RetryCount = 0 }, new HttpClient(f.Http, disposeHandler: false))));
            services.AddSingleton<ITelegramTokenProbe>(new OwnerIdentityProbe((token, _) => Task.FromResult(new TelegramUser { Id = long.Parse(token.Split(':')[0]), IsBot = true, FirstName = "fixture", Username = "fixture_bot" })));
            services.AddSingleton(new TelegramForegroundDeliveryPolicy { OverallBudget = TimeSpan.FromSeconds(2), MediaGroupBudget = TimeSpan.FromSeconds(2) });
            f.Provider = services.BuildServiceProvider();
            await f.Manager.StartAsync(CancellationToken.None);
            return f;
        }
        /// <summary>Starts a direct-manager composition and manually binds its real private control identity.</summary>
        /// <returns>The bound immutable draft.</returns>
        public PublicChannelPostDraftSnapshot Begin()
        {
            var d = Manager.StartDraft(Key)!;
            Manager.BindControlMessage(Key, d.Id, d.Revision, 700);
            return Manager.GetDraft(Key)!;
        }
        /// <summary>Creates another authorized composition used to saturate the queue without canceling the held probe.</summary>
        /// <param name="key">Exact configured source, sender and matching private chat.</param>
        /// <returns>A content-bearing bound draft for this separate actor.</returns>
        public PublicChannelPostDraftSnapshot BeginFor(PublicChannelPostKey key)
        {
            var d = Manager.StartDraft(key)!;
            var message = Text(1, "queued content");
            message.From!.Id = key.TelegramUserId;
            message.Chat.Id = key.ChatId;
            var result = Manager.AddMessage(key, message);
            Assert.True(result.Accepted);
            d = result.Draft!;
            Manager.BindControlMessage(key, d.Id, d.Revision, 700);
            return Manager.GetDraft(key)!;
        }
        /// <summary>Finds actual retained state solely for deadline and completion observation.</summary>
        /// <param name="id">Opaque identity returned by the real manager.</param>
        /// <returns>Manager-owned state; callers hold its existing lock before reading or mutating it.</returns>
        private object Retained(string id)
        {
            var field = typeof(PublicChannelPostManager).GetField("_retained", BindingFlags.Instance | BindingFlags.NonPublic)!;
            return ((IDictionary)field.GetValue(Manager)!)[id]!;
        }
        /// <summary>Forces only a process-local deadline under its existing lock without changing production clocks.</summary>
        /// <param name="id">Retained opaque draft/job identity.</param>
        public void Expire(string id)
        {
            var draft = Retained(id);
            var type = draft.GetType();
            lock (type.GetField("Sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(draft)!)
                type.GetField("Expires", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(draft, DateTimeOffset.UtcNow.AddMinutes(-1));
        }
        /// <summary>Observes an older retained job after foreground composition replacement.</summary>
        /// <param name="id">Admitted job's opaque identity.</param>
        /// <returns>The closed actual manager phase.</returns>
        public PublicChannelPostPhase JobPhase(string id)
        {
            var draft = Retained(id);
            var type = draft.GetType();
            lock (type.GetField("Sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(draft)!)
                return (PublicChannelPostPhase)type.GetField("Phase", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(draft)!;
        }
        /// <summary>Observes the existing operation lease count so assertions do not race final report delivery.</summary>
        /// <param name="id">Retained opaque job identity.</param>
        /// <returns>Queued/running operations still owning assets and status I/O.</returns>
        public int JobOperations(string id)
        {
            var draft = Retained(id);
            var type = draft.GetType();
            lock (type.GetField("Sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(draft)!)
                return (int)type.GetField("Operations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(draft)!;
        }
        /// <summary>Reads the readonly manager-owned OS-temp directory for cleanup assertions.</summary>
        /// <param name="id">Retained opaque draft/job identity.</param>
        /// <returns>The exact GUID-owned non-repository directory.</returns>
        public string AssetDirectory(string id)
        {
            var draft = Retained(id);
            return (string)draft.GetType().GetField("DirectoryPath", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(draft)!;
        }
        /// <summary>Reads a detached real progress snapshot under the owning job lock, without adding production API surface.</summary>
        /// <param name="id">Optional retained job id; defaults to current composition's admitted job.</param>
        /// <returns>Actual frozen accounting and sanitized details.</returns>
        /// <remarks>Requests the complete details explicitly; summary-only worker status edits deliberately omit unused detail copies.</remarks>
        public PublicChannelPostProgressSnapshot Progress(string? id = null)
        {
            var draft = Retained(id ?? Manager.GetDraft(Key)!.Id);
            var type = draft.GetType();
            lock (type.GetField("Sync", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(draft)!)
            {
                var progress = type.GetField("Progress", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(draft)!;
                return (PublicChannelPostProgressSnapshot)typeof(PublicChannelPostManager).GetMethod("ProgressSnapshot",
                    BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { progress, true })!;
            }
        }
        /// <summary>Admits content and rebinds the current control revision exactly as the foreground composer does.</summary>
        /// <param name="message">Private Telegram input from the authorized actor.</param>
        /// <returns>The accepted immutable snapshot.</returns>
        public PublicChannelPostDraftSnapshot Add(TelegramMessage message)
        {
            var result = Manager.AddMessage(Key, message);
            Assert.True(result.Accepted, result.ReasonCode);
            var d = result.Draft!;
            Manager.BindControlMessage(Key, d.Id, d.Revision, 700);
            return Manager.GetDraft(Key)!;
        }
        /// <summary>Queues a selected real private preview and waits until its keyboard has armed confirmation.</summary>
        /// <param name="index">Zero-based frozen target selection.</param>
        /// <returns>A task after the current revision becomes PreviewReady.</returns>
        public async Task PreviewAsync(int index = 0)
        {
            var d = Manager.GetDraft(Key)!;
            Assert.True(Manager.QueuePreview(Key, d.Id, d.Revision, d.ControlMessageId, index).Accepted);
            await ReadyAsync();
        }
        /// <summary>Waits for the current preview to be visibly armed.</summary>
        /// <returns>A bounded task checking actual manager state.</returns>
        public Task ReadyAsync() => WaitAsync(() => Manager.GetDraft(Key)?.Phase == PublicChannelPostPhase.PreviewReady);
        /// <summary>Waits for terminal fan-out and completion of its original status I/O/operation lease.</summary>
        /// <returns>A bounded task checking actual manager lifecycle rather than transport timing.</returns>
        public Task CompletedAsync() => WaitAsync(() =>
        {
            var draft = Manager.GetDraft(Key);
            return draft?.Phase == PublicChannelPostPhase.Completed && JobOperations(draft.Id) == 0;
        });
        /// <summary>Yields to worker continuations without timing assumptions or arbitrary sleeps.</summary>
        /// <param name="predicate">Consumer-visible terminal state to observe.</param>
        /// <returns>A task throwing on a missing transition instead of hanging the suite.</returns>
        public async Task WaitAsync(Func<bool> predicate)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!predicate()) { deadline.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
        }
        /// <summary>Admits exactly one publication of the latest armed revision.</summary>
        public void Publish()
        {
            var d = Manager.GetDraft(Key)!;
            Assert.True(Manager.QueuePublish(Key, d.Id, d.Revision, d.ControlMessageId).Accepted);
        }
        /// <summary>Registers an exact active Owned configuration without using stale database configuration.</summary>
        /// <param name="id">Internal runtime bot identifier.</param>
        /// <param name="telegramId">Synthetic numeric BotFather identity.</param>
        /// <param name="channels">Persisted associated channel forms.</param>
        public void AddOwned(string id, int telegramId, params string[] channels) => AddOwned(id, telegramId, channels, true);
        /// <summary>Registers a runtime Owned bot with an explicit activation gate.</summary>
        /// <param name="id">Internal runtime identifier.</param>
        /// <param name="telegramId">Synthetic numeric bot identity.</param>
        /// <param name="channel">Associated public channel.</param>
        /// <param name="enabled">Whether the bot may participate.</param>
        public void AddOwned(string id, int telegramId, string channel, bool enabled) => AddOwned(id, telegramId, new[] { channel }, enabled);
        /// <summary>Writes the authoritative Owned runtime configuration.</summary>
        /// <param name="id">Internal identifier.</param>
        /// <param name="telegramId">Numeric bot identity.</param>
        /// <param name="channels">Associated channel forms.</param>
        /// <param name="enabled">Active participation flag.</param>
        private void AddOwned(string id, int telegramId, string[] channels, bool enabled) => Registry.Upsert(new BotInstance { Id = id, Type = BotInstanceTypes.Owned, Enabled = enabled, IsDefault = id == "main", Token = Token(telegramId), ChannelIdsJson = JsonSerializer.Serialize(channels) });
        /// <summary>Persists a Tenant target and optionally makes its exact transport available in runtime.</summary>
        /// <param name="id">Internal store identifier.</param>
        /// <param name="telegramId">Numeric bot identity.</param>
        /// <param name="owner">Telegram owner identifier.</param>
        /// <param name="channel">Saved channel association.</param>
        /// <param name="consent">Independent public-post consent.</param>
        /// <param name="runtime">Whether the runtime knows this exact store.</param>
        /// <returns>A task after the detached store becomes authoritative.</returns>
        public async Task AddTenantAsync(string id, int telegramId, long owner, string channel, bool consent = true, bool runtime = true)
        {
            var row = new BotInstance { Id = id, Type = BotInstanceTypes.Tenant, Enabled = true, Token = Token(telegramId), TelegramBotId = telegramId, OwnerTelegramUserId = owner, TenantChannelIdsJson = JsonSerializer.Serialize(new[] { channel }), TenantPublicChannelPostsEnabled = consent, TenantMandatoryJoinEnabled = false };
            await using var db = Databases.Users.CreateDbContext();
            db.BotInstances.Add(row);
            await db.SaveChangesAsync();
            if (runtime) Registry.Upsert(row);
        }
        /// <summary>Dispatches one real Telegram update through a fresh production scope.</summary>
        /// <param name="message">Synthetic private input.</param>
        /// <param name="botId">Exact runtime source bot id; defaults to the Owned source.</param>
        /// <returns>A task after the foreground handler completes.</returns>
        public async Task DispatchAsync(TelegramMessage message, string botId = "main")
        {
            var config = Registry.Bots.Single(x => x.Id == botId);
            var client = Provider.GetRequiredService<BotClientProvider>().GetClient(botId, long.Parse(config.Token.Split(':')[0]));
            await using var scope = Provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TelegramBotService>().DispatchUpdateAsync(client, new Update { Id = message.Id, Message = message }, new BotRuntimeContext { Config = config, Client = client }, CancellationToken.None);
        }
        /// <summary>Uses the real recorded cpp keyboard, never inventing callback authentication from a test payload.</summary>
        /// <param name="action">Closed callback action to select.</param>
        /// <param name="actor">Actual callback sender Telegram id.</param>
        /// <param name="controlOverride">Optional wrong control id for forgery checks.</param>
        /// <param name="chatId">Attached Telegram chat id; negative values exercise nonprivate-chat rejection.</param>
        /// <param name="botId">Runtime source id used to detect cross-bot callbacks.</param>
        /// <param name="dataOverride">Optional explicitly malformed/stale payload for negative cases.</param>
        /// <returns>A task after fresh-scope callback dispatch.</returns>
        public async Task CallbackAsync(string action, long actor = 711, int? controlOverride = null,
            long chatId = 711, string botId = "main", string? dataOverride = null)
        {
            var d = Manager.GetDraft(Key)!;
            var data = dataOverride ?? Http.Requests.SelectMany(x => x.Callbacks).Last(x => x.EndsWith(":" + action, StringComparison.Ordinal));
            var config = Registry.Bots.Single(x => x.Id == botId);
            var client = Provider.GetRequiredService<BotClientProvider>().GetClient(botId, long.Parse(config.Token.Split(':')[0]));
            await using var scope = Provider.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TelegramBotService>().DispatchUpdateAsync(client, new Update
            {
                Id = Http.NextId(), CallbackQuery = new CallbackQuery
                {
                    Id = Guid.NewGuid().ToString("N"), From = new TelegramUser { Id = actor, FirstName = "actor" },
                    Data = data, Message = new TelegramMessage
                    {
                        Id = controlOverride ?? d.ControlMessageId,
                        Chat = new Chat { Id = chatId, Type = chatId > 0 ? ChatType.Private : ChatType.Channel }
                    }
                }
            }, new BotRuntimeContext { Config = config, Client = client }, CancellationToken.None);
        }
        /// <summary>Creates plain private text without parse-mode interpretation.</summary>
        /// <param name="id">Telegram source message identifier.</param>
        /// <param name="text">Exact visible Unicode content.</param>
        /// <returns>A detached Telegram input.</returns>
        public static TelegramMessage Text(int id, string text) => new() { Id = id, Text = text, From = new TelegramUser { Id = 711, FirstName = "admin" }, Chat = new Chat { Id = 711, Type = ChatType.Private } };
        /// <summary>Creates two renditions so selection of the largest file is exercised.</summary>
        /// <param name="id">Telegram source message identifier.</param>
        /// <param name="file">Bot-scoped largest file identifier.</param>
        /// <param name="album">Optional incoming media group identity.</param>
        /// <param name="caption">Optional common caption.</param>
        /// <returns>A detached private photo update.</returns>
        public static TelegramMessage Photo(int id, string file, string? album = null, string? caption = null) => new() { Id = id, From = new TelegramUser { Id = 711, FirstName = "admin" }, Chat = new Chat { Id = 711, Type = ChatType.Private }, Caption = caption, MediaGroupId = album, Photo = new[] { new PhotoSize { FileId = "small_" + file, FileUniqueId = "small_" + file, Width = 1, Height = 1, FileSize = 1 }, new PhotoSize { FileId = file, FileUniqueId = file, Width = 10, Height = 10, FileSize = PublicPostHttp.Image(file).Length } } };
        /// <summary>Stops all manager I/O before disposing services and fixture-scoped SQLite pools.</summary>
        /// <returns>A task after safe fixture cleanup.</returns>
        public async ValueTask DisposeAsync()
        {
            Http.ReleaseAll();
            if (Provider != null) { await Manager.StopAsync(CancellationToken.None); await Provider.DisposeAsync(); }
            Http.Dispose();
            Databases.Dispose();
        }
    }

    /// <summary>Captures request content while streams are alive and returns Bot API-shaped real SDK responses.</summary>
    private sealed class PublicPostHttp : HttpMessageHandler
    {
        /// <summary>All completed request captures in transport order.</summary>
        public ConcurrentQueue<PublicPostRequest> Requests { get; } = new();
        /// <summary>Actual content attempts to negative channels, including explicitly rejected/ambiguous sends.</summary>
        public List<PublicPostRequest> ChannelPosts => Requests.Where(x => x.ChatId < 0 && x.Method is "sendMessage" or "sendPhoto" or "sendMediaGroup").ToList();
        /// <summary>Channel to reject once with an explicit 429.</summary>
        public long RateLimitChat;
        /// <summary>Channel whose request is recorded as accepted but response is lost.</summary>
        public long AmbiguousChat;
        /// <summary>Whether all control edits fail after content delivery.</summary>
        public bool FailStatus;
        /// <summary>Whether the private preview waits for the policy cancellation deadline.</summary>
        public bool TimeoutPreview;
        /// <summary>Whether source downloads fail before a real preview can be sent.</summary>
        public bool FailDownload;
        /// <summary>Live metadata permission removals applied only after a real preview.</summary>
        public ConcurrentDictionary<long, bool> DeniedChannels { get; } = new();
        /// <summary>Live public channel username changes used to reject unpreviewed footer substitution.</summary>
        public ConcurrentDictionary<long, string> Usernames { get; } = new();
        /// <summary>Canonical channel to reject authoritatively with a non-retryable 400.</summary>
        public long FailedChat;
        /// <summary>Whether a small declared photo actually streams more than Telegram's maximum byte limit.</summary>
        public bool OversizeDownload;
        /// <summary>Total source file byte downloads.</summary>
        public int DownloadCount;
        /// <summary>Monotonic Telegram message identity; duplicates never arise from the responder.</summary>
        private int _id = 1000;
        /// <summary>One-shot transport barriers consumed by matching requests.</summary>
        private readonly ConcurrentQueue<PublicPostGate> _gates = new();
        /// <summary>Tracks every gate so teardown releases even an unconsumed barrier.</summary>
        private readonly ConcurrentBag<PublicPostGate> _allGates = new();
        /// <summary>Creates known one-pixel PNG bytes with an inert trailing occurrence marker for exact upload assertions.</summary>
        /// <param name="file">Source file identifier.</param>
        /// <returns>A valid tiny image followed by a distinct non-rendered marker for this source file.</returns>
        public static byte[] Image(string file) => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+j1k0AAAAASUVORK5CYII=").Concat(Encoding.UTF8.GetBytes(file)).ToArray();
        /// <summary>Allocates a globally distinct synthetic message/update identifier.</summary>
        /// <returns>A positive identifier.</returns>
        public int NextId() => Interlocked.Increment(ref _id);
        /// <summary>Blocks exactly the next matching operation until an explicit release or cancellation.</summary>
        /// <param name="method">Bot API method name.</param>
        /// <param name="channelsOnly">True limits the barrier to negative channel sends.</param>
        /// <param name="chatId">Optional exact canonical channel filter for an isolated retry/consent barrier.</param>
        /// <returns>The deterministic gate owned by the test.</returns>
        public PublicPostGate HoldNext(string method, bool channelsOnly = false, long? chatId = null)
        {
            var gate = new PublicPostGate(method, channelsOnly, chatId);
            _gates.Enqueue(gate); _allGates.Add(gate); return gate;
        }
        /// <summary>Unblocks all test-controlled operations before fixture cleanup.</summary>
        public void ReleaseAll() { foreach (var gate in _allGates) gate.Release.TrySetResult(); }
        /// <summary>Decodes multipart/JSON requests, captures uploads immediately and serializes valid Telegram responses.</summary>
        /// <param name="request">Real SDK HTTP request; no network is performed.</param>
        /// <param name="cancellationToken">Manager's bounded linked operation cancellation.</param>
        /// <returns>A correctly shaped Bot API success or configured authoritative failure.</returns>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/file/bot", StringComparison.Ordinal))
            {
                Assert.Contains("bot10001:", path);
                if (FailDownload) return new HttpResponseMessage(HttpStatusCode.NotFound);
                Interlocked.Increment(ref DownloadCount);
                if (OversizeDownload) return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10 * 1024 * 1024 + 1]) };
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Image(Uri.UnescapeDataString(path.Split('/').Last()))) };
            }
            var tokenPart = path.Split('/')[1];
            var botId = long.Parse(tokenPart[3..].Split(':')[0]);
            var method = path.Split('/').Last();
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (request.Content is MultipartFormDataContent multipart)
            {
                foreach (var part in multipart)
                {
                    var name = part.Headers.ContentDisposition!.Name!.Trim('"');
                    if (part.Headers.ContentDisposition.FileName != null) files[name] = await part.ReadAsByteArrayAsync(cancellationToken);
                    else fields[name] = await part.ReadAsStringAsync(cancellationToken);
                }
            }
            else if (request.Content != null)
            {
                var raw = await request.Content.ReadAsStringAsync(cancellationToken);
                if (raw.StartsWith('{'))
                {
                    using var json = JsonDocument.Parse(raw);
                    foreach (var prop in json.RootElement.EnumerateObject()) fields[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString()! : prop.Value.GetRawText();
                }
                else foreach (var pair in raw.Split('&', StringSplitOptions.RemoveEmptyEntries)) { var split = pair.Split('=', 2); fields[WebUtility.UrlDecode(split[0])] = WebUtility.UrlDecode(split.Length == 2 ? split[1] : ""); }
            }
            if (method == "sendPhoto" && !fields.ContainsKey("photo") && files.ContainsKey("photo")) fields["photo"] = "attach://photo";
            var capture = new PublicPostRequest(botId, method, fields, files);
            if (method == "getFile") Assert.Equal(10001, botId);
            if (capture.Body.Contains("🤖 ربات:", StringComparison.Ordinal))
            {
                Assert.False(fields.TryGetValue("parse_mode", out var parseMode) && !string.IsNullOrWhiteSpace(parseMode));
                if (method == "sendMessage")
                {
                    Assert.True(fields.TryGetValue("link_preview_options", out var options));
                    using var previewOptions = JsonDocument.Parse(options!);
                    Assert.True(previewOptions.RootElement.GetProperty("is_disabled").GetBoolean());
                }
            }
            Requests.Enqueue(capture);
            if (_gates.TryPeek(out var gate) && gate.Method == method && (!gate.ChannelsOnly || capture.ChatId < 0) &&
                (!gate.ChatId.HasValue || gate.ChatId.Value == capture.ChatId) && _gates.TryDequeue(out _))
            {
                gate.Entered.TrySetResult();
                await gate.Release.Task.WaitAsync(cancellationToken);
            }
            if (TimeoutPreview && capture.ChatId == 711 && method is "sendPhoto" or "sendMediaGroup") await Task.Delay(Timeout.Infinite, cancellationToken);
            if (FailStatus && method == "editMessageText") return Error(400, "Bad Request: status inaccessible");
            if (capture.ChatId == RateLimitChat && capture.ChatId < 0 && method.StartsWith("send", StringComparison.Ordinal))
            { RateLimitChat = 0; return Error(429, "Too Many Requests", retryAfter: 0); }
            if (capture.ChatId == AmbiguousChat && capture.ChatId < 0 && method.StartsWith("send", StringComparison.Ordinal))
            { AmbiguousChat = 0; await Task.Delay(Timeout.Infinite, cancellationToken); }
            if (capture.ChatId == FailedChat && capture.ChatId < 0 && method.StartsWith("send", StringComparison.Ordinal))
                return Error(400, "Bad Request: photo rejected");
            object result = method switch
            {
                "getMe" => new { id = botId, is_bot = true, first_name = "fixture", username = "bot" + botId },
                "getChat" => ChatResult(capture.ChatId),
                "getChatMember" => new { status = "administrator", user = new { id = botId, is_bot = true, first_name = "fixture" }, can_post_messages = capture.ChatId != -1005 && !DeniedChannels.ContainsKey(capture.ChatId), can_manage_chat = true, can_delete_messages = true, can_manage_video_chats = true, can_restrict_members = true, can_promote_members = true, can_change_info = true, can_invite_users = true, is_anonymous = false },
                "getFile" => new { file_id = fields["file_id"], file_unique_id = fields["file_id"], file_size = Image(fields["file_id"]).Length, file_path = "photos/" + fields["file_id"] },
                "sendMediaGroup" => capture.MediaIds.Select(_ => MessageResult(capture.ChatId)).ToArray(),
                "sendMessage" or "sendPhoto" => MessageResult(capture.ChatId),
                "editMessageText" or "editMessageReplyMarkup" => MessageResult(capture.ChatId, int.Parse(fields["message_id"])),
                "answerCallbackQuery" or "deleteMessage" or "sendChatAction" => true,
                _ => throw new InvalidOperationException("Unexpected fixture API method: " + method)
            };
            if (capture.ChatId < 0 && method is "sendPhoto" or "sendMediaGroup") Assert.Equal(capture.MediaIds.Count, capture.Uploads.Count);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { ok = true, result }), Encoding.UTF8, "application/json") };
        }
        /// <summary>Creates Telegram channel metadata, including private/non-channel exclusions.</summary>
        /// <param name="id">Resolved canonical negative chat identity.</param>
        /// <returns>Bot API chat metadata.</returns>
        private object ChatResult(long id) => new { id, type = id == -1004 ? "supergroup" : "channel", title = "fixture", username = id == -1003 ? null : Usernames.GetValueOrDefault(id, "channel" + Math.Abs(id)) };
        /// <summary>Creates a distinct returned message so control binding is exercised.</summary>
        /// <param name="chat">Telegram destination identity.</param>
        /// <param name="messageId">Existing identity for an edit; null allocates a new sent message identity.</param>
        /// <returns>A Bot API message object.</returns>
        private object MessageResult(long chat, int? messageId = null) => new { message_id = messageId ?? NextId(), date = 1760000000, chat = new { id = chat, type = chat < 0 ? "channel" : "private" }, text = "fixture" };
        /// <summary>Returns an explicit authoritative API rejection without transport ambiguity.</summary>
        /// <param name="code">Telegram error code.</param>
        /// <param name="description">Sanitized test rejection.</param>
        /// <param name="retryAfter">429 retry delay in seconds.</param>
        /// <returns>An SDK-decodable unsuccessful response.</returns>
        private static HttpResponseMessage Error(int code, string description, int retryAfter = 0) => new((HttpStatusCode)code) { Content = new StringContent(JsonSerializer.Serialize(new { ok = false, error_code = code, description, parameters = new { retry_after = retryAfter } }), Encoding.UTF8, "application/json") };
    }

    /// <summary>A single deterministic barrier at a real SDK transport operation.</summary>
    /// <param name="Method">Bot API method to intercept.</param>
    /// <param name="ChannelsOnly">Whether only channel delivery matches.</param>
    /// <param name="ChatId">Optional canonical Telegram channel identity to match.</param>
    private sealed record PublicPostGate(string Method, bool ChannelsOnly, long? ChatId)
    {
        /// <summary>Completes after request bytes are captured and before its response is returned.</summary>
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>Explicit test release; operation cancellation also releases the awaiting request.</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Detached request metadata and byte captures, with no retained HTTP request or upload streams.</summary>
    private sealed class PublicPostRequest
    {
        /// <summary>Numeric sending bot identity parsed from a synthetic request token.</summary>
        public long BotId { get; }
        /// <summary>Bot API method.</summary>
        public string Method { get; }
        /// <summary>Resolved chat identity; aliases are translated using the fixture's canonical channel map.</summary>
        public long ChatId { get; }
        /// <summary>Exact common text/caption including destination footer.</summary>
        public string Body { get; }
        /// <summary>Ordered source identifiers or upload attachment references.</summary>
        public List<string> MediaIds { get; } = new();
        /// <summary>Ordered eagerly captured upload bytes.</summary>
        public List<byte[]> Uploads { get; } = new();
        /// <summary>Per-media captions, including empty later album captions.</summary>
        public List<string> Captions { get; } = new();
        /// <summary>Explicit entities from text or first caption.</summary>
        public List<JsonElement> Entities { get; } = new();
        /// <summary>Detached callback payloads from real reply markup.</summary>
        public List<string> Callbacks { get; } = new();
        /// <summary>Snapshots SDK JSON and multipart fields before any request streams close.</summary>
        /// <param name="botId">Synthetic numeric sending bot identity.</param>
        /// <param name="method">SDK Bot API method.</param>
        /// <param name="fields">Serialized request fields.</param>
        /// <param name="files">Eager attachment byte captures.</param>
        public PublicPostRequest(long botId, string method, Dictionary<string, string> fields, Dictionary<string, byte[]> files)
        {
            BotId = botId; Method = method;
            if (fields.TryGetValue("chat_id", out var chat))
            {
                if (long.TryParse(chat, out var numeric)) ChatId = numeric;
                else { var digits = new string(chat.Where(char.IsDigit).ToArray()); ChatId = digits.Length == 0 ? -9999 : -long.Parse(digits); }
            }
            Body = fields.GetValueOrDefault("text") ?? fields.GetValueOrDefault("caption") ?? "";
            var entities = fields.GetValueOrDefault("entities") ?? fields.GetValueOrDefault("caption_entities");
            if (entities != null) { using var doc = JsonDocument.Parse(entities); Entities.AddRange(doc.RootElement.EnumerateArray().Select(x => x.Clone())); }
            if (fields.TryGetValue("photo", out var photo)) { MediaIds.Add(photo); Captions.Add(Body); CaptureUpload(photo, files); }
            if (fields.TryGetValue("media", out var media))
            {
                using var doc = JsonDocument.Parse(media);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var id = item.GetProperty("media").GetString()!;
                    MediaIds.Add(id); CaptureUpload(id, files);
                    var caption = item.TryGetProperty("caption", out var value) ? value.GetString()! : "";
                    Captions.Add(caption);
                    if (MediaIds.Count == 1)
                    {
                        Body = caption;
                        if (item.TryGetProperty("caption_entities", out var list)) Entities.AddRange(list.EnumerateArray().Select(x => x.Clone()));
                    }
                }
            }
            if (fields.TryGetValue("reply_markup", out var markup))
            {
                using var doc = JsonDocument.Parse(markup);
                if (doc.RootElement.TryGetProperty("inline_keyboard", out var rows))
                    foreach (var row in rows.EnumerateArray()) foreach (var button in row.EnumerateArray()) if (button.TryGetProperty("callback_data", out var callback)) Callbacks.Add(callback.GetString()!);
            }
        }
        /// <summary>Resolves multipart attachment references while preserving occurrence order.</summary>
        /// <param name="id">Photo field or attach reference.</param>
        /// <param name="files">Captured multipart files.</param>
        private void CaptureUpload(string id, Dictionary<string, byte[]> files)
        {
            var name = id.StartsWith("attach://", StringComparison.Ordinal) ? id[9..] : id;
            if (files.TryGetValue(name, out var bytes)) Uploads.Add(bytes);
            else if (files.Count == 1 && Method == "sendPhoto") Uploads.Add(files.Values.Single());
        }
        /// <summary>Checks UTF-16 offsets and exact identity-specific clickable footer links.</summary>
        public void AssertFooter()
        {
            foreach (var username in new[] { "bot" + BotId, "channel" + Math.Abs(ChatId) })
            {
                var entity = Assert.Single(Entities, x => x.TryGetProperty("url", out var url) && url.GetString() == "https://t.me/" + username);
                Assert.Equal("text_link", entity.GetProperty("type").GetString());
                Assert.Equal("@" + username, Body.Substring(entity.GetProperty("offset").GetInt32(), entity.GetProperty("length").GetInt32()));
            }
        }
    }
}
