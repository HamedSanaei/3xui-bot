using Adminbot.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using Xunit;

/// <summary>Covers financial-mode boundaries and startup validation without treating insufficient funding as suspension.</summary>
/// <remarks>
/// The threshold key is <c>tenantMinimumSiteWalletToman</c>. These tests drive the real
/// <see cref="TenantAccessService"/> through a website API fake so configuration, lookup, and wallet parsing
/// exercise the unchanged OR eligibility rule independently of customer access.
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>Below the configured site boundary requires central payments; at and above it restore saved preferences.</summary>
    /// <returns>A task completing three financial evaluations against the website API fake.</returns>
    /// <remarks>All three modes allow customer access. The 200000-toman threshold does not require a positive bot wallet.</remarks>
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
        var below = await access.EvaluateDecisionAsync(711, default);
        Assert.Equal(TenantAccessDecision.InsufficientFunding, below.Decision);
        Assert.True(below.IsAllowed);
        Assert.True(below.RequiresPlatformPayments);
        Assert.Null(below.RestrictionMessage);
        Assert.Null(await access.EvaluateAsync(711, default));
        wallet = 200_000;
        var at = await access.EvaluateDecisionAsync(711, default);
        Assert.Equal(TenantAccessDecision.Allowed, at.Decision);
        Assert.True(at.IsAllowed);
        Assert.False(at.RequiresPlatformPayments);
        Assert.Null(at.RestrictionMessage);
        wallet = 500_000;
        var above = await access.EvaluateDecisionAsync(711, default);
        Assert.Equal(TenantAccessDecision.Allowed, above.Decision);
        Assert.True(above.IsAllowed);
        Assert.False(above.RequiresPlatformPayments);
        Assert.Null(above.RestrictionMessage);
        await app.StopAsync();
    }

    /// <summary>A positive local bot wallet retains saved payment preferences without website funding.</summary>
    /// <returns>A task completing after owner credit and the financial-mode snapshot.</returns>
    /// <remarks>The website client is unconfigured; the positive shared bot wallet alone satisfies the OR funding rule.</remarks>
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
        var evaluation = await access.EvaluateFundingSnapshotAsync(711, default);
        Assert.Equal(TenantAccessDecision.Allowed, evaluation.Decision);
        Assert.True(evaluation.IsAllowed);
        Assert.False(evaluation.RequiresPlatformPayments);
        Assert.Null(evaluation.RestrictionMessage);
    }

    /// <summary>A negative configured site-wallet threshold fails startup validation instead of being clamped.</summary>
    /// <remarks>The validator is the exact private method <c>Program.Main</c> calls before migration; a negative value
    /// would make every usable website wallet financially eligible and must not be silently converted to zero.</remarks>
    [Fact]
    public void Negative_tenant_site_wallet_threshold_is_rejected_by_startup_validation()
    {
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