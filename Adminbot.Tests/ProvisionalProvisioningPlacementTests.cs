using Adminbot.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Xunit;

/// <summary>
/// Regression coverage for provisioning a tenant client with caller-supplied commercial limits while reusing the plan
/// catalog's authoritative service-to-server/inbound placement.
/// </summary>
/// <remarks>
/// The provisional tenant card-to-card flow needs a 1 GB / 1 day courtesy account that is deliberately not a catalog
/// plan. The blocking rule is the configured MINIMUM TRAFFIC, not a missing duration key: <c>normal</c> requires at least
/// 10 GB, so <see cref="XuiV3PurchaseService.ResolveTenantPurchase" /> refuses a 1 GB selection before any panel call
/// even though <c>normal</c> does accept a custom <c>days-1</c> duration. These tests
/// pin the four properties that make the new entry point safe:
///
/// 1. Provisional placement can only be produced by
///    <see cref="XuiV3PurchaseService.ResolveTenantProvisionalPlacement" />, which first revalidates the order's
///    ORIGINAL selection through the tenant audience wrapper. A globally enabled catalog entry is never sufficient
///    authorization, and a plan the storefront can no longer sell yields no provisional account at all.
/// 2. Placement (inbound ids and service key) still comes from the one authoritative enabled-service lookup, so no
///    server/inbound mapping is duplicated outside <see cref="XuiV3PurchaseService"/>.
/// 3. The quota is derived only from the requested GB value, so "1 GB" always means exactly 1 GiB on the panel and a
///    conflicting byte override is refused instead of silently applied.
/// 4. The durable exactly-once creation boundary still applies, so a repeated provisional call issues no second panel
///    create.
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>One GiB in bytes, the byte quota the provisional "1 GB" promise must always produce.</summary>
    private const long OneGibBytes = 1073741824L;

    /// <summary>
    /// Proves the explicit-limit path provisions the requested 1 GB / 1 day client through catalog placement while the
    /// catalog pricing path rejects the very same selection.
    /// </summary>
    /// <remarks>
    /// The catalog-rejection assertion is the important one: it fails if someone later "simplifies" the provisional path
    /// by routing its 1 GB / 1 day selection through <see cref="XuiV3PurchaseService.ResolveTenantPurchase" />, which
    /// would make provisional delivery impossible. The panel assertions then prove the provisional quota, the provisional
    /// lifetime, and the configured inbound placement actually reached the wire request.
    /// </remarks>
    [Fact]
    public async Task Explicit_limit_provisioning_reuses_catalog_placement_without_pricing_validation()
    {
        using var databases = new Databases();
        var catalogPath = WriteTenantCatalog(databases, _ => { });
        await using var panel = await ProvisionalPanel.StartAsync();
        var purchaseService = BuildPurchaseService(databases, catalogPath, panel.Url);

        // The catalog path must reject the provisional shape. This is the regression that protects the whole design:
        // if this stops throwing, the explicit-limit entry point is no longer the only way to create it. The refusal
        // comes from the 10 GB service minimum; the `days-1` duration itself is expressible for `normal`.
        var provisionalSelection = new XuiV3PurchaseSelection
        {
            ServiceKey = "normal",
            TrafficGb = 1,
            DurationKey = "days-1",
            AccountCount = 1
        };
        Assert.Throws<InvalidOperationException>(() => purchaseService.ResolveTenantPurchase(provisionalSelection, false));
        // The very same duration key with sellable traffic proves the refusal above is about traffic, not duration.
        Assert.NotNull(purchaseService.ResolveTenantPurchase(new XuiV3PurchaseSelection
        {
            ServiceKey = "normal",
            TrafficGb = 50,
            DurationKey = "days-1",
            AccountCount = 1
        }, false));
        Assert.Equal(0, panel.PostCount);

        // The ORIGINAL ordered plan is what authorizes provisional placement, and it is still sellable here.
        var originalSelection = new XuiV3PurchaseSelection
        {
            ServiceKey = "normal",
            TrafficGb = 50,
            DurationKey = "m1",
            AccountCount = 1
        };
        var placement = purchaseService.ResolveTenantProvisionalPlacement(originalSelection);
        Assert.NotNull(placement.Service);
        Assert.Equal("normal", placement.Service.Key);
        var expectedInboundIds = XuiV3PurchaseService.ResolveServiceInboundIds(placement.Service).Order().ToArray();
        Assert.NotEmpty(expectedInboundIds);
        Assert.Equal(expectedInboundIds, placement.InboundIds.Order().ToArray());

        var user = new CredUser { TelegramUserId = 5150, IsColleague = false };
        var serverInfo = new ServerInfo { Url = panel.Url, ApiToken = "test-only" };
        const string operationKey = "tenant-card-provisional-create:9001";

        var created = await CreateProvisionalAsync(purchaseService, user, serverInfo, placement, operationKey);

        Assert.True(created.Success, created.Message);
        Assert.Equal(1, panel.PostCount);
        var postedClient = Assert.IsType<JObject>(panel.LastClient);

        // Exactly the provisional commercial limits must reach the panel request.
        Assert.Equal(OneGibBytes, postedClient["totalGB"]!.Value<long>());
        var expectedExpiry = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
        var postedExpiry = postedClient["expiryTime"]!.Value<long>();
        Assert.InRange(postedExpiry, expectedExpiry - TimeSpan.FromMinutes(5).TotalMilliseconds,
            expectedExpiry + TimeSpan.FromMinutes(5).TotalMilliseconds);

        // Placement must be the configured catalog placement, not an empty or invented inbound list.
        Assert.NotNull(panel.LastInboundIds);
        Assert.Equal(expectedInboundIds, panel.LastInboundIds!.Values<int>().Order().ToArray());

        // The panel comment must carry the provisional plan label so reminder and sync logic can recognise it.
        var comment = postedClient["comment"]!.Value<string>();
        Assert.Contains("provisional-1gb-1d", comment);
        Assert.Contains("normal", comment);

        await using (var db = databases.Users.CreateDbContext())
        {
            var reservation = await db.XuiV3CreationOperations.AsNoTracking()
                .SingleAsync(x => x.OperationKey == operationKey);
            // The reserved business parameters are the provisional ones, so a later full-order create cannot reuse this key.
            Assert.Contains("\"TrafficGb\":1", reservation.BusinessParametersJson);
            Assert.Contains("\"DurationDays\":1", reservation.BusinessParametersJson);
            Assert.Contains("provisional-1gb-1d", reservation.ClientJson);
        }

        // Exactly-once: repeating the same provisional operation key must never issue another panel create.
        var repeated = await CreateProvisionalAsync(purchaseService, user, serverInfo, placement, operationKey);
        Assert.True(repeated.Success, repeated.Message);
        Assert.Equal(1, panel.PostCount);
    }

    /// <summary>
    /// Proves provisional placement is refused, with no panel traffic at all, whenever the customer's ORIGINAL order is
    /// no longer tenant-eligible.
    /// </summary>
    /// <remarks>
    /// Each case represents a real way an order can become unsellable between purchase and receipt review: the service
    /// was switched off globally, the ordered traffic sits below the configured minimum, the ordered duration key was
    /// disabled, or an unlimited plan was hidden from tenant storefronts. In all four cases the storefront must not hand
    /// out free provisional access, so the guard throws before any XUI create.
    /// </remarks>
    [Fact]
    public async Task Provisional_placement_requires_the_original_ordered_plan_to_still_be_tenant_eligible()
    {
        using var databases = new Databases();
        // Disable the national service and hide the u2-m1 unlimited plan from tenant storefronts.
        var catalogPath = WriteTenantCatalog(databases, catalog =>
        {
            foreach (var service in catalog["services"]!)
            {
                if (string.Equals(service["key"]?.Value<string>(), "national", StringComparison.OrdinalIgnoreCase))
                    service["isEnabled"] = false;
                if (string.Equals(service["key"]?.Value<string>(), "unlimited", StringComparison.OrdinalIgnoreCase))
                    service["unlimitedPlans"]!.First(x => x["key"]!.Value<string>() == "u2-m1")["tenantVisible"] = false;
            }
        });
        await using var panel = await ProvisionalPanel.StartAsync();
        var purchaseService = BuildPurchaseService(databases, catalogPath, panel.Url);

        // The service is still a real catalog entry, but it is switched off, so the enabled-service lookup cannot resolve
        // it and provisional placement must refuse. This is why a globally enabled catalog entry alone is never treated
        // as tenant authorization.
        Assert.Contains("national", File.ReadAllText(catalogPath), StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => purchaseService.FindService("national"));
        Assert.Throws<InvalidOperationException>(() => purchaseService.ResolveTenantProvisionalPlacement(
            new XuiV3PurchaseSelection { ServiceKey = "national", TrafficGb = 5, DurationKey = "m1" }));

        // Ordered traffic below the configured minimum is no longer sellable.
        Assert.Throws<InvalidOperationException>(() => purchaseService.ResolveTenantProvisionalPlacement(
            new XuiV3PurchaseSelection { ServiceKey = "normal", TrafficGb = 1, DurationKey = "m1" }));

        // A duration key the catalog carries but has switched off is no longer sellable. `life` is disabled for normal.
        Assert.Throws<InvalidOperationException>(() => purchaseService.ResolveTenantProvisionalPlacement(
            new XuiV3PurchaseSelection { ServiceKey = "normal", TrafficGb = 50, DurationKey = "life" }));

        // A duration key the catalog does not define is not sellable either.
        Assert.Throws<InvalidOperationException>(() => purchaseService.ResolveTenantProvisionalPlacement(
            new XuiV3PurchaseSelection { ServiceKey = "normal", TrafficGb = 50, DurationKey = "m99" }));

        // An unlimited plan hidden from tenant storefronts is no longer sellable.
        Assert.Throws<InvalidOperationException>(() => purchaseService.ResolveTenantProvisionalPlacement(
            new XuiV3PurchaseSelection { ServiceKey = "unlimited", UnlimitedPlanKey = "u2-m1" }));

        // None of the four refusals may have created anything on the panel.
        Assert.Equal(0, panel.PostCount);
    }

    /// <summary>
    /// Pins the quota invariant: the provisional path derives its byte quota from the requested GB value only, and a
    /// contradictory byte override is refused instead of quietly changing the customer's promised quota.
    /// </summary>
    /// <remarks>
    /// 1 GiB is 1073741824 bytes. A mismatch here would either over-deliver or under-deliver against the customer-facing
    /// "1 GB" wording, so the conflict is rejected loudly and before any panel request.
    /// </remarks>
    [Fact]
    public async Task One_gb_is_exactly_one_gib_and_a_conflicting_byte_override_is_rejected()
    {
        using var databases = new Databases();
        var catalogPath = WriteTenantCatalog(databases, _ => { });
        await using var panel = await ProvisionalPanel.StartAsync();
        var purchaseService = BuildPurchaseService(databases, catalogPath, panel.Url);

        Assert.Equal(OneGibBytes, ApiService.ConvertGBToBytes(1));

        var placement = purchaseService.ResolveTenantProvisionalPlacement(new XuiV3PurchaseSelection
        {
            ServiceKey = "normal",
            TrafficGb = 50,
            DurationKey = "m1"
        });
        var user = new CredUser { TelegramUserId = 5151, IsColleague = false };
        var serverInfo = new ServerInfo { Url = panel.Url, ApiToken = "test-only" };

        // A byte override that agrees with the GB value is harmless and must not change the wire quota.
        await CreateProvisionalAsync(purchaseService, user, serverInfo, placement,
            "tenant-card-provisional-create:9002", trafficBytesOverride: OneGibBytes);
        Assert.Equal(1, panel.PostCount);
        Assert.Equal(OneGibBytes, panel.LastClient!["totalGB"]!.Value<long>());

        // A byte override that disagrees must be refused, and must not reach the panel.
        var conflict = await Assert.ThrowsAsync<ArgumentException>(() => CreateProvisionalAsync(
            purchaseService, user, serverInfo, placement,
            "tenant-card-provisional-create:9003", trafficBytesOverride: OneGibBytes * 10));
        Assert.Contains("TrafficBytes", conflict.Message, StringComparison.Ordinal);
        Assert.Equal(1, panel.PostCount);
    }

    /// <summary>Invokes the production explicit-limit provisioning path with the standard provisional options.</summary>
    /// <param name="purchaseService">Production purchase service bound to a temporary catalog and the fake panel.</param>
    /// <param name="user">Credentials profile of the provisional account recipient.</param>
    /// <param name="serverInfo">Fake panel endpoint.</param>
    /// <param name="placement">Authorized placement previously resolved from the original order selection.</param>
    /// <param name="operationKey">Durable exactly-once key for this provisional creation.</param>
    /// <param name="trafficBytesOverride">Optional byte override used to probe the quota-integrity guard.</param>
    /// <returns>The creation result returned by the production pipeline.</returns>
    private static Task<XuiV3AccountCreationResult> CreateProvisionalAsync(
        XuiV3PurchaseService purchaseService,
        CredUser user,
        ServerInfo serverInfo,
        XuiV3ProvisionalPlacement placement,
        string operationKey,
        long trafficBytesOverride = 0)
        => purchaseService.CreateAccountWithExplicitLimitsAsync(
            user,
            serverInfo,
            placement,
            selectedCountry: "tenant-card-provisional",
            trafficGb: 1,
            durationDays: 1,
            cancellationToken: default,
            metadataOptions: new XuiV3AccountMetadataOptions
            {
                OperationKey = operationKey,
                PlanKeyOverride = "provisional-1gb-1d",
                PlanNameOverride = "اکانت موقت",
                PriceTomanOverride = 0,
                TrafficBytes = trafficBytesOverride,
                UserComment = "tenant provisional courtesy account",
                SaveUserStatus = false
            });

    /// <summary>Builds a production purchase service bound to a temporary catalog file and the fake panel.</summary>
    /// <param name="databases">Fixture owning the temporary users.db path.</param>
    /// <param name="catalogPath">Absolute path of the temporary plan catalog written by <see cref="WriteTenantCatalog" />.</param>
    /// <param name="panelUrl">Loopback URL of the in-process fake XUI panel.</param>
    /// <returns>A purchase service that reads the temporary catalog and the fake panel configuration.</returns>
    private static XuiV3PurchaseService BuildPurchaseService(Databases databases, string catalogPath, string panelUrl)
        => new(BuildConfiguration(databases, catalogPath, panelUrl), databases.Users);

    /// <summary>Creates the configuration shared by the provisional provisioning tests.</summary>
    /// <param name="databases">Fixture owning the temporary database paths.</param>
    /// <param name="catalogPath">Absolute path of the temporary plan catalog.</param>
    /// <param name="panelUrl">Loopback URL of the in-process fake XUI panel.</param>
    /// <returns>In-memory configuration with no live provider credentials.</returns>
    private static IConfiguration BuildConfiguration(Databases databases, string catalogPath, string panelUrl)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["userDatabasePath"] = Path.Combine(databases.DirectoryPath, "users.db"),
            ["credentialsDatabasePath"] = Path.Combine(databases.DirectoryPath, "credentials.db"),
            ["XuiV3ApiBaseUrl"] = panelUrl,
            ["XuiV3ApiToken"] = "test-only",
            ["XuiV3ServicePlansPath"] = catalogPath,
            ["xuiV3TransientRetryCount"] = "0",
            ["xuiV3RequestTimeoutSeconds"] = "5"
        }).Build();

    /// <summary>
    /// Copies the real plan catalog into the fixture directory and applies test-only mutations to it.
    /// </summary>
    /// <param name="databases">Fixture that owns the temporary directory.</param>
    /// <param name="mutate">
    /// Callback applied to the copied catalog root before it is written. Tests use it to disable a service or hide an
    /// unlimited plan without editing the repository's production plan file.
    /// </param>
    /// <returns>The absolute path of the temporary catalog file.</returns>
    /// <remarks>
    /// Copying the production catalog keeps these tests honest about the real service keys, minimums, and inbound
    /// placement while still allowing a test to simulate an order whose plan later stopped being tenant-visible.
    /// </remarks>
    private static string WriteTenantCatalog(Databases databases, Action<JObject> mutate)
    {
        var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/xui-v3-service-plans.json"));
        var catalog = JObject.Parse(File.ReadAllText(source));
        mutate(catalog);
        var target = Path.Combine(databases.DirectoryPath, "provisional-catalog.json");
        File.WriteAllText(target, catalog.ToString());
        return target;
    }

    /// <summary>In-process fake 3x-ui panel that records create requests and answers read-backs.</summary>
    /// <remarks>
    /// Only the endpoints the creation boundary touches are served: the add POST is captured, the per-client link probe
    /// returns an empty list, and every other GET echoes the captured client so the pipeline can prove the panel UUID and
    /// subId without any network access to a real panel.
    /// </remarks>
    private sealed class ProvisionalPanel : IAsyncDisposable
    {
        private WebApplication? _app;

        /// <summary>Gets the loopback base URL the fake panel listens on.</summary>
        public string Url { get; private set; } = string.Empty;

        /// <summary>Gets the number of add POST requests observed.</summary>
        public int PostCount { get; private set; }

        /// <summary>Gets the client object captured from the most recent add POST.</summary>
        public JObject? LastClient { get; private set; }

        /// <summary>Gets the inbound id array captured from the most recent add POST.</summary>
        public JArray? LastInboundIds { get; private set; }

        /// <summary>Starts the fake panel on an ephemeral loopback port.</summary>
        /// <returns>The started panel.</returns>
        public static async Task<ProvisionalPanel> StartAsync()
        {
            var panel = new ProvisionalPanel();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.Run(async context =>
            {
                if (context.Request.Method == "POST")
                {
                    var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                    var payload = JObject.Parse(body);
                    panel.LastClient = (JObject)payload["client"]!;
                    // The add request carries placement beside the client object, mirroring the 3x-ui create contract.
                    panel.LastInboundIds = payload["inboundIds"] as JArray ?? new JArray();
                    panel.LastClient["inboundIds"] = panel.LastInboundIds;
                    panel.PostCount++;
                    await context.Response.WriteAsync("{\"success\":true,\"obj\":{}}");
                    return;
                }

                if (context.Request.Path.Value != null && context.Request.Path.Value.Contains("links"))
                {
                    await context.Response.WriteAsync("{\"success\":true,\"obj\":[]}");
                    return;
                }

                await context.Response.WriteAsync(
                    new JObject { ["success"] = true, ["obj"] = panel.LastClient ?? new JObject() }.ToString());
            });
            await app.StartAsync();
            panel._app = app;
            panel.Url = app.Urls.Single();
            return panel;
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_app != null)
                await _app.StopAsync();
        }
    }
}
