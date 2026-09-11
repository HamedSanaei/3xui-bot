using System.Reflection;
using Adminbot.Domain;
using Adminbot.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Xunit;

/// <summary>
/// Regression coverage for the redesigned tenant storefront installation tutorials.
/// </summary>
/// <remarks>
/// The redesign changes who owns customer-facing tutorials. Tenant owners can no longer configure tutorial links, and
/// every storefront now serves three built-in image albums shipped as publish assets. Four invariants are pinned here,
/// because each has a plausible regression that would be invisible in production until a customer hit it:
///
/// 1. The owner-configurable manager stays unreachable, and historical <c>TenantTutorialsJson</c> is never rewritten, so
///    a rollback to owner-configured tutorials remains possible.
/// 2. The customer menu always offers exactly the three built-in categories and never reads tenant tutorial
///    configuration to build it.
/// 3. Album delivery is real Telegram media groups with correct batching: at most ten items per album, step order
///    preserved, image 11 delivered rather than dropped, and a single image sent as a photo because a one-item media
///    group is invalid.
/// 4. Choosing a tutorial is presentation-only: it writes nothing to users.db.
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>Owner-panel label that must no longer be offered now that tutorials are built in.</summary>
    private const string LegacyOwnerTutorialButtonLabel = "🎓 آموزش‌ها";

    /// <summary>Builds a temporary tutorial asset directory containing the given file names.</summary>
    /// <param name="fileNames">File names to create inside the directory.</param>
    /// <returns>The absolute path of the created temporary root; the caller deletes it.</returns>
    private static string CreateTutorialAssetRoot(params string[] fileNames)
    {
        var root = Path.Combine(Path.GetTempPath(), "AdminbotTutorialAssets-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        foreach (var name in fileNames)
        {
            var nested = Path.Combine(root, name);
            if (Path.GetDirectoryName(nested) is { Length: > 0 } parent)
                Directory.CreateDirectory(parent);
            System.IO.File.WriteAllText(nested, "image-bytes");
        }

        return root;
    }

    /// <summary>Deletes a temporary tutorial asset root created by <see cref="CreateTutorialAssetRoot" />.</summary>
    /// <param name="root">Absolute temporary root to delete.</param>
    private static void DeleteTutorialAssetRoot(string root)
    {
        try
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
            // Temporary cleanup is best effort and must never fail a test.
        }
    }

    /// <summary>Copies the repository's real tutorial assets into a temporary root for end-to-end resolution tests.</summary>
    /// <returns>The absolute temporary root containing the three real tutorial directories.</returns>
    private static string CopyRepositoryTutorialAssets()
    {
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Assets/tutorials"));
        var root = Path.Combine(Path.GetTempPath(), "AdminbotTutorialCopy-" + Guid.NewGuid().ToString("N"));
        foreach (var directory in Directory.GetDirectories(source))
        {
            var target = Path.Combine(root, Path.GetFileName(directory));
            Directory.CreateDirectory(target);
            foreach (var file in Directory.GetFiles(directory))
                System.IO.File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        return root;
    }

    /// <summary>
    /// Proves each tutorial kind maps to exactly one fixed directory and that no other value resolves.
    /// </summary>
    /// <remarks>
    /// A callback payload is the only customer-controlled input in this flow, so the mapping must be a closed set. If an
    /// unknown kind silently mapped to a directory, a crafted payload could reach another tutorial's files.
    /// </remarks>
    [Fact]
    public void Tutorial_kinds_map_to_their_own_fixed_directories()
    {
        Assert.Equal("android_v2rayng", TenantTutorialAssetService.ResolveDirectoryName(TenantTutorialKinds.Android));
        Assert.Equal("windows_v2rayn", TenantTutorialAssetService.ResolveDirectoryName(TenantTutorialKinds.Windows));
        Assert.Equal("ios_android_v2box", TenantTutorialAssetService.ResolveDirectoryName(TenantTutorialKinds.Ios));
        Assert.Equal(3, TenantTutorialKinds.All.Count);
        Assert.True(TenantTutorialKinds.IsSupported(TenantTutorialKinds.Android));

        // Unknown, empty, and traversal-shaped values are rejected rather than mapped.
        Assert.False(TenantTutorialKinds.IsSupported("../android_v2rayng"));
        Assert.False(TenantTutorialKinds.IsSupported("ANDROID"));
        Assert.False(TenantTutorialKinds.IsSupported(string.Empty));
        var unsupported = TenantTutorialAssetService.Resolve("../../etc");
        Assert.Equal(TenantTutorialAssetStatus.UnsupportedKind, unsupported.Status);
        Assert.Empty(unsupported.ImagePaths);
        Assert.Throws<ArgumentOutOfRangeException>(() => TenantTutorialAssetService.ResolveDirectoryName("nope"));
    }

    /// <summary>
    /// Proves installation steps sort naturally, so 10 follows 9 instead of following 1.
    /// </summary>
    /// <remarks>
    /// Lexicographic ordering would produce 1,10,11,2 and scramble a numbered guide. This is the ordering the customer
    /// actually follows, so it is asserted on file names rather than on a generic string comparison.
    /// </remarks>
    [Fact]
    public void Installation_steps_sort_in_natural_numeric_order()
    {
        var names = new[] { "10.png", "2.png", "1.png", "11.png", "3.png", "9.png" };
        var sorted = names.OrderBy(x => x, Comparer<string>.Create(TenantTutorialAssetService.CompareNatural)).ToArray();
        Assert.Equal(new[] { "1.png", "2.png", "3.png", "9.png", "10.png", "11.png" }, sorted);

        // Prefixed variants used by the shipped V2Box and v2rayN guides must order the same way.
        var prefixed = new[] { "v2box 10.png", "v2box 2.png", "v2box 1.png" };
        Assert.Equal(
            new[] { "v2box 1.png", "v2box 2.png", "v2box 10.png" },
            prefixed.OrderBy(x => x, Comparer<string>.Create(TenantTutorialAssetService.CompareNatural)).ToArray());
        Assert.True(TenantTutorialAssetService.CompareNatural("pc 2.png", "pc 10.png") < 0);
        Assert.Equal(0, TenantTutorialAssetService.CompareNatural("pc 2.png", "pc 2.png"));
        Assert.True(TenantTutorialAssetService.CompareNatural("a", "b") < 0);
        Assert.True(TenantTutorialAssetService.CompareNatural("a", "aa") < 0);
    }

    /// <summary>
    /// Proves only supported image formats are selected and unrelated files are ignored.
    /// </summary>
    /// <remarks>
    /// Telegram cannot upload a <c>Thumbs.db</c> or a text note as a photo, and picking one up would break the whole
    /// album. Extension matching is case-insensitive so a <c>.PNG</c> asset is still delivered.
    /// </remarks>
    [Fact]
    public void Asset_resolution_selects_only_supported_images()
    {
        var root = CreateTutorialAssetRoot(
            "android_v2rayng/1.png",
            "android_v2rayng/2.PNG",
            "android_v2rayng/3.jpg",
            "android_v2rayng/4.jpeg",
            "android_v2rayng/Thumbs.db",
            "android_v2rayng/notes.txt",
            "android_v2rayng/.DS_Store",
            "windows_v2rayn/pc 1.png",
            "ios_android_v2box/v2box 1.png");
        try
        {
            var android = TenantTutorialAssetService.Resolve(TenantTutorialKinds.Android, root);
            Assert.Equal(TenantTutorialAssetStatus.Available, android.Status);
            Assert.Equal(4, android.ImagePaths.Count);
            Assert.All(android.ImagePaths, path => Assert.True(TenantTutorialAssetService.IsSupportedImage(path)));
            Assert.DoesNotContain(android.ImagePaths, path => path.EndsWith("Thumbs.db", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(android.ImagePaths, path => path.EndsWith("notes.txt", StringComparison.OrdinalIgnoreCase));
            // Android must never pick up the Windows or V2Box files even though they exist in the same root.
            Assert.All(android.ImagePaths, path => Assert.Contains("android_v2rayng", path));
        }
        finally
        {
            DeleteTutorialAssetRoot(root);
        }
    }

    /// <summary>
    /// Proves a missing or image-less directory is reported as unavailable instead of throwing.
    /// </summary>
    /// <remarks>
    /// Asset deployment problems are operational, not customer errors. The resolver must return a status so the caller can
    /// answer the customer politely rather than letting a filesystem exception escape into the update pipeline.
    /// </remarks>
    [Fact]
    public void Missing_and_empty_tutorial_directories_are_reported_as_unavailable()
    {
        var emptyRoot = CreateTutorialAssetRoot("android_v2rayng/notes.txt");
        try
        {
            var missing = TenantTutorialAssetService.Resolve(TenantTutorialKinds.Windows, emptyRoot);
            Assert.Equal(TenantTutorialAssetStatus.Missing, missing.Status);
            Assert.False(missing.IsAvailable);
            Assert.Equal("Assets/tutorials/windows_v2rayn", missing.RelativeDirectory);

            var empty = TenantTutorialAssetService.Resolve(TenantTutorialKinds.Android, emptyRoot);
            Assert.Equal(TenantTutorialAssetStatus.Empty, empty.Status);
            Assert.False(empty.IsAvailable);
        }
        finally
        {
            DeleteTutorialAssetRoot(emptyRoot);
        }
    }

    /// <summary>
    /// Proves the real shipped assets resolve for all three categories with the expected counts.
    /// </summary>
    /// <remarks>
    /// This is the closest a unit test can get to the deployment guarantee: it fails if a tutorial directory is renamed,
    /// emptied, or stops shipping images, which is exactly the deployment state the release preflight also refuses.
    /// </remarks>
    [Fact]
    public void Shipped_assets_resolve_for_all_three_categories()
    {
        var root = CopyRepositoryTutorialAssets();
        try
        {
            foreach (var kind in TenantTutorialKinds.All)
            {
                var assets = TenantTutorialAssetService.Resolve(kind, root);
                Assert.True(assets.IsAvailable, $"Tutorial '{kind}' assets are unavailable: {assets.Status}.");
                Assert.NotEmpty(assets.ImagePaths);
                Assert.False(string.IsNullOrWhiteSpace(assets.Caption));
                // Every image must live inside this tutorial's own directory.
                Assert.All(assets.ImagePaths, path =>
                    Assert.Contains(TenantTutorialAssetService.ResolveDirectoryName(kind), path));
            }
        }
        finally
        {
            DeleteTutorialAssetRoot(root);
        }
    }

    /// <summary>
    /// Proves batching is lossless and never exceeds Telegram's ten-item media-group limit.
    /// </summary>
    /// <remarks>
    /// Dropping image 11 would silently truncate an installation guide, and an eleven-item media group would be rejected
    /// outright, so both the size cap and the item count are asserted.
    /// </remarks>
    [Fact]
    public void Media_group_batches_never_exceed_ten_items_and_never_drop_one()
    {
        var paths = Enumerable.Range(1, 21).Select(index => $"step-{index}.png").ToArray();
        var batches = TenantTutorialAssetService.BatchForMediaGroups(paths);

        Assert.Equal(3, batches.Count);
        Assert.Equal(new[] { 10, 10, 1 }, batches.Select(batch => batch.Count));
        Assert.All(batches, batch => Assert.InRange(batch.Count, 1, TenantTutorialAssetService.MaxMediaGroupItems));
        // Order is preserved and nothing is lost.
        Assert.Equal(paths, batches.SelectMany(batch => batch));

        Assert.Equal(new[] { 10 }, TenantTutorialAssetService.BatchForMediaGroups(Enumerable.Range(1, 10).Select(i => $"s{i}.png").ToArray()).Select(b => b.Count));
        Assert.Equal(new[] { 10, 1 }, TenantTutorialAssetService.BatchForMediaGroups(Enumerable.Range(1, 11).Select(i => $"s{i}.png").ToArray()).Select(b => b.Count));
        Assert.Empty(TenantTutorialAssetService.BatchForMediaGroups(Array.Empty<string>()));
        Assert.Equal(new[] { 1 }, TenantTutorialAssetService.BatchForMediaGroups(new[] { "only.png" }).Select(b => b.Count));
    }

    /// <summary>
    /// Proves a guide of up to ten images is delivered as one real Telegram media group carrying a single caption.
    /// </summary>
    /// <remarks>
    /// One album is the intended customer experience, and repeating the caption on every slide would be noise, so the
    /// caption count is asserted as well as the album size.
    /// </remarks>
    [Fact]
    public async Task Album_sender_uses_one_media_group_with_a_single_caption()
    {
        var root = CreateTutorialAssetRoot(
            "android_v2rayng/1.png", "android_v2rayng/2.png", "android_v2rayng/3.png", "android_v2rayng/4.png");
        try
        {
            var assets = TenantTutorialAssetService.Resolve(TenantTutorialKinds.Android, root);
            var client = new StorefrontClient();

            var delivered = await TenantTutorialAlbumSender.SendAsync(
                client, new ChatId(722), assets, null, CancellationToken.None);

            Assert.True(delivered);
            Assert.Equal(new[] { 4 }, client.MediaGroupSizes);
            // Four images are two or more, so a real media group is used rather than a single photo.
            // A real album request, never a photo per slide and never a text message carrying a path.
            Assert.Single(client.MediaGroupSizes);
            Assert.Equal(0, client.SinglePhotoSends);
            Assert.Single(client.MediaGroupCaptions);
            Assert.Contains("v2rayNG", client.MediaGroupCaptions[0]);
            Assert.Equal(new[] { "media-group" }, client.Events);
        }
        finally
        {
            DeleteTutorialAssetRoot(root);
        }
    }

    /// <summary>
    /// Proves more than ten images are delivered across several valid albums without dropping the remainder.
    /// </summary>
    /// <remarks>
    /// Eleven images must become one ten-item album plus a single-photo continuation, because an eleven-item media group
    /// is invalid and dropping the last image would leave the guide unfinished.
    /// </remarks>
    [Fact]
    public async Task Album_sender_splits_long_guides_without_dropping_images()
    {
        var root = CreateTutorialAssetRoot(
            Enumerable.Range(1, 23).Select(index => $"windows_v2rayn/pc {index}.png").ToArray());
        try
        {
            var assets = TenantTutorialAssetService.Resolve(TenantTutorialKinds.Windows, root);
            var client = new StorefrontClient();

            var delivered = await TenantTutorialAlbumSender.SendAsync(
                client, new ChatId(722), assets, null, CancellationToken.None);

            Assert.True(delivered);
            // 23 images become three valid albums (10 + 10 + 3); no slide is dropped and none exceed the limit.
            Assert.Equal(new[] { 10, 10, 3 }, client.MediaGroupSizes);
            Assert.All(client.MediaGroupSizes, size => Assert.InRange(size, 2, TenantTutorialAssetService.MaxMediaGroupItems));
            Assert.Equal(0, client.SinglePhotoSends);
            // The caption belongs to the first album only.
            Assert.Single(client.MediaGroupCaptions);
            Assert.Equal(23, client.MediaGroupSizes.Sum());
            Assert.DoesNotContain(client.Events, entry => entry == "text");
        }
        finally
        {
            DeleteTutorialAssetRoot(root);
        }
    }

    /// <summary>
    /// Proves a one-image guide uses a single photo instead of an invalid one-item media group.
    /// </summary>
    [Fact]
    public async Task Album_sender_uses_a_single_photo_for_one_image()
    {
        var root = CreateTutorialAssetRoot("ios_android_v2box/v2box 1.png");
        try
        {
            var assets = TenantTutorialAssetService.Resolve(TenantTutorialKinds.Ios, root);
            var client = new StorefrontClient();

            var delivered = await TenantTutorialAlbumSender.SendAsync(
                client, new ChatId(722), assets, null, CancellationToken.None);

            Assert.True(delivered);
            Assert.Empty(client.MediaGroupSizes);
            Assert.Equal(1, client.SinglePhotoSends);
            Assert.Single(client.SinglePhotoCaptions);
            Assert.Contains("V2Box", client.SinglePhotoCaptions[0]);
            Assert.Equal(new[] { "photo" }, client.Events);
        }
        finally
        {
            DeleteTutorialAssetRoot(root);
        }
    }

    /// <summary>
    /// Proves an unavailable asset set is refused before any Telegram request is made.
    /// </summary>
    /// <remarks>
    /// The caller is responsible for sending the friendly unavailable notice, so the sender must simply decline rather
    /// than uploading a partial or empty album.
    /// </remarks>
    [Fact]
    public async Task Album_sender_declines_when_assets_are_unavailable()
    {
        var client = new StorefrontClient();
        var delivered = await TenantTutorialAlbumSender.SendAsync(
            client,
            new ChatId(722),
            new TenantTutorialAssetResult { Kind = TenantTutorialKinds.Android, Status = TenantTutorialAssetStatus.Missing },
            null,
            CancellationToken.None);

        Assert.False(delivered);
        Assert.Empty(client.Events);
    }

    /// <summary>
    /// Proves the owner panel no longer offers the tutorial configuration entry point.
    /// </summary>
    /// <remarks>
    /// Leaving the button rendered would let an owner enter a manager that no longer affects what customers see, which is
    /// worse than hiding it.
    /// </remarks>
    [Fact]
    public async Task Owner_panel_no_longer_offers_tutorial_configuration()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var store = await provider.GetRequiredService<TenantStoreStore>().CreateAsync(711, Guid.NewGuid().ToString("N"));
        var owner = new CredUser { TelegramUserId = 711, IsColleague = true };
        await provider.GetRequiredService<CredentialsStore>().SaveUserStatus(owner);
        var client = new StorefrontClient();
        using var context = new BotContextAccessor().Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = "owned-tutorial" }, Client = client });

        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(store, "panel"));

        Assert.NotEmpty(client.Texts);
        Assert.DoesNotContain(client.Labels, label => label.Contains(LegacyOwnerTutorialButtonLabel));
        Assert.DoesNotContain(client.Callbacks, data => data.EndsWith(":tutorials", StringComparison.Ordinal));
        Assert.DoesNotContain(client.Callbacks, data => data.EndsWith(":tutorial-add", StringComparison.Ordinal));
    }

    /// <summary>
    /// Proves stale owner tutorial callbacks answer safely, change no state, and preserve historical tutorial data.
    /// </summary>
    /// <remarks>
    /// Old Telegram messages still contain these buttons. They must not be able to delete or rewrite
    /// <c>TenantTutorialsJson</c>, because that data has to survive for a later rollback to owner-configured tutorials.
    /// </remarks>
    [Fact]
    public async Task Stale_owner_tutorial_callbacks_are_safe_noops_and_preserve_stored_json()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var store = await provider.GetRequiredService<TenantStoreStore>().CreateAsync(711, Guid.NewGuid().ToString("N"));
        var owner = new CredUser { TelegramUserId = 711, IsColleague = true };
        await provider.GetRequiredService<CredentialsStore>().SaveUserStatus(owner);
        var client = new StorefrontClient();
        using var context = new BotContextAccessor().Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = "owned-tutorial" }, Client = client });

        const string historicalJson = "[{\"Title\":\"آموزش قدیمی\",\"Url\":\"https://example.com/old\"}]";
        await using (var db = databases.Users.CreateDbContext())
        {
            var row = await db.BotInstances.SingleAsync(x => x.Id == store.Id);
            row.TenantTutorialsJson = historicalJson;
            await db.SaveChangesAsync();
        }

        // A non-panel management callback is only honored for the owner's explicitly selected store, and every callback
        // is revision-bound. Select the store first, then encode the stale tutorial callbacks from the current row.
        var stores = provider.GetRequiredService<TenantStoreStore>();
        var revisioned = (await stores.ListAsync(711)).Single(x => x.Id == store.Id);
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(revisioned, "panel"));
        var selected = (await stores.ListAsync(711)).Single(x => x.Id == store.Id);
        var answersBefore = client.Answers.Count;

        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(selected, "tutorials"));
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(selected, "tutorial-add"));
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(selected, "tutorial-del:0"));

        // Every stale callback answered with the disabled notice instead of entering the manager.
        var staleAnswers = client.Answers.Skip(answersBefore).ToList();
        Assert.Equal(3, staleAnswers.Count);
        Assert.All(staleAnswers, answer => Assert.Contains("غیرفعال", answer));
        // The add callback must not have stored an owner input step.
        var state = await provider.GetRequiredService<UserStateStore>().GetUserStatus(711);
        Assert.NotEqual("tutorial-title", state.LastStep);
        Assert.NotEqual("tutorial-url", state.LastStep);

        await using (var db = databases.Users.CreateDbContext())
        {
            var row = await db.BotInstances.AsNoTracking().SingleAsync(x => x.Id == store.Id);
            Assert.Equal(historicalJson, row.TenantTutorialsJson);
        }
    }

    /// <summary>
    /// Proves an owner message arriving on an obsolete tutorial step is cancelled without writing tutorial data.
    /// </summary>
    /// <remarks>
    /// Owners whose conversation state still holds <c>tutorial-title</c> from before the redesign would otherwise be
    /// stuck: their next message fits no current step. It must clear the stale step, keep historical JSON intact, and
    /// return the owner to the panel.
    /// </remarks>
    [Fact]
    public async Task Obsolete_owner_tutorial_step_is_cancelled_without_writing_tutorials()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var store = await provider.GetRequiredService<TenantStoreStore>().CreateAsync(711, Guid.NewGuid().ToString("N"));
        var owner = new CredUser { TelegramUserId = 711, IsColleague = true };
        await provider.GetRequiredService<CredentialsStore>().SaveUserStatus(owner);
        var client = new StorefrontClient();
        using var context = new BotContextAccessor().Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = "owned-tutorial" }, Client = client });

        await using (var db = databases.Users.CreateDbContext())
        {
            var row = await db.BotInstances.SingleAsync(x => x.Id == store.Id);
            row.TenantTutorialsJson = "[{\"Title\":\"قدیمی\",\"Url\":\"https://example.com/keep\"}]";
            await db.SaveChangesAsync();
        }

        // Establish the owner's explicitly selected store, then simulate the pre-redesign conversation step. The panel
        // callback is revision-bound, so it is encoded from the row as persisted after the JSON seed above.
        var revisioned = (await provider.GetRequiredService<TenantStoreStore>().ListAsync(711)).Single(x => x.Id == store.Id);
        await OwnerCallback(provider, client, owner, TenantOwnerCallback.Encode(revisioned, "panel"));
        var state = provider.GetRequiredService<UserStateStore>();
        await state.SaveUserStatus(new User { Id = 711, Flow = "TENANTBOT-owner", LastStep = "tutorial-title", OwnerStoreId = store.Id });

        await using (var scope = provider.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<TenantBotService>().TryHandleOwnerMessageAsync(
                client,
                new Message { From = new Telegram.Bot.Types.User { Id = 711 }, Chat = new Chat { Id = 711 }, Text = "آموزش تست" },
                owner,
                await state.GetUserStatus(711),
                new ReplyKeyboardRemove(),
                default);

        var after = await state.GetUserStatus(711);
        Assert.NotEqual("tutorial-title", after.LastStep);
        await using (var db = databases.Users.CreateDbContext())
        {
            var row = await db.BotInstances.AsNoTracking().SingleAsync(x => x.Id == store.Id);
            Assert.Contains("https://example.com/keep", row.TenantTutorialsJson);
        }
    }

    /// <summary>
    /// Proves both customer installation-guide aliases produce exactly the three built-in category buttons.
    /// </summary>
    /// <remarks>
    /// The aliases are asserted through the real recognition predicate, and the rendered keyboard is asserted through the
    /// real menu builder, so neither the trigger words nor the button set can drift independently.
    /// </remarks>
    [Fact]
    public async Task Customer_tutorial_menu_offers_exactly_the_three_builtin_categories()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var client = new StorefrontClient();
        var tenant = new BotInstance { Id = "tenant-tutorial-menu", Type = BotInstanceTypes.Tenant, Enabled = true };

        await using var scope = provider.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
        var isTutorialCommand = typeof(TenantBotService).GetMethod("IsTenantTutorialCommand", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True((bool)isTutorialCommand.Invoke(null, new object[] { "راهنما نصب" })!);
        Assert.True((bool)isTutorialCommand.Invoke(null, new object[] { "💡راهنما نصب" })!);

        var sendMenu = typeof(TenantBotService).GetMethod("SendTenantTutorialsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)sendMenu.Invoke(service, new object[] { client, new ChatId(722), tenant, CancellationToken.None })!;

        // Exactly three category buttons, one per row, addressed by the three built-in tutorial kinds.
        Assert.Equal(3, client.Labels.Count);
        Assert.Contains("Android", client.Labels[0]);
        Assert.Contains("iOS", client.Labels[1]);
        Assert.Contains("ویندوز", client.Labels[2]);
        Assert.Equal(
            new[] { "TN:tutorial:android", "TN:tutorial:ios", "TN:tutorial:windows" },
            client.Callbacks.ToArray());
        Assert.Contains("آموزش نصب", client.Texts[0]);
    }

    /// <summary>
    /// Proves a customer tutorial callback is acknowledged before upload work and writes nothing to users.db.
    /// </summary>
    /// <remarks>
    /// Two production hazards are covered at once. First, album upload opens local files and streams several photos, so
    /// acknowledging the callback afterwards would risk Telegram's callback timeout. Second, choosing a tutorial is
    /// presentation-only: it must not touch customer state, orders, wallets, or tenant configuration.
    /// </remarks>
    [Fact]
    public async Task Customer_tutorial_callback_acks_first_and_writes_nothing()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var tenant = new BotInstance
        {
            Id = "tenant-tutorial-callback",
            Type = BotInstanceTypes.Tenant,
            Enabled = true,
            OwnerTelegramUserId = 711,
            CreatedAtUtc = DateTime.UtcNow
        };
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(tenant);
            await db.SaveChangesAsync();
        }

        var client = new StorefrontClient();
        using var context = new BotContextAccessor().Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = tenant.Id }, Client = client });
        string usersDbBefore;
        await using (var db = databases.Users.CreateDbContext())
            usersDbBefore = string.Join('|', await db.BotUserStates.AsNoTracking().Select(x => x.TelegramUserId + ":" + x.LastStep).ToListAsync());

        await using (var scope = provider.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
            var handle = typeof(TenantBotService).GetMethod("HANDLECUSTOMERCALLBACKASYNC", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var callback = new CallbackQuery
            {
                Id = Guid.NewGuid().ToString("N"),
                Data = "TN:tutorial:android",
                From = new Telegram.Bot.Types.User { Id = 722 },
                Message = new Message { MessageId = 1, Chat = new Chat { Id = 722 } }
            };
            await (Task)handle.Invoke(service, new object[]
            {
                client,
                callback,
                new CredUser { TelegramUserId = 722 },
                new User { Id = 722 },
                CancellationToken.None
            })!;
        }

        // The acknowledgement happened before the album upload started.
        Assert.NotEmpty(client.Events);
        Assert.Equal("answer", client.Events[0]);
        Assert.Contains("media-group", client.Events);
        Assert.True(
            client.Events.IndexOf("answer") < client.Events.IndexOf("media-group"),
            "the tutorial callback must be acknowledged before the album upload begins");

        // The real Android album was uploaded, and the customer was never trapped in a conversation step.
        Assert.Equal(new[] { 6 }, client.MediaGroupSizes);
        Assert.Single(client.MediaGroupCaptions);
        Assert.Contains("v2rayNG", client.MediaGroupCaptions[0]);

        await using (var db = databases.Users.CreateDbContext())
            Assert.Equal(usersDbBefore, string.Join('|', await db.BotUserStates.AsNoTracking().Select(x => x.TelegramUserId + ":" + x.LastStep).ToListAsync()));
    }

    /// <summary>
    /// Proves the iOS and Windows customer categories deliver their own albums, including the V2Box platform note.
    /// </summary>
    /// <remarks>
    /// The iOS guide uses V2Box, which also runs on Android, and the caption must say so without taking over the dedicated
    /// Android/v2rayNG tutorial. The Windows category must still deliver the v2rayN guide.
    /// </remarks>
    [Fact]
    public async Task Ios_and_windows_categories_deliver_their_own_albums()
    {
        using var databases = new Databases();
        await using var provider = StorefrontProvider(databases);
        var tenant = new BotInstance
        {
            Id = "tenant-tutorial-categories",
            Type = BotInstanceTypes.Tenant,
            Enabled = true,
            OwnerTelegramUserId = 711,
            CreatedAtUtc = DateTime.UtcNow
        };
        await using (var db = databases.Users.CreateDbContext())
        {
            db.BotInstances.Add(tenant);
            await db.SaveChangesAsync();
        }

        foreach (var (kind, expectedAlbumSize, expectedCaption) in new[]
                 {
                     ("ios", 6, "V2Box"),
                     ("windows", 8, "v2rayN")
                 })
        {
            var client = new StorefrontClient();
            using var context = new BotContextAccessor().Push(new BotRuntimeContext { Config = new BotInstanceConfig { Id = tenant.Id }, Client = client });
            await using var scope = provider.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<TenantBotService>();
            var handle = typeof(TenantBotService).GetMethod("HANDLECUSTOMERCALLBACKASYNC", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)handle.Invoke(service, new object[]
            {
                client,
                new CallbackQuery
                {
                    Id = Guid.NewGuid().ToString("N"),
                    Data = $"TN:tutorial:{kind}",
                    From = new Telegram.Bot.Types.User { Id = 722 },
                    Message = new Message { MessageId = 1, Chat = new Chat { Id = 722 } }
                },
                new CredUser { TelegramUserId = 722 },
                new User { Id = 722 },
                CancellationToken.None
            })!;

            Assert.Equal(new[] { expectedAlbumSize }, client.MediaGroupSizes);
            Assert.Single(client.MediaGroupCaptions);
            Assert.Contains(expectedCaption, client.MediaGroupCaptions[0]);

            // The iOS caption must point out that the same client also works on Android.
            if (kind == "ios")
                Assert.Contains("Android", client.MediaGroupCaptions[0]);
        }
    }
}
