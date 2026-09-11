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
/// plan: the configured service minimum traffic is 10 GB for <c>normal</c> and there is no one-day duration key, so
/// <see cref="XuiV3PurchaseService.ResolveTenantPurchase" /> rejects that selection before any panel call. These tests
/// pin the two properties that make the new entry point safe:
///
/// 1. Placement (inbound ids and service key) still comes from the catalog lookup, so no server/inbound mapping is
///    duplicated outside <see cref="XuiV3PurchaseService"/>.
/// 2. Commercial validation is genuinely bypassed for the supplied limits while the durable exactly-once creation
///    boundary introduced by <see cref="XuiV3CreationOperationStore"/> is preserved, so a repeated call issues no
///    second panel create.
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>
    /// Proves the explicit-limit path provisions the requested 1 GB / 1 day client through catalog placement while the
    /// catalog pricing path rejects the very same selection.
    /// </summary>
    /// <remarks>
    /// The first assertion is the important one: it fails if someone later "simplifies" the explicit-limit path by
    /// routing it through <see cref="XuiV3PurchaseService.ResolveTenantPurchase" />, which would make provisional
    /// delivery impossible again. The panel assertions then prove the provisional quota, the provisional lifetime, and
    /// the reuse of the configured inbound placement actually reached the wire request.
    /// </remarks>
    [Fact]
    public async Task Explicit_limit_provisioning_reuses_catalog_placement_without_pricing_validation()
    {
        using var databases = new Databases();
        var postCount = 0;
        JObject? postedClient = null;
        JArray? postedInbounds = null;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            if (context.Request.Method == "POST")
            {
                Interlocked.Increment(ref postCount);
                var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                var payload = JObject.Parse(body);
                postedClient = (JObject)payload["client"]!;
                // The add request carries placement beside the client object, mirroring the 3x-ui create contract.
                postedInbounds = payload["inboundIds"] as JArray ?? new JArray();
                postedClient["inboundIds"] = postedInbounds;
                await context.Response.WriteAsync("{\"success\":true,\"obj\":{}}");
                return;
            }

            if (context.Request.Path.Value != null && context.Request.Path.Value.Contains("links"))
            {
                await context.Response.WriteAsync("{\"success\":true,\"obj\":[]}");
                return;
            }

            // Read-back returns the captured client so the creation boundary can prove the panel UUID and subId.
            await context.Response.WriteAsync(new JObject { ["success"] = true, ["obj"] = postedClient ?? new JObject() }.ToString());
        });
        await app.StartAsync();
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["userDatabasePath"] = Path.Combine(databases.DirectoryPath, "users.db"),
                ["credentialsDatabasePath"] = Path.Combine(databases.DirectoryPath, "credentials.db"),
                ["XuiV3ApiBaseUrl"] = app.Urls.Single(),
                ["XuiV3ApiToken"] = "test-only",
                ["XuiV3ServicePlansPath"] = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../Data/xui-v3-service-plans.json")),
                ["xuiV3TransientRetryCount"] = "0",
                ["xuiV3RequestTimeoutSeconds"] = "5"
            }).Build();
            var purchaseService = new XuiV3PurchaseService(configuration, databases.Users);

            // The catalog path must reject the provisional shape. This is the regression that protects the whole design:
            // if this stops throwing, the explicit-limit entry point is no longer the only way to create it.
            var provisionalSelection = new XuiV3PurchaseSelection
            {
                ServiceKey = "normal",
                TrafficGb = 1,
                DurationKey = "days-1",
                AccountCount = 1
            };
            Assert.ThrowsAny<Exception>(() => purchaseService.ResolveTenantPurchase(provisionalSelection, false));
            Assert.Equal(0, postCount);

            var service = purchaseService.FindService("normal");
            var expectedInboundIds = XuiV3PurchaseService.ResolveServiceInboundIds(service).Order().ToArray();
            Assert.NotEmpty(expectedInboundIds);

            var user = new CredUser { TelegramUserId = 5150, IsColleague = false };
            var serverInfo = new ServerInfo { Url = app.Urls.Single(), ApiToken = "test-only" };
            const string operationKey = "tenant-card-provisional-create:9001";

            var created = await purchaseService.CreateAccountWithExplicitLimitsAsync(
                user,
                serverInfo,
                serviceKey: "normal",
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
                    UserComment = "tenant provisional courtesy account",
                    SaveUserStatus = false
                });

            Assert.True(created.Success, created.Message);
            Assert.Equal(1, postCount);
            Assert.NotNull(postedClient);

            // Exactly the provisional commercial limits must reach the panel request.
            Assert.Equal(ApiService.ConvertGBToBytes(1), postedClient!["totalGB"]!.Value<long>());
            var expectedExpiry = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds();
            var postedExpiry = postedClient["expiryTime"]!.Value<long>();
            Assert.InRange(postedExpiry, expectedExpiry - TimeSpan.FromMinutes(5).TotalMilliseconds,
                expectedExpiry + TimeSpan.FromMinutes(5).TotalMilliseconds);

            // Placement must be the configured catalog placement, not an empty or invented inbound list.
            Assert.NotNull(postedInbounds);
            var postedInboundIds = postedInbounds!.Values<int>().Order().ToArray();
            Assert.Equal(expectedInboundIds, postedInboundIds);

            // The panel comment must carry the provisional plan label so reminder and sync logic can recognise it.
            var comment = postedClient["comment"]!.Value<string>();
            Assert.Contains("provisional-1gb-1d", comment);
            Assert.Contains("normal", comment);

            await using var db = databases.Users.CreateDbContext();
            var reservation = await db.XuiV3CreationOperations.AsNoTracking().SingleAsync(x => x.OperationKey == operationKey);
            Assert.Equal("tenant-card-provisional-create:9001", reservation.OperationKey);
            // The reserved business parameters are the provisional ones, so a later full-order create cannot reuse this key.
            Assert.Contains("\"TrafficGb\":1", reservation.BusinessParametersJson);
            Assert.Contains("\"DurationDays\":1", reservation.BusinessParametersJson);
            Assert.Contains("provisional-1gb-1d", reservation.ClientJson);

            // Exactly-once: repeating the same provisional operation key must never issue another panel create.
            var repeated = await purchaseService.CreateAccountWithExplicitLimitsAsync(
                user,
                serverInfo,
                serviceKey: "normal",
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
                    UserComment = "tenant provisional courtesy account",
                    SaveUserStatus = false
                });
            Assert.True(repeated.Success, repeated.Message);
            Assert.Equal(1, postCount);
        }
        finally
        {
            await app.StopAsync();
        }
    }
}
