using Adminbot.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;
using Xunit;

/// <summary>
/// Regression coverage for generational tenant provisioning attempts and durable retry authorization.
/// </summary>
/// <remarks>
/// A <see cref="TenantBotOrder"/> is the commercial transaction; every <c>XuiV3CreationOperation</c> row is one
/// immutable provisioning attempt keyed <c>tenant-create:{orderId}</c> or <c>tenant-create:{orderId}:retry:N</c>.
/// These tests protect the production incident where a paid order's original attempt was definitively rejected and
/// the owner's later explicit confirmation must allocate <c>retry:1</c> with the current service topology instead of
/// reusing the rejected key (which threw "Creation operation identity does not match its reservation").
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>Creates one durable tenant creation attempt row for a given order and generation.</summary>
    /// <param name="orderId">Positive users.db tenant order id embedded in the immutable key.</param>
    /// <param name="generation">Zero for the base attempt; positive N for <c>:retry:N</c>.</param>
    /// <param name="outcome">Durable boundary outcome of the attempt.</param>
    /// <param name="authorizedBy">Optional durable authorization key that granted this retry generation.</param>
    /// <param name="inboundIdsJson">Immutable inbound reservation text; the null default keeps tests focused on state.</param>
    /// <returns>An attempt row that a test may add through its users.db context.</returns>
    private static XuiV3CreationOperation TenantAttempt(
        int orderId,
        int generation,
        XuiV3CreationOutcome outcome,
        string? authorizedBy = null,
        string inboundIdsJson = "[1]")
    {
        return new XuiV3CreationOperation
        {
            OperationKey = generation == 0
                ? TenantProvisioningAttemptCoordinator.BuildBaseOperationKey(orderId)
                : TenantProvisioningAttemptCoordinator.BuildRetryOperationKey(orderId, generation),
            PanelKey = "panel",
            TelegramUserId = 123,
            ClientJson = "private",
            InboundIdsJson = inboundIdsJson,
            BusinessParametersJson = "{}",
            Outcome = outcome,
            AuthorizedByKey = authorizedBy,
            CreatedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>Runs the durable reservation store against the isolated users database.</summary>
    /// <param name="databases">Fixture owning the temporary SQLite files.</param>
    /// <returns>A fresh store whose contexts are created per operation.</returns>
    private static XuiV3CreationOperationStore TenantStore(Databases databases) => new(databases.Users);

    /// <summary>Builds the central attempt coordinator over the isolated users database.</summary>
    /// <param name="databases">Fixture owning the temporary SQLite files.</param>
    /// <returns>A fresh coordinator reading short-lived detached contexts.</returns>
    private static TenantProvisioningAttemptCoordinator TenantCoordinator(Databases databases) => new(databases.Users);

    /// <summary>Regression: a paid order with no prior attempt always uses the base tenant-create key.</summary>
    /// <returns>A completed task after the base key resolution is asserted.</returns>
    /// <remarks>Protects state-machine rule A: first fulfillment is automatic and needs no explicit authorization.</remarks>
    [Fact]
    public async Task Provisioning_no_prior_attempt_allocates_base_key()
    {
        using var databases = new Databases();
        var resolution = await TenantCoordinator(databases).ResolvePurchaseAttemptAsync(205, null, default);
        Assert.True(resolution.Allowed);
        Assert.Equal("tenant-create:205", resolution.OperationKey);
        Assert.Equal(0, resolution.Generation);
        Assert.Null(resolution.AuthorizedByKey);
    }

    /// <summary>Reserved, PostStarted, Ambiguous, and Applied attempts are reused; only a rejected attempt needs a new authorization.</summary>
    /// <param name="outcome">Durable boundary outcome stored for the highest attempt.</param>
    /// <returns>A completed task after the exact key reuse is asserted for every non-terminal outcome.</returns>
    /// <remarks>Protects rules B, C, D, E, and F: invoking fulfillment again never creates <c>:retry:1</c> while an
    /// attempt is already reserved, started, ambiguous, or applied.</remarks>
    [Theory]
    [InlineData(XuiV3CreationOutcome.Reserved)]
    [InlineData(XuiV3CreationOutcome.PostStarted)]
    [InlineData(XuiV3CreationOutcome.Ambiguous)]
    [InlineData(XuiV3CreationOutcome.Applied)]
    public async Task Provisioning_non_terminal_latest_attempt_is_reused(XuiV3CreationOutcome outcome)
    {
        using var databases = new Databases();
        await using (var db = databases.Users.CreateDbContext())
        {
            db.XuiV3CreationOperations.Add(TenantAttempt(205, 1, outcome));
            await db.SaveChangesAsync();
        }

        var coordinator = TenantCoordinator(databases);
        // Even a new explicit action cannot open retry:2 while retry:1 is not terminally rejected.
        TenantProvisioningRetryAuthorization? authorization;
        using (TelegramUpdateExecutionScope.Push(55))
            authorization = TenantProvisioningRetryAuthorization.TryTelegramConfirmation(
                TenantProvisioningRetryAuthorizationKind.OwnerExplicit, 42, 205);
        Assert.NotNull(authorization);
        var resolution = await coordinator.ResolvePurchaseAttemptAsync(205, authorization, default);
        Assert.True(resolution.Allowed);
        Assert.Equal("tenant-create:205:retry:1", resolution.OperationKey);
        Assert.Equal(1, resolution.Generation);
        Assert.Null(resolution.AuthorizedByKey);
        // The automatic path (null authorization) reuses the same non-terminal attempt as well.
        var automatic = await coordinator.ResolvePurchaseAttemptAsync(205, null, default);
        Assert.True(automatic.Allowed);
        Assert.Equal("tenant-create:205:retry:1", automatic.OperationKey);
    }

    /// <summary>An automatic path never allocates the next generation after a definitive rejection.</summary>
    /// <returns>A completed task after the safe refusal is asserted.</returns>
    /// <remarks>Protects rule G and the payment contract: duplicate provider callbacks, reconciliation workers, and
    /// customer status checks cannot silently create another account after a terminal rejection.</remarks>
    [Fact]
    public async Task Provisioning_rejected_latest_without_explicit_authorization_is_refused()
    {
        using var databases = new Databases();
        await using (var db = databases.Users.CreateDbContext())
        {
            db.XuiV3CreationOperations.Add(TenantAttempt(205, 0, XuiV3CreationOutcome.DefinitiveRejected));
            db.XuiV3CreationOperations.Add(TenantAttempt(205, 1, XuiV3CreationOutcome.DefinitiveRejected));
            await db.SaveChangesAsync();
        }

        var resolution = await TenantCoordinator(databases).ResolvePurchaseAttemptAsync(205, null, default);
        Assert.False(resolution.Allowed);
        Assert.Null(resolution.OperationKey);
        Assert.Contains("explicit", resolution.Refusal, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>An explicit owner or super-admin action opens retry:1 after a rejected base attempt.</summary>
    /// <param name="kind">Explicit authorization kind supplied by the acting product or operator flow.</param>
    /// <returns>A completed task after retry:1 is selected for both explicit kinds.</returns>
    /// <remarks>Protects rules H and I and the production incident: the owner's later explicit confirmation of the
    /// already-paid unfulfilled order must automatically select the next generation without /inbox_retry_tenant_order.</remarks>
    [Theory]
    [InlineData(TenantProvisioningRetryAuthorizationKind.OwnerExplicit)]
    [InlineData(TenantProvisioningRetryAuthorizationKind.SuperAdminExplicit)]
    public async Task Provisioning_rejected_base_plus_explicit_action_opens_retry_one(TenantProvisioningRetryAuthorizationKind kind)
    {
        using var databases = new Databases();
        await using (var db = databases.Users.CreateDbContext())
        {
            db.XuiV3CreationOperations.Add(TenantAttempt(205, 0, XuiV3CreationOutcome.DefinitiveRejected));
            await db.SaveChangesAsync();
        }

        TenantProvisioningRetryAuthorization authorization;
        using (TelegramUpdateExecutionScope.Push(99))
            authorization = TenantProvisioningRetryAuthorization.TryTelegramConfirmation(kind, 42, 205);
        var resolution = await TenantCoordinator(databases).ResolvePurchaseAttemptAsync(205, authorization, default);
        Assert.True(resolution.Allowed);
        Assert.Equal("tenant-create:205:retry:1", resolution.OperationKey);
        Assert.Equal(1, resolution.Generation);
        Assert.NotNull(resolution.AuthorizedByKey);
    }

    /// <summary>Rejections advance only under a NEW authorization; replaying the same review never opens retry:2.</summary>
    /// <returns>A completed task after retry:1 rejection, replay refusal, and retry:2 allocation are asserted.</returns>
    /// <remarks>Protects rules J and K and the emergency-command contract: one reviewed action grants at most one
    /// generation, so the durable review reference cannot be reused to climb generations.</remarks>
    [Fact]
    public async Task Provisioning_same_authorization_never_advances_a_second_generation()
    {
        using var databases = new Databases();
        await using (var db = databases.Users.CreateDbContext())
        {
            db.XuiV3CreationOperations.Add(TenantAttempt(205, 0, XuiV3CreationOutcome.DefinitiveRejected));
            await db.SaveChangesAsync();
        }

        var store = TenantStore(databases);
        var coordinator = TenantCoordinator(databases);
        var first = TenantProvisioningRetryAuthorization.ReviewedRecovery(7, 205, "review-7");

        // First reviewed action grants retry:1 and persists the durable authorization on the reservation.
        var r1 = await coordinator.ResolvePurchaseAttemptAsync(205, first, default);
        Assert.True(r1.Allowed);
        Assert.Equal("tenant-create:205:retry:1", r1.OperationKey);
        Assert.Equal(first.DurableKey, r1.AuthorizedByKey);
        await store.ReserveAsync(new XuiV3CreationOperation
        {
            OperationKey = r1.OperationKey, PanelKey = "panel", TelegramUserId = 123,
            ClientJson = "private", InboundIdsJson = "[1]",
            AuthorizedByKey = r1.AuthorizedByKey, CreatedAtUtc = DateTime.UtcNow
        }, default);
        Assert.True(await store.TryStartPostAsync(r1.OperationKey, default));
        await store.MarkFailureAsync(r1.OperationKey, XuiV3CreationOutcome.DefinitiveRejected, default);

        // Replaying review-7 after retry:1 was rejected must NOT allocate retry:2.
        var replay = await coordinator.ResolvePurchaseAttemptAsync(205, first, default);
        Assert.False(replay.Allowed);
        Assert.Contains("already used", replay.Refusal, StringComparison.OrdinalIgnoreCase);

        // A genuinely new reviewed action may allocate retry:2.
        var second = TenantProvisioningRetryAuthorization.ReviewedRecovery(7, 205, "review-8");
        var r2 = await coordinator.ResolvePurchaseAttemptAsync(205, second, default);
        Assert.True(r2.Allowed);
        Assert.Equal("tenant-create:205:retry:2", r2.OperationKey);
        Assert.Equal(second.DurableKey, r2.AuthorizedByKey);
    }

    /// <summary>Foreign keys of other orders or malformed suffixes never influence this order's decision.</summary>
    /// <returns>A completed task after exact-order isolation is asserted.</returns>
    /// <remarks>Protects strict key parsing: <c>tenant-create:2050</c> and <c>tenant-create:205:other</c> are never
    /// correlated with order 205, so an unrelated panel row cannot block or advance a paid order.</remarks>
    [Fact]
    public async Task Provisioning_ignores_other_orders_and_foreign_key_suffixes()
    {
        using var databases = new Databases();
        await using (var db = databases.Users.CreateDbContext())
        {
            db.XuiV3CreationOperations.Add(TenantAttempt(2050, 0, XuiV3CreationOutcome.DefinitiveRejected));
            db.XuiV3CreationOperations.Add(new XuiV3CreationOperation
            {
                OperationKey = "tenant-create:205:foreign",
                PanelKey = "panel", TelegramUserId = 123, ClientJson = "private",
                InboundIdsJson = "[1]", Outcome = XuiV3CreationOutcome.DefinitiveRejected,
                CreatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var resolution = await TenantCoordinator(databases).ResolvePurchaseAttemptAsync(205, null, default);
        Assert.True(resolution.Allowed);
        Assert.Equal("tenant-create:205", resolution.OperationKey);
    }

    /// <summary>Concurrent explicit retry requests converge on one generation and at most one POST authorization.</summary>
    /// <returns>A completed task after identical key selection and a single TryStartPost winner are asserted.</returns>
    /// <remarks>Protects rule N and requirement 5: decisions derive from the same durable state, so racing callers
    /// pick the same retry key and the unique operation key plus conditional Reserved-to-PostStarted claim remain the
    /// final safety boundary.</remarks>
    [Fact]
    public async Task Provisioning_concurrent_explicit_retries_converge_on_one_generation()
    {
        using var databases = new Databases();
        await using (var db = databases.Users.CreateDbContext())
        {
            db.XuiV3CreationOperations.Add(TenantAttempt(205, 0, XuiV3CreationOutcome.DefinitiveRejected));
            await db.SaveChangesAsync();
        }

        var coordinator = TenantCoordinator(databases);
        TenantProvisioningRetryAuthorization? a;
        TenantProvisioningRetryAuthorization? b;
        using (TelegramUpdateExecutionScope.Push(1))
            a = TenantProvisioningRetryAuthorization.TryTelegramConfirmation(
                TenantProvisioningRetryAuthorizationKind.OwnerExplicit, 1, 205);
        using (TelegramUpdateExecutionScope.Push(2))
            b = TenantProvisioningRetryAuthorization.TryTelegramConfirmation(
                TenantProvisioningRetryAuthorizationKind.OwnerExplicit, 2, 205);
        Assert.NotNull(a);
        Assert.NotNull(b);
        var results = await Task.WhenAll(
            coordinator.ResolvePurchaseAttemptAsync(205, a, default),
            coordinator.ResolvePurchaseAttemptAsync(205, b, default));
        Assert.True(results.All(x => x.Allowed));
        Assert.All(results, x => Assert.Equal("tenant-create:205:retry:1", x.OperationKey));

        // The winner's reservation persists the first authorization; a replayed loser never advances.
        var store = TenantStore(databases);
        var claim = await store.ReserveAsync(new XuiV3CreationOperation
        {
            OperationKey = results[0].OperationKey, PanelKey = "panel", TelegramUserId = 123,
            ClientJson = "private", InboundIdsJson = "[1]",
            AuthorizedByKey = results[0].AuthorizedByKey, CreatedAtUtc = DateTime.UtcNow
        }, default);
        Assert.True(claim.MayCreate);
        Assert.True(await store.TryStartPostAsync(results[0].OperationKey, default));
        Assert.False(await store.TryStartPostAsync(results[0].OperationKey, default));
    }

    /// <summary>Restart after a retry reservation or PostStarted commit reuses the same attempt; no second generation.</summary>
    /// <param name="outcome">Durable boundary outcome at the simulated crash.</param>
    /// <returns>A completed task after the restarted coordinator stays on retry:1.</returns>
    /// <remarks>Protects rules O and P: a crash after reservation or after the POST authorization never makes the next
    /// explicit confirmation allocate retry:2; read-back of the existing attempt remains the only recovery path.</remarks>
    [Theory]
    [InlineData(XuiV3CreationOutcome.Reserved)]
    [InlineData(XuiV3CreationOutcome.PostStarted)]
    public async Task Provisioning_restart_after_crash_reuses_same_retry_key(XuiV3CreationOutcome outcome)
    {
        using var databases = new Databases();
        await using (var db = databases.Users.CreateDbContext())
        {
            db.XuiV3CreationOperations.Add(TenantAttempt(205, 0, XuiV3CreationOutcome.DefinitiveRejected));
            await db.SaveChangesAsync();
        }

        var store = TenantStore(databases);
        var coordinator = TenantCoordinator(databases);
        TenantProvisioningRetryAuthorization? authorization;
        using (TelegramUpdateExecutionScope.Push(66))
            authorization = TenantProvisioningRetryAuthorization.TryTelegramConfirmation(
                TenantProvisioningRetryAuthorizationKind.OwnerExplicit, 42, 205);
        Assert.NotNull(authorization);
        var reservation = await coordinator.ResolvePurchaseAttemptAsync(205, authorization, default);
        await store.ReserveAsync(new XuiV3CreationOperation
        {
            OperationKey = reservation.OperationKey, PanelKey = "panel", TelegramUserId = 123,
            ClientJson = "private", InboundIdsJson = "[1]",
            AuthorizedByKey = reservation.AuthorizedByKey, CreatedAtUtc = DateTime.UtcNow
        }, default);
        if (outcome == XuiV3CreationOutcome.PostStarted)
            Assert.True(await store.TryStartPostAsync(reservation.OperationKey, default));

        // Process restart: a brand-new coordinator/store read only durable state.
        var restartedCoordinator = TenantCoordinator(databases);
        var restartedStore = TenantStore(databases);
        var afterRestart = await restartedCoordinator.ResolvePurchaseAttemptAsync(205, authorization, default);
        Assert.True(afterRestart.Allowed);
        Assert.Equal("tenant-create:205:retry:1", afterRestart.OperationKey);
        Assert.Equal(1, afterRestart.Generation);
        Assert.Null(afterRestart.AuthorizedByKey);
        if (outcome == XuiV3CreationOutcome.PostStarted)
        {
            // A crashed PostStarted attempt is GET-only forever: no second POST is ever granted.
            Assert.False(await restartedStore.TryStartPostAsync(afterRestart.OperationKey, default));
        }
    }

    /// <summary>Reproduces the production incident: rejected base attempt re-provisioned by an explicit owner action.</summary>
    /// <returns>A completed task after retry:1 with the new topology is reserved, POSTed exactly once, and Applied.</returns>
    /// <remarks>Protects the full incident (section 13): original <c>tenant-create:205</c> is DefinitiveRejected with the
    /// historical inbound list, the service now resolves a newer inbound topology, the owner explicitly confirms again,
    /// and fulfillment must select <c>retry:1</c> with the CURRENT topology — never reusing the rejected key (which would
    /// raise the reservation identity exception), never creating a payment, and never sending a second POST after Applied.</remarks>
    [Fact]
    public async Task Provisioning_incident_205_retry_uses_current_topology_exactly_once()
    {
        using var databases = new Databases();
        const int orderId = 205;
        var historicalTopology = new[] { 188, 190, 200, 202, 206, 207, 212, 215, 216, 217, 218, 219, 220 };
        var currentTopology = new[] { 200, 202, 206, 207, 212, 215, 216, 217, 218, 219, 220, 221 };
        await using (var db = databases.Users.CreateDbContext())
        {
            db.XuiV3CreationOperations.Add(TenantAttempt(
                orderId,
                0,
                XuiV3CreationOutcome.DefinitiveRejected,
                inboundIdsJson: "[" + string.Join(",", historicalTopology) + "]"));
            await db.SaveChangesAsync();
        }

        var posts = 0;
        JObject? identity = null;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            if (context.Request.Method == "POST")
            {
                Interlocked.Increment(ref posts);
                var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                identity = (JObject)JObject.Parse(body)["client"]!;
                identity!["inboundIds"] = new JArray(1);
                await context.Response.WriteAsync("{\"success\":true,\"obj\":{}}");
                return;
            }
            if (context.Request.Path.Value != null && context.Request.Path.Value.Contains("links"))
            {
                await context.Response.WriteAsync("{\"success\":true,\"obj\":[]}");
                return;
            }
            var found = (JObject)(identity?.DeepClone() ?? new JObject());
            await context.Response.WriteAsync(new JObject { ["success"] = true, ["obj"] = found }.ToString());
        });
        await app.StartAsync();
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["xuiV3TransientRetryCount"] = "0",
                ["xuiV3RequestTimeoutSeconds"] = "5"
            }).Build();
            var store = TenantStore(databases);
            var coordinator = TenantCoordinator(databases);
            TenantProvisioningRetryAuthorization authorization;
            using (TelegramUpdateExecutionScope.Push(777))
                authorization = TenantProvisioningRetryAuthorization.TryTelegramConfirmation(
                    TenantProvisioningRetryAuthorizationKind.OwnerExplicit, 42, orderId);

            // The coordinator picks retry:1 without ever calling ReserveAsync with the rejected base key, so no
            // identity-mismatch exception can escape the routine owner-confirmation flow.
            var resolution = await coordinator.ResolvePurchaseAttemptAsync(orderId, authorization, default);
            Assert.True(resolution.Allowed);
            Assert.Equal("tenant-create:205:retry:1", resolution.OperationKey);
            Assert.Equal(1, resolution.Generation);

            var request = new AccountDto
            {
                TelegramUserId = 123,
                TotoalGB = "10",
                SelectedCountry = "default",
                ServerInfo = new ServerInfo { Url = app.Urls.Single(), ApiToken = "test-only" }
            };
            var createOptions = () => new XuiV3CreateAccountOptions
            {
                OperationKey = resolution.OperationKey,
                OperationStore = new XuiV3CreationOperationStore(databases.Users),
                AuthorizedByKey = resolution.AuthorizedByKey,
                InboundIds = currentTopology,
                DurationDays = 30,
                TrafficGb = 10,
                SaveUserStatus = false
            };
            var created = await ApiServicev3.CreateUserAccountAsync(request, configuration, createOptions());
            Assert.True(created.Success, created.Message);
            Assert.Equal(1, posts);

            await using (var db = databases.Users.CreateDbContext())
            {
                var rows = await db.XuiV3CreationOperations.AsNoTracking().OrderBy(x => x.OperationKey).ToListAsync();
                var original = rows.Single(x => x.OperationKey == "tenant-create:205");
                var retry = rows.Single(x => x.OperationKey == "tenant-create:205:retry:1");
                // Historical attempt stays immutable and terminal; it is never rewritten with the new topology.
                Assert.Equal(XuiV3CreationOutcome.DefinitiveRejected, original.Outcome);
                Assert.Equal("[" + string.Join(",", historicalTopology) + "]", original.InboundIdsJson);
                Assert.Null(original.AuthorizedByKey);
                // The retry attempt used the CURRENT operational topology and recorded its durable authorization.
                Assert.Equal(XuiV3CreationOutcome.Applied, retry.Outcome);
                Assert.Equal("[" + string.Join(",", currentTopology) + "]", retry.InboundIdsJson);
                Assert.Equal(authorization.DurableKey, retry.AuthorizedByKey);
            }

            // A repeated successful fulfillment call (crash after Applied but before order settlement, then resumed)
            // reuses the Applied attempt: no second account, no second POST, no retry:2, even under a new explicit press.
            var resumed = await coordinator.ResolvePurchaseAttemptAsync(orderId, authorization, default);
            Assert.True(resumed.Allowed);
            Assert.Equal("tenant-create:205:retry:1", resumed.OperationKey);
            var secondCall = await ApiServicev3.CreateUserAccountAsync(request, configuration, createOptions());
            Assert.True(secondCall.Success, secondCall.Message);
            Assert.Equal(1, posts);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>An Applied attempt resumes idempotently without another POST even when the order was never settled.</summary>
    /// <returns>A completed task after the crash window is re-entered and creation succeeds with zero additional POSTs.</returns>
    /// <remarks>Protects rule E and requirement 9: after XUI success but a crash before <c>IsFulfilled=true</c>, the next
    /// fulfillment call must detect the Applied attempt, issue no addClient, and remain able to continue one-time
    /// settlement (which the shared fulfillment gate and unique ledger row then protect). The first call creates the
    /// durable Applied reservation through the real pipeline so the resumed call's immutable parameters match.</remarks>
    [Fact]
    public async Task Provisioning_applied_attempt_resumes_without_new_post()
    {
        using var databases = new Databases();
        var posts = 0;
        JObject? identity = null;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            if (context.Request.Method == "POST")
            {
                Interlocked.Increment(ref posts);
                var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                identity = (JObject)JObject.Parse(body)["client"]!;
                identity!["inboundIds"] = new JArray(1);
                await context.Response.WriteAsync("{\"success\":true,\"obj\":{}}");
                return;
            }
            if (context.Request.Path.Value != null && context.Request.Path.Value.Contains("links"))
            {
                await context.Response.WriteAsync("{\"success\":true,\"obj\":[]}");
                return;
            }
            await context.Response.WriteAsync(new JObject { ["success"] = true, ["obj"] = identity ?? new JObject() }.ToString());
        });
        await app.StartAsync();
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["xuiV3TransientRetryCount"] = "0",
                ["xuiV3RequestTimeoutSeconds"] = "5"
            }).Build();
            var request = new AccountDto
            {
                TelegramUserId = 123,
                TotoalGB = "10",
                SelectedCountry = "default",
                ServerInfo = new ServerInfo { Url = app.Urls.Single(), ApiToken = "test-only" }
            };
            var resolution = await TenantCoordinator(databases).ResolvePurchaseAttemptAsync(205, null, default);
            Assert.True(resolution.Allowed);
            Assert.Equal("tenant-create:205", resolution.OperationKey);
            var options = () => new XuiV3CreateAccountOptions
            {
                OperationKey = resolution.OperationKey,
                OperationStore = new XuiV3CreationOperationStore(databases.Users),
                InboundIds = new[] { 200 },
                DurationDays = 30,
                TrafficGb = 10,
                SaveUserStatus = false
            };
            var created = await ApiServicev3.CreateUserAccountAsync(request, configuration, options());
            Assert.True(created.Success, created.Message);
            Assert.Equal(1, posts);

            // Simulated crash after Applied but before order settlement: the resumed fulfillment call reuses the
            // same Applied attempt, issues no addClient, and can proceed to one-time settlement.
            var resumed = await TenantCoordinator(databases).ResolvePurchaseAttemptAsync(205, null, default);
            Assert.True(resumed.Allowed);
            Assert.Equal("tenant-create:205", resumed.OperationKey);
            var afterCrash = await ApiServicev3.CreateUserAccountAsync(request, configuration, options());
            Assert.True(afterCrash.Success, afterCrash.Message);
            Assert.Equal(1, posts);
            await using var db = databases.Users.CreateDbContext();
            Assert.Equal(1, await db.XuiV3CreationOperations.CountAsync());
        }
        finally
        {
            await app.StopAsync();
        }
    }

    /// <summary>The retry-authorization column upgrades existing historical rows without loss and without drift.</summary>
    /// <returns>A completed task after migrating the immediately prior schema and asserting model consistency.</returns>
    /// <remarks>Protects the EF migration contract: real databases upgraded through the exact migration before this one
    /// gain the nullable column while existing attempt rows keep their outcomes, and the current model has no drift.</remarks>
    [Fact]
    public async Task Provisioning_retry_authorization_migration_upgrades_prior_schema()
    {
        using var databases = new Databases(initialize: false);
        await using var db = databases.Users.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260906041858_LinkMutationClaimsToInbox");
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO XuiV3CreationOperations(OperationKey,PanelKey,TelegramUserId,ClientJson,InboundIdsJson,CreatedAtUtc,Outcome) VALUES ('tenant-create:205','panel',123,'private','[1]','2026-01-01',3)");
        await migrator.MigrateAsync();
        await using var current = databases.Users.CreateDbContext();
        var migrated = await current.XuiV3CreationOperations.AsNoTracking().SingleAsync(x => x.OperationKey == "tenant-create:205");
        Assert.Equal(XuiV3CreationOutcome.DefinitiveRejected, migrated.Outcome);
        Assert.Null(migrated.AuthorizedByKey);
        Assert.False(current.Database.HasPendingModelChanges());
    }
}
