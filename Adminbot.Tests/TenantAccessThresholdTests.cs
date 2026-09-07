using Adminbot.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

/// <summary>Covers the configurable tenant storefront site-wallet threshold and its startup validation.</summary>
/// <remarks>
/// The threshold key is <c>tenantMinimumSiteWalletToman</c>; the historical hard-coded one-million-toman gate is
/// replaced by this value. These tests drive the real <see cref="TenantAccessService"/> through a real website API
/// fake so the full eligibility pipeline (config, HTTP lookup, wallet parse) is exercised.
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>Site wallet at the configured boundary: below denies, at and above the threshold allow.</summary>
    /// <returns>A task completing after the three eligibility evaluations against the real fake server.</returns>
    /// <remarks>Regression guard: the gate must follow <c>tenantMinimumSiteWalletToman</c> (200000 here), not the
    /// historical hard-coded 1,000,000 toman. 199999 must be denied while 200000 must be allowed.</remarks>
    [Fact]
    public async Task Tenant_site_wallet_threshold_uses_configured_minimum()
    {
        using var databases = new Databases(); var wallet = 0L;
        var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders(); builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            using var body = await System.Text.Json.JsonDocument.ParseAsync(context.Request.Body);
            if (body.RootElement.GetProperty("action").GetString() == "get_user")
                await context.Response.WriteAsJsonAsync(new
                {
                    success = true,
                    data = new { username = "owner", telegram_id = "711", email = "owner@test", wallet = Volatile.Read(ref wallet), ban = 0 }
                });
            else
                await context.Response.WriteAsJsonAsync(new { success = true });
        });
        await app.StartAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["gozargahSiteSyncEnabled"] = "true",
            ["gozargahSiteWalletPaymentsEnabled"] = "true",
            ["gozargahSiteApiBaseUrl"] = app.Urls.Single(),
            ["gozargahSiteApiKey"] = "test-only",
            ["tenantMinimumSiteWalletToman"] = "200000"
        }).Build();
        var credentials = new CredentialsStore(databases.Credentials);
        await credentials.AddEmptyUser(711);
        var sync = new GozargahSiteSyncService(databases.Users, credentials,
            new GozargahSiteApiClient(configuration, NullLogger<GozargahSiteApiClient>.Instance),
            configuration, NullLogger<GozargahSiteSyncService>.Instance);
        var access = new TenantAccessService(databases.Users, credentials, sync, configuration, NullLogger<TenantAccessService>.Instance);

        wallet = 199_999;
        Assert.Equal(TenantAccessService.DebtMessage, await access.EvaluateAsync(711, default));
        wallet = 200_000;
        Assert.Null(await access.EvaluateAsync(711, default));
        wallet = 500_000;
        Assert.Null(await access.EvaluateAsync(711, default));
        await app.StopAsync();
    }

    /// <summary>A positive local bot wallet allows access without any website threshold involvement.</summary>
    /// <returns>A task completing after the owner is credited and the access gate opens.</returns>
    /// <remarks>The website client is left unconfigured, so website funding is unavailable; only the local balance can open the gate.</remarks>
    [Fact]
    public async Task Positive_local_wallet_allows_access_independently_of_website_threshold()
    {
        using var databases = new Databases();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["gozargahSiteSyncEnabled"] = "true",
            ["gozargahSiteWalletPaymentsEnabled"] = "true",
            ["gozargahSiteApiKey"] = "test-only",
            ["tenantMinimumSiteWalletToman"] = "200000"
        }).Build();
        var credentials = new CredentialsStore(databases.Credentials);
        await credentials.AddEmptyUser(711);
        await credentials.MutateWalletAsync(711, 100_000, "test-credit:" + Guid.NewGuid().ToString("N"), botId: "owner-wallet");
        var sync = new GozargahSiteSyncService(databases.Users, credentials,
            new GozargahSiteApiClient(configuration, NullLogger<GozargahSiteApiClient>.Instance),
            configuration, NullLogger<GozargahSiteSyncService>.Instance);
        var access = new TenantAccessService(databases.Users, credentials, sync, configuration, NullLogger<TenantAccessService>.Instance);
        Assert.Null(await access.EvaluateAsync(711, default));
    }

    /// <summary>A negative configured site-wallet threshold fails startup validation instead of being clamped.</summary>
    /// <returns>A task completing after invoking the real startup validator.</returns>
    /// <remarks>The validator is the exact private method <c>Program.Main</c> calls before migration; a negative value
    /// would make every storefront pass the website gate and must never be silently converted to zero.</remarks>
    [Fact]
    public void Negative_tenant_site_wallet_threshold_is_rejected_by_startup_validation()
    {
        Assert.Equal(200_000L, new AppConfig().TenantMinimumSiteWalletToman);
        var validator = typeof(Program).GetMethod("ValidateTenantStorefrontConfiguration", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Startup validator not found.");
        var negative = new AppConfig { TenantMinimumSiteWalletToman = -1 };
        // Reflection wraps the validator's exception; assert both the wrapper and the real startup error.
        var thrown = Assert.Throws<TargetInvocationException>(() => validator.Invoke(null, new object[] { negative }));
        Assert.IsType<InvalidOperationException>(thrown.InnerException);
        validator.Invoke(null, new object[] { new AppConfig { TenantMinimumSiteWalletToman = 0 } });
        validator.Invoke(null, new object[] { new AppConfig { TenantMinimumSiteWalletToman = 200_000 } });
    }
}