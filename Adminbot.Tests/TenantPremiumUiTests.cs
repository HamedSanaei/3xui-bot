using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Adminbot.Domain;
using Adminbot.Domain.TelegramUi;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Telegram.Bot.Types;
using Xunit;
using TelegramUser = Telegram.Bot.Types.User;

/// <summary>
/// End-to-end regression coverage for the storefront premium appearance toggle inside the tenant owner panel.
/// </summary>
/// <remarks>
/// <para>
/// The toggle is the only customer-visible part of this phase, so its authorization order is asserted explicitly: the
/// addressed storefront and its persisted owner are reloaded first, the owner's Telegram Premium status is read second,
/// and the storefront's own bot token proves the capability third. A failure at any step must leave the persisted
/// preference untouched.
/// </para>
/// <para>
/// Every test uses the real production service graph against isolated temporary SQLite databases. The only substitution
/// is the capability probe, so no Telegram connection and no real storefront token is ever used.
/// </para>
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>Records probe requests and returns a scripted status.</summary>
    private sealed class RecordingProbe : ITelegramPremiumUiCapabilityProbe
    {
        /// <summary>Requests the owner flow attempted to probe.</summary>
        public List<TelegramPremiumUiCapabilityProbeRequest> Requests { get; } = new();

        /// <summary>Status returned for every probe attempt.</summary>
        public TelegramPremiumUiProbeStatus Status { get; set; } = TelegramPremiumUiProbeStatus.Supported;

        /// <inheritdoc />
        public Task<TelegramPremiumUiProbeResult> ProbeAsync(
            TelegramPremiumUiCapabilityProbeRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new TelegramPremiumUiProbeResult(Status, Status.ToString()));
        }
    }

    /// <summary>Builds the production graph against isolated databases with a scripted capability probe.</summary>
    /// <param name="databases">Fixture that owns the database paths and cleanup.</param>
    /// <param name="probe">Scripted capability probe substituted for the production probe.</param>
    /// <returns>A provider the test must dispose.</returns>
    private static ServiceProvider PremiumStorefrontProvider(Databases databases, ITelegramPremiumUiCapabilityProbe probe)
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/configuration.example.json")))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["bots:0:enabled"] = "false",
                ["bots:0:token"] = "12345:" + new string('a', 35)
            }).Build();
        var config = configuration.Get<AppConfig>()!;
        config.UserDatabasePath = Path.Combine(databases.DirectoryPath, "users.db");
        config.CredentialsDatabasePath = Path.Combine(databases.DirectoryPath, "credentials.db");
        var services = new ServiceCollection();
        Program.RegisterApplicationServices(services, configuration, config, databases.DirectoryPath);
        services.AddSingleton(probe);
        return services.BuildServiceProvider();
    }

    /// <summary>Executes one owner callback in a fresh scope with an explicit Telegram Premium flag.</summary>
    /// <param name="provider">Test provider.</param>
    /// <param name="client">Recording Telegram transport.</param>
    /// <param name="owner">Authenticated colleague owner.</param>
    /// <param name="data">Untrusted management callback under test.</param>
    /// <param name="isPremium">Telegram Premium flag reported for the sender.</param>
    /// <returns>A task completing after the handler has run.</returns>
    private static async Task OwnerCallbackWithPremium(
        ServiceProvider provider,
        StorefrontClient client,
        CredUser owner,
        string data,
        bool isPremium)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<TenantBotService>().TryHandleOwnerCallbackAsync(
            client,
            new CallbackQuery
            {
                Id = Guid.NewGuid().ToString("N"),
                Data = data,
                From = new TelegramUser { Id = owner.TelegramUserId, IsPremium = isPremium },
                Message = new Message { Id = 1, Chat = new Chat { Id = owner.TelegramUserId } }
            },
            owner,
            await provider.GetRequiredService<UserStateStore>().GetUserStatus(owner.TelegramUserId),
            default);
    }

    /// <summary>Finds the addressed premium toggle button produced by the owner panel.</summary>
    /// <param name="client">Recording Telegram transport.</param>
    /// <param name="expectedEnabled">Target state encoded in the button.</param>
    /// <returns>The signed callback payload, or <c>null</c> when the panel did not offer that target state.</returns>
    private static string? FindPremiumToggleCallback(StorefrontClient client, bool expectedEnabled)
    {
        foreach (var data in client.Callbacks)
        {
            if (!TenantOwnerCallback.TryDecode(data, out _, out _, out var action))
                continue;
            if (action == null || !action.StartsWith("set-setting:premium:", StringComparison.Ordinal))
                continue;

            var parts = action.Split(':');
            if (parts.Length >= 3 && parts[2] == (expectedEnabled ? "1" : "0"))
                return data;
        }

        return null;
    }

    /// <summary>Creates one storefront through the production allocator.</summary>
    /// <param name="databases">Database fixture.</param>
    /// <returns>The persisted storefront row.</returns>
    private static Task<BotInstance> CreateStoreAsync(Databases databases)
        => new TenantStoreStore(databases.Users, new ConfigurationBuilder().Build())
            .CreateAsync(711, Guid.NewGuid().ToString("N"));

    /// <summary>Reads the current storefront row used to build revision-bound callbacks.</summary>
    /// <param name="databases">Database fixture.</param>
    /// <param name="storeId">Storefront id.</param>
    /// <returns>The detached current row.</returns>
    private static async Task<BotInstance> ReadStoreRowAsync(Databases databases, string storeId)
    {
        await using var db = databases.Users.CreateDbContext();
        return await db.BotInstances.AsNoTracking().SingleAsync(x => x.Id == storeId);
    }

    /// <summary>Reads the persisted premium preference of one storefront.</summary>
    /// <param name="databases">Database fixture.</param>
    /// <param name="storeId">Storefront id.</param>
    /// <returns>The persisted preference.</returns>
    private static async Task<bool> ReadPreferenceAsync(Databases databases, string storeId)
        => (await ReadStoreRowAsync(databases, storeId)).TenantPremiumUiEnabled;

    /// <summary>Registers the owner profile used by the panel flow.</summary>
    /// <param name="provider">Test provider.</param>
    /// <param name="telegramUserId">Colleague owner Telegram user id.</param>
    /// <returns>A task completing after the profile is stored.</returns>
    private static async Task<CredUser> RegisterOwnerAsync(ServiceProvider provider, long telegramUserId = 711)
    {
        var owner = new CredUser { TelegramUserId = telegramUserId, IsColleague = true };
        await provider.GetRequiredService<CredentialsStore>().SaveUserStatus(owner);
        return owner;
    }

    /// <summary>The owner panel shows the disabled state and offers an addressed enable button.</summary>
    [Fact]
    public async Task Owner_panel_offers_an_addressed_premium_toggle()
    {
        using var databases = new Databases();
        var probe = new RecordingProbe();
        await using var provider = PremiumStorefrontProvider(databases, probe);
        var store = await CreateStoreAsync(databases);
        var owner = await RegisterOwnerAsync(provider);
        var client = new StorefrontClient();

        await OwnerCallbackWithPremium(provider, client, owner, TenantOwnerCallback.Encode(store, "panel"), true);

        Assert.NotNull(FindPremiumToggleCallback(client, expectedEnabled: true));
        Assert.Null(FindPremiumToggleCallback(client, expectedEnabled: false));
        Assert.Contains(client.Texts, x => x.Contains("ظاهر پریمیوم فروشگاه", StringComparison.Ordinal));
        Assert.DoesNotContain("tenant-", client.Texts.Last(), StringComparison.OrdinalIgnoreCase);
        Assert.All(client.Callbacks, x => Assert.InRange(Encoding.UTF8.GetByteCount(x), 1, 64));
        Assert.Empty(probe.Requests);
    }

    /// <summary>A non-Premium owner is refused before any probe and the preference stays false.</summary>
    /// <remarks>
    /// This is the central fail-closed rule of the feature: without a Premium owner account no capability probe is sent
    /// and no storefront is opted in.
    /// </remarks>
    [Fact]
    public async Task Non_premium_owner_cannot_enable_and_sends_no_probe()
    {
        using var databases = new Databases();
        var probe = new RecordingProbe();
        await using var provider = PremiumStorefrontProvider(databases, probe);
        var store = await CreateStoreAsync(databases);
        var owner = await RegisterOwnerAsync(provider);
        var client = new StorefrontClient();

        await OwnerCallbackWithPremium(provider, client, owner, TenantOwnerCallback.Encode(store, "panel"), false);
        var enable = FindPremiumToggleCallback(client, expectedEnabled: true);
        Assert.NotNull(enable);
        await OwnerCallbackWithPremium(provider, client, owner, enable!, false);

        Assert.Empty(probe.Requests);
        Assert.False(await ReadPreferenceAsync(databases, store.Id));
        Assert.Contains(client.Texts, x => x.Contains("Telegram Premium", StringComparison.Ordinal));
    }

    /// <summary>A Premium owner is opted in only after the storefront bot proves the capability.</summary>
    [Fact]
    public async Task Premium_owner_enable_requires_a_successful_probe()
    {
        using var databases = new Databases();
        var probe = new RecordingProbe { Status = TelegramPremiumUiProbeStatus.Supported };
        await using var provider = PremiumStorefrontProvider(databases, probe);
        var store = await CreateStoreAsync(databases);
        var owner = await RegisterOwnerAsync(provider);
        var client = new StorefrontClient();

        await OwnerCallbackWithPremium(provider, client, owner, TenantOwnerCallback.Encode(store, "panel"), true);
        var enable = FindPremiumToggleCallback(client, expectedEnabled: true);
        Assert.NotNull(enable);
        await OwnerCallbackWithPremium(provider, client, owner, enable!, true);

        var request = Assert.Single(probe.Requests);
        Assert.Equal(store.Id, request.BotId);
        Assert.Equal(711, request.OwnerChatId);
        Assert.True(request.OwnerIsPremium);
        Assert.True(await ReadPreferenceAsync(databases, store.Id));
        Assert.Contains(client.Texts, x => x.Contains("ظاهر پریمیوم برای این فروشگاه فعال شد", StringComparison.Ordinal));
    }

    /// <summary>Every non-supported probe status leaves the storefront disabled with a safe message.</summary>
    /// <param name="status">Probe status returned by the scripted probe.</param>
    /// <param name="marker">Substring expected in the owner-visible message.</param>
    [Theory]
    [InlineData(TelegramPremiumUiProbeStatus.Ambiguous, "هیچ تغییری اعمال نشد")]
    [InlineData(TelegramPremiumUiProbeStatus.TransientFailure, "هیچ تغییری اعمال نشد")]
    [InlineData(TelegramPremiumUiProbeStatus.TransportUnavailable, "هیچ تغییری اعمال نشد")]
    [InlineData(TelegramPremiumUiProbeStatus.CatalogUnavailable, "کاتالوگ اموجی پریمیوم")]
    [InlineData(TelegramPremiumUiProbeStatus.Rejected, "تأیید نکرد")]
    [InlineData(TelegramPremiumUiProbeStatus.BaselineRejected, "تأیید نکرد")]
    [InlineData(TelegramPremiumUiProbeStatus.PremiumRequired, "Telegram Premium")]
    public async Task Unproven_probe_status_keeps_the_storefront_disabled(
        TelegramPremiumUiProbeStatus status,
        string marker)
    {
        using var databases = new Databases();
        var probe = new RecordingProbe { Status = status };
        await using var provider = PremiumStorefrontProvider(databases, probe);
        var store = await CreateStoreAsync(databases);
        var owner = await RegisterOwnerAsync(provider);
        var client = new StorefrontClient();

        await OwnerCallbackWithPremium(provider, client, owner, TenantOwnerCallback.Encode(store, "panel"), true);
        var enable = FindPremiumToggleCallback(client, expectedEnabled: true);
        Assert.NotNull(enable);
        await OwnerCallbackWithPremium(provider, client, owner, enable!, true);

        Assert.False(await ReadPreferenceAsync(databases, store.Id));
        Assert.Contains(client.Texts, x => x.Contains(marker, StringComparison.Ordinal));
    }

    /// <summary>Disabling needs no Telegram Premium status and no capability probe.</summary>
    /// <remarks>
    /// This is the path that must keep working after an owner loses Premium or after Telegram stops accepting decoration.
    /// </remarks>
    [Fact]
    public async Task Disable_requires_no_premium_and_no_probe()
    {
        using var databases = new Databases();
        var probe = new RecordingProbe { Status = TelegramPremiumUiProbeStatus.Ambiguous };
        await using var provider = PremiumStorefrontProvider(databases, probe);
        var store = await CreateStoreAsync(databases);
        await using (var db = databases.Users.CreateDbContext())
        {
            var row = await db.BotInstances.SingleAsync(x => x.Id == store.Id);
            row.TenantPremiumUiEnabled = true;
            row.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        var owner = await RegisterOwnerAsync(provider);
        var client = new StorefrontClient();

        var current = await ReadStoreRowAsync(databases, store.Id);
        await OwnerCallbackWithPremium(provider, client, owner, TenantOwnerCallback.Encode(current, "panel"), false);
        var disable = FindPremiumToggleCallback(client, expectedEnabled: false);
        Assert.NotNull(disable);
        await OwnerCallbackWithPremium(provider, client, owner, disable!, false);

        Assert.Empty(probe.Requests);
        Assert.False(await ReadPreferenceAsync(databases, store.Id));
        Assert.Contains(client.Texts, x => x.Contains("غیرفعال شد", StringComparison.Ordinal));
    }

    /// <summary>A redelivered enable callback cannot produce a second provider decision or a second probe.</summary>
    /// <remarks>
    /// The panel revision is the exactly-once guard: the first press persists the change and advances the revision, so the
    /// redelivered envelope is refused as stale instead of running a second probe.
    /// </remarks>
    [Fact]
    public async Task Redelivered_enable_callback_is_stale_and_probes_once()
    {
        using var databases = new Databases();
        var probe = new RecordingProbe { Status = TelegramPremiumUiProbeStatus.Supported };
        await using var provider = PremiumStorefrontProvider(databases, probe);
        var store = await CreateStoreAsync(databases);
        var owner = await RegisterOwnerAsync(provider);
        var client = new StorefrontClient();

        await OwnerCallbackWithPremium(provider, client, owner, TenantOwnerCallback.Encode(store, "panel"), true);
        var enable = FindPremiumToggleCallback(client, expectedEnabled: true);
        Assert.NotNull(enable);
        await OwnerCallbackWithPremium(provider, client, owner, enable!, true);
        await OwnerCallbackWithPremium(provider, client, owner, enable!, true);

        Assert.Single(probe.Requests);
        Assert.True(await ReadPreferenceAsync(databases, store.Id));
    }

    /// <summary>A tampered or stale revision never changes the persisted preference.</summary>
    [Fact]
    public async Task Stale_revision_cannot_change_the_preference()
    {
        using var databases = new Databases();
        var probe = new RecordingProbe { Status = TelegramPremiumUiProbeStatus.Supported };
        await using var provider = PremiumStorefrontProvider(databases, probe);
        var store = await CreateStoreAsync(databases);
        var owner = await RegisterOwnerAsync(provider);
        var client = new StorefrontClient();

        await OwnerCallbackWithPremium(provider, client, owner, TenantOwnerCallback.Encode(store, "panel"), true);

        var current = await ReadStoreRowAsync(databases, store.Id);
        var issued = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 300 * 300).ToString("X", CultureInfo.InvariantCulture);
        var bogusRevision = ((current.UpdatedAtUtc ?? current.CreatedAtUtc).Ticks + 1).ToString("X", CultureInfo.InvariantCulture);
        var tampered = $"TBM:{current.TenantStoreNumber:X}:{bogusRevision}:{issued}:s:premium:1";
        await OwnerCallbackWithPremium(provider, client, owner, tampered, true);

        Assert.Empty(probe.Requests);
        Assert.False(await ReadPreferenceAsync(databases, store.Id));
    }

    /// <summary>A malformed callback never mutates anything and shows the fresh store list.</summary>
    /// <param name="data">Untrusted callback payload.</param>
    [Theory]
    [InlineData("TBM:set-setting:premium:1:0:0")]
    [InlineData("TBM:not-a-number:0:0:s:premium:1")]
    [InlineData("PUI:preview")]
    [InlineData("")]
    public async Task Malformed_owner_callbacks_are_inert(string data)
    {
        using var databases = new Databases();
        var probe = new RecordingProbe();
        await using var provider = PremiumStorefrontProvider(databases, probe);
        var store = await CreateStoreAsync(databases);
        var owner = await RegisterOwnerAsync(provider);
        var client = new StorefrontClient();

        await OwnerCallbackWithPremium(provider, client, owner, data, true);

        Assert.Empty(probe.Requests);
        Assert.False(await ReadPreferenceAsync(databases, store.Id));
    }

    /// <summary>Another colleague can never toggle a storefront they do not own.</summary>
    [Fact]
    public async Task Foreign_owner_callback_cannot_enable_a_storefront()
    {
        using var databases = new Databases();
        var probe = new RecordingProbe { Status = TelegramPremiumUiProbeStatus.Supported };
        await using var provider = PremiumStorefrontProvider(databases, probe);
        var store = await CreateStoreAsync(databases);
        var owner = await RegisterOwnerAsync(provider, 711);
        var intruder = await RegisterOwnerAsync(provider, 999);
        var client = new StorefrontClient();

        await OwnerCallbackWithPremium(provider, client, intruder, TenantOwnerCallback.Encode(store, "panel"), true);

        Assert.Empty(probe.Requests);
        Assert.False(await ReadPreferenceAsync(databases, store.Id));
        Assert.NotNull(owner);
    }

    /// <summary>The premium preference is independent per storefront of the same owner.</summary>
    [Fact]
    public async Task Premium_preference_is_isolated_per_storefront()
    {
        using var databases = new Databases();
        var probe = new RecordingProbe { Status = TelegramPremiumUiProbeStatus.Supported };
        await using var provider = PremiumStorefrontProvider(databases, probe);
        var first = await CreateStoreAsync(databases);
        var second = await CreateStoreAsync(databases);
        var owner = await RegisterOwnerAsync(provider);
        var client = new StorefrontClient();

        await OwnerCallbackWithPremium(provider, client, owner, TenantOwnerCallback.Encode(first, "panel"), true);
        var enable = FindPremiumToggleCallback(client, expectedEnabled: true);
        Assert.NotNull(enable);
        await OwnerCallbackWithPremium(provider, client, owner, enable!, true);

        Assert.True(await ReadPreferenceAsync(databases, first.Id));
        Assert.False(await ReadPreferenceAsync(databases, second.Id));
    }

    /// <summary>The panel reflects persisted state and never renders an internal storefront identifier.</summary>
    [Fact]
    public async Task Owner_panel_reflects_persisted_state_without_internal_identifiers()
    {
        using var databases = new Databases();
        var probe = new RecordingProbe();
        await using var provider = PremiumStorefrontProvider(databases, probe);
        var store = await CreateStoreAsync(databases);
        await using (var db = databases.Users.CreateDbContext())
        {
            var row = await db.BotInstances.SingleAsync(x => x.Id == store.Id);
            row.TenantPremiumUiEnabled = true;
            row.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        var owner = await RegisterOwnerAsync(provider);
        var client = new StorefrontClient();

        var current = await ReadStoreRowAsync(databases, store.Id);
        await OwnerCallbackWithPremium(provider, client, owner, TenantOwnerCallback.Encode(current, "panel"), true);

        Assert.Contains(client.Texts, x => x.Contains("ظاهر پریمیوم فروشگاه", StringComparison.Ordinal) && x.Contains("فعال", StringComparison.Ordinal));
        Assert.NotNull(FindPremiumToggleCallback(client, expectedEnabled: false));
        Assert.Null(FindPremiumToggleCallback(client, expectedEnabled: true));
    }
}
