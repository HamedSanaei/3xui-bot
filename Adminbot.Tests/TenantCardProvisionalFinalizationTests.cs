using Adminbot.Domain;
using Adminbot.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;
using Xunit;

/// <summary>
/// Regression coverage for the crash-safe same-client finalization of a provisionally delivered tenant card-to-card
/// order.
/// </summary>
/// <remarks>
/// <para>
/// The invariant under test is narrow and financial: after owner approval the SAME panel client must carry exactly the
/// ordered quota, with provisional usage neither deducted from it nor added to it, the purchased period starting from a
/// timestamp frozen once, the provisional client's credentials untouched, and no second client anywhere.
/// </para>
/// <para>
/// The tests run against <see cref="FakeUpstreamPanel" />, a model of the upstream 3x-ui traffic and enable semantics
/// proven from MHSanaei/3x-ui v3.7.0 source. The model's exact fidelity matters here, so the most load-bearing property —
/// that the official reset re-admits a runtime user only when the client was disabled when the reset ran — is additionally
/// pinned by its own contract test in <c>XuiV3TrafficContractTests</c>. If that contract test ever fails, these
/// expectations are invalid and the saga must be redesigned rather than adjusted.
/// </para>
/// </remarks>
public sealed partial class ConcurrencyTests
{
    /// <summary>One GiB in bytes, the exact quota a provisional "1 GB" courtesy allowance produces.</summary>
    private const long OneGib = 1073741824L;

    /// <summary>Typical purchased quota used by these tests.</summary>
    private const long PurchasedQuota = OneGib * 50;

    /// <summary>Creates the finalizer under test with the shared panel configuration.</summary>
    /// <param name="databases">The per-test users.db fixture owning the durable saga rows.</param>
    /// <returns>The finalizer and its durable store.</returns>
    private static (TenantCardProvisionalFinalizationService Service, TenantCardProvisionalOperationStore Store)
        CreateFinalizer(Databases databases)
    {
        var store = new TenantCardProvisionalOperationStore(databases.Users);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["XuiV3ApiToken"] = "test-only",
            ["xuiV3TransientRetryCount"] = "0",
            ["xuiV3RequestTimeoutSeconds"] = "5"
        }).Build();
        var service = new TenantCardProvisionalFinalizationService(
            store, configuration, NullLogger<TenantCardProvisionalFinalizationService>.Instance);
        return (service, store);
    }

    /// <summary>Builds the finalization request for a seeded provisional client.</summary>
    /// <param name="panel">Fake panel the provisioned client lives on.</param>
    /// <param name="orderId">Public tenant order id.</param>
    /// <param name="email">Exact provisional client email.</param>
    /// <param name="uuid">Exact provisional client UUID.</param>
    /// <param name="subId">Exact provisional subscription id.</param>
    /// <param name="inboundIds">Expected inbound placement.</param>
    /// <returns>The authoritative request the finalizer is given.</returns>
    private static TenantCardProvisionalFinalizationRequest BuildRequest(
        FakeUpstreamPanel panel,
        string orderId,
        string email,
        string uuid,
        string subId,
        params int[] inboundIds)
        => new(
            TenantBotOrderId: 4242,
            OrderId: orderId,
            ServerInfo: panel.ServerInfo,
            ExpectedEmail: email,
            ExpectedUuid: uuid,
            ExpectedSubId: subId,
            ExpectedInboundIds: inboundIds,
            PurchasedQuotaBytes: PurchasedQuota,
            PurchasedDurationDays: 30);

    /// <summary>Builds the durable saga key exactly as the finalizer does.</summary>
    /// <param name="orderId">Public tenant order id.</param>
    /// <returns>The stable finalize operation key.</returns>
    private static string FinalizeKey(string orderId)
        => $"tenant-card-{TenantCardProvisionalOperationKinds.Finalize}:{orderId}";

    /// <summary>Seeds an enabled provisional client that still holds part of its courtesy allowance.</summary>
    /// <param name="panel">Fake panel to seed.</param>
    /// <param name="email">Client email.</param>
    /// <param name="uuid">Client UUID.</param>
    /// <param name="subId">Client subscription id.</param>
    /// <param name="inboundIds">Inbound placement.</param>
    private static void SeedProvisionalClient(
        FakeUpstreamPanel panel, string email, string uuid, string subId, params int[] inboundIds)
    {
        panel.SeedClient(email, up: 300_000_000, down: 200_000_000, enable: true);
        panel.SetClientIdentity(email, uuid, subId);
        panel.SetClientQuota(email, OneGib, DateTimeOffset.UtcNow.AddHours(20).ToUnixTimeMilliseconds());
        if (inboundIds.Length > 0)
            panel.AttachInbounds(email, inboundIds);
    }

    /// <summary>
    /// Proves the whole point of the saga: the same client ends up with exactly the ordered quota, a fresh counter, and
    /// the frozen purchased period, with its credentials untouched.
    /// </summary>
    /// <remarks>
    /// The provisional client starts with 300 MB uploaded and 200 MB downloaded of courtesy usage already counted, and a
    /// master-pushed global row and node row on top. The final client must show exactly 50 GB with zero usage — not 50 GB
    /// minus 500 MB, and not 50 GB plus 500 MB.
    /// </remarks>
    [Fact]
    public async Task Finalization_applies_exact_purchased_entitlement_to_the_same_client()
    {
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        const string email = "provisional-1";
        const string uuid = "11111111-1111-1111-1111-111111111111";
        SeedProvisionalClient(panel, email, uuid, "sub-1", 2, 4);
        panel.PushGlobalTraffic(email, up: 900_000_000, down: 800_000_000);
        panel.PushNodeTraffic(email, inboundId: 2, up: 700, down: 600);
        var (service, _) = CreateFinalizer(databases);

        var result = await service.FinalizeAsync(
            BuildRequest(panel, "ORD-1", email, uuid, "sub-1", 2, 4), CancellationToken.None);

        Assert.Equal(TenantCardProvisionalFinalizationStatus.Finalized, result.Status);
        Assert.Equal(TenantCardProvisionalSteps.Proven, result.Step);

        var traffic = panel.ReadTraffic(email);
        Assert.Equal(PurchasedQuota, traffic["totalGB"]!.Value<long>());
        Assert.Equal(0, traffic["up"]!.Value<long>());
        Assert.Equal(0, traffic["down"]!.Value<long>());
        Assert.True(traffic["enable"]!.Value<bool>());

        // Every master-pushed and node usage source was cleared, so nothing can re-deplete the client later.
        Assert.Empty(panel.GlobalTrafficRows(email));
        Assert.Empty(panel.NodeTrafficRowsForAllInbounds(email));

        // Same client, same credentials, same placement: no second account and no rotated link.
        var client = panel.ReadClient(email);
        Assert.Equal(uuid, client["uuid"]!.Value<string>());
        Assert.Equal("sub-1", client["subId"]!.Value<string>());
        Assert.Equal(new long[] { 2, 4 }, client["inboundIds"]!.Values<long>().Order().ToArray());

        // The client was enabled throughout, so the reset never re-admitted it and no enable mutation happened at all.
        Assert.Equal(0, panel.EnableMutationCount);
        Assert.Equal(0, panel.RuntimeAddUserCount);
        Assert.Equal(1, panel.ResetRequestCount);
    }

    /// <summary>Proves a repeated approval cannot reset traffic again, extend the expiry, or create a second client.</summary>
    [Fact]
    public async Task Finalization_is_idempotent_when_repeated()
    {
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        const string email = "provisional-2";
        const string uuid = "22222222-2222-2222-2222-222222222222";
        SeedProvisionalClient(panel, email, uuid, "sub-2");
        var (service, _) = CreateFinalizer(databases);
        var request = BuildRequest(panel, "ORD-2", email, uuid, "sub-2");

        var first = await service.FinalizeAsync(request, CancellationToken.None);
        Assert.Equal(TenantCardProvisionalFinalizationStatus.Finalized, first.Status);
        var expiryAfterFirst = panel.ReadClient(email)["expiryTime"]!.Value<long>();
        var resetsAfterFirst = panel.ResetRequestCount;
        var writesAfterFirst = panel.UpdateRequestCount;

        var second = await service.FinalizeAsync(request, CancellationToken.None);

        Assert.Equal(TenantCardProvisionalFinalizationStatus.AlreadyFinalized, second.Status);
        Assert.Equal(resetsAfterFirst, panel.ResetRequestCount);
        Assert.Equal(writesAfterFirst, panel.UpdateRequestCount);
        Assert.Equal(expiryAfterFirst, panel.ReadClient(email)["expiryTime"]!.Value<long>());
        Assert.Equal(PurchasedQuota, panel.ReadClient(email)["totalGB"]!.Value<long>());
    }

    /// <summary>
    /// Proves a same-email client carrying a different credential is never finalized, so the saga cannot upgrade another
    /// customer's account.
    /// </summary>
    [Fact]
    public async Task Finalization_fails_closed_when_the_uuid_does_not_match()
    {
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        const string email = "provisional-3";
        SeedProvisionalClient(panel, email, "33333333-3333-3333-3333-333333333333", "sub-3");
        var (service, _) = CreateFinalizer(databases);

        var result = await service.FinalizeAsync(
            BuildRequest(panel, "ORD-3", email, "99999999-9999-9999-9999-999999999999", "sub-3"),
            CancellationToken.None);

        Assert.Equal(TenantCardProvisionalFinalizationStatus.ManualReview, result.Status);
        Assert.Equal("provisional_identity_uuid_mismatch", result.ReasonCode);

        // Nothing was mutated: the client still carries its provisional quota, usage, and identity.
        var client = panel.ReadClient(email);
        Assert.Equal(OneGib, client["totalGB"]!.Value<long>());
        Assert.Equal(300_000_000, client["up"]!.Value<long>());
        Assert.Equal(200_000_000, client["down"]!.Value<long>());
        Assert.Equal("33333333-3333-3333-3333-333333333333", client["uuid"]!.Value<string>());
        Assert.Equal(0, panel.ResetRequestCount);
        Assert.Equal(0, panel.UpdateRequestCount);
    }

    /// <summary>Proves a client placed on unexpected inbounds is not upgraded, because that would change the subscription.</summary>
    [Fact]
    public async Task Finalization_fails_closed_when_the_inbound_placement_drifted()
    {
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        const string email = "provisional-4";
        const string uuid = "44444444-4444-4444-4444-444444444444";
        SeedProvisionalClient(panel, email, uuid, "sub-4", 2, 4);
        var (service, _) = CreateFinalizer(databases);

        var result = await service.FinalizeAsync(
            BuildRequest(panel, "ORD-4", email, uuid, "sub-4", 2, 4, 9), CancellationToken.None);

        Assert.Equal(TenantCardProvisionalFinalizationStatus.ManualReview, result.Status);
        Assert.Equal("provisional_identity_placement_mismatch", result.ReasonCode);
        Assert.Equal(0, panel.ResetRequestCount);
        Assert.Equal(0, panel.UpdateRequestCount);
    }

    /// <summary>
    /// Proves a provisional client the panel had already disabled is re-admitted exactly once, through the explicit enable
    /// step, and never through the reset's own auto-enable branch.
    /// </summary>
    /// <remarks>
    /// This is the state a real customer reaches when the provisional 1 GB allowance is exhausted: the panel's traffic
    /// poll disables the client. The saga must still be able to upgrade it, and the enable must be a deliberate, proven
    /// step rather than a side effect of the reset.
    /// </remarks>
    [Fact]
    public async Task Finalization_reenables_a_disabled_provisional_client_exactly_once()
    {
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        const string email = "provisional-5";
        const string uuid = "55555555-5555-5555-5555-555555555555";
        panel.SeedClient(email, up: 0, down: 0, enable: false);
        panel.SetClientIdentity(email, uuid, "sub-5");
        panel.SetClientQuota(email, OneGib, DateTimeOffset.UtcNow.AddHours(20).ToUnixTimeMilliseconds());
        var (service, _) = CreateFinalizer(databases);

        var result = await service.FinalizeAsync(
            BuildRequest(panel, "ORD-5", email, uuid, "sub-5"), CancellationToken.None);

        Assert.Equal(TenantCardProvisionalFinalizationStatus.Finalized, result.Status);
        Assert.Equal(PurchasedQuota, panel.ReadClient(email)["totalGB"]!.Value<long>());
        Assert.True(panel.ReadClient(email)["enable"]!.Value<bool>());

        // One enable mutation for the whole saga: the explicit enable step. The reset added nothing on top, because it
        // was handed an already-enabled client and its re-admission guard therefore stayed false.
        Assert.Equal(1, panel.EnableMutationCount);
        Assert.Equal(1, panel.RuntimeAddUserCount);
    }

    /// <summary>
    /// Proves a restart after the purchased quota was already written does not extend the paid period or reset traffic
    /// again.
    /// </summary>
    /// <remarks>
    /// The durable row is pre-seeded with a freeze timestamp two hours in the past, which is what a process that froze the
    /// entitlement and then crashed would leave behind. The resumed saga must write the frozen expiry, not a fresh
    /// <c>now + 30 days</c>, or every restart would silently gift the customer the outage duration.
    /// </remarks>
    [Fact]
    public async Task Restart_after_the_final_write_reuses_the_frozen_expiry_without_a_second_reset()
    {
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        const string email = "provisional-6";
        const string uuid = "66666666-6666-6666-6666-666666666666";
        SeedProvisionalClient(panel, email, uuid, "sub-6");
        var (service, store) = CreateFinalizer(databases);

        var frozenEffective = DateTime.UtcNow.AddHours(-2);
        var frozenExpiry = new DateTimeOffset(DateTime.SpecifyKind(frozenEffective, DateTimeKind.Utc))
            .AddDays(30).ToUnixTimeMilliseconds();

        var key = FinalizeKey("ORD-6");
        await store.ClaimAsync(key, 4242, "ORD-6", TenantCardProvisionalOperationKinds.Finalize, CancellationToken.None);
        await store.FreezeFinalEntitlementAsync(key, frozenEffective, PurchasedQuota, frozenExpiry, CancellationToken.None);
        await store.AdvanceAsync(key, TenantCardProvisionalSteps.QuiescenceConfirmed, null, CancellationToken.None);
        await store.AdvanceAsync(key, TenantCardProvisionalSteps.Reset, "{\"resetIssued\":\"true\"}", CancellationToken.None);
        await store.AdvanceAsync(key, TenantCardProvisionalSteps.Zeroed, null, CancellationToken.None);
        await store.AdvanceAsync(key, TenantCardProvisionalSteps.QuotaWritten, null, CancellationToken.None);

        // The crash window being modeled: the quota write landed on the panel, the counters were cleared, and nothing
        // further was recorded before the process stopped.
        panel.SetUsage(email, 0, 0);
        panel.SetClientQuota(email, PurchasedQuota, frozenExpiry);
        var resetsBeforeResume = panel.ResetRequestCount;

        var result = await service.FinalizeAsync(BuildRequest(panel, "ORD-6", email, uuid, "sub-6"), CancellationToken.None);

        Assert.Equal(TenantCardProvisionalFinalizationStatus.AlreadyFinalized, result.Status);
        Assert.Equal(frozenExpiry, panel.ReadClient(email)["expiryTime"]!.Value<long>());
        Assert.Equal(PurchasedQuota, panel.ReadClient(email)["totalGB"]!.Value<long>());
        Assert.Equal(resetsBeforeResume, panel.ResetRequestCount);
    }

    /// <summary>
    /// Proves a restart after the reset step does not repeat the reset when the panel already reflects a cleared state.
    /// </summary>
    [Fact]
    public async Task Restart_after_the_reset_recovers_without_repeating_it()
    {
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        const string email = "provisional-7";
        const string uuid = "77777777-7777-7777-7777-777777777777";
        SeedProvisionalClient(panel, email, uuid, "sub-7");
        var (service, store) = CreateFinalizer(databases);

        var key = FinalizeKey("ORD-7");
        await store.ClaimAsync(key, 4242, "ORD-7", TenantCardProvisionalOperationKinds.Finalize, CancellationToken.None);
        await store.FreezeFinalEntitlementAsync(key, DateTime.UtcNow, PurchasedQuota, 0, CancellationToken.None);
        await store.AdvanceAsync(key, TenantCardProvisionalSteps.QuiescenceConfirmed, null, CancellationToken.None);

        // The reset was applied on the panel but its durable proof was never recorded before the process stopped.
        panel.SetUsage(email, 0, 0);
        Assert.Equal(0, panel.ResetRequestCount);

        var result = await service.FinalizeAsync(BuildRequest(panel, "ORD-7", email, uuid, "sub-7"), CancellationToken.None);

        Assert.Equal(TenantCardProvisionalFinalizationStatus.Finalized, result.Status);
        // The resumed saga observed an already-cleared panel and therefore issued no reset at all.
        Assert.Equal(0, panel.ResetRequestCount);
        Assert.Equal(0, panel.ReadTraffic(email)["up"]!.Value<long>());
        Assert.Equal(PurchasedQuota, panel.ReadClient(email)["totalGB"]!.Value<long>());
    }

    /// <summary>
    /// Proves usage that appears after the purchased quota was written is re-zeroed instead of being absorbed into the
    /// quota.
    /// </summary>
    /// <remarks>
    /// The refused alternative design was <c>TotalGB = purchasedBytes + usedBytesAtFinalize</c>. This test is the direct
    /// counter-example: usage observed at finalize time must reduce the customer's entitlement by nothing at all.
    /// </remarks>
    [Fact]
    public async Task Post_write_usage_is_rezeroed_without_inflating_the_purchased_quota()
    {
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        const string email = "provisional-8";
        const string uuid = "88888888-8888-8888-8888-888888888888";
        SeedProvisionalClient(panel, email, uuid, "sub-8");
        var (service, store) = CreateFinalizer(databases);

        var key = FinalizeKey("ORD-8");
        await store.ClaimAsync(key, 4242, "ORD-8", TenantCardProvisionalOperationKinds.Finalize, CancellationToken.None);
        await store.FreezeFinalEntitlementAsync(key, DateTime.UtcNow, PurchasedQuota, 0, CancellationToken.None);
        await store.AdvanceAsync(key, TenantCardProvisionalSteps.QuiescenceConfirmed, null, CancellationToken.None);
        await store.AdvanceAsync(key, TenantCardProvisionalSteps.Reset, null, CancellationToken.None);
        await store.AdvanceAsync(key, TenantCardProvisionalSteps.Zeroed, null, CancellationToken.None);
        await store.AdvanceAsync(key, TenantCardProvisionalSteps.QuotaWritten, null, CancellationToken.None);

        // The customer pulled 700 MB between the quota write and the final proof.
        panel.SetClientQuota(email, PurchasedQuota, 0);
        panel.ConsumeTraffic(email, 400_000_000, 300_000_000);

        var result = await service.FinalizeAsync(BuildRequest(panel, "ORD-8", email, uuid, "sub-8"), CancellationToken.None);

        Assert.Equal(TenantCardProvisionalFinalizationStatus.AlreadyFinalized, result.Status);
        Assert.Equal(PurchasedQuota, panel.ReadClient(email)["totalGB"]!.Value<long>());
        Assert.Equal(0, panel.ReadTraffic(email)["up"]!.Value<long>());
        Assert.Equal(0, panel.ReadTraffic(email)["down"]!.Value<long>());
    }

    /// <summary>Proves one order's saga cannot touch another order's client, including one on the same panel.</summary>
    [Fact]
    public async Task Finalization_is_isolated_between_orders()
    {
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        const string firstEmail = "provisional-9a";
        const string secondEmail = "provisional-9b";
        SeedProvisionalClient(panel, firstEmail, "aaaaaaaa-1111-1111-1111-111111111111", "sub-9a");
        SeedProvisionalClient(panel, secondEmail, "bbbbbbbb-2222-2222-2222-222222222222", "sub-9b");
        var (service, _) = CreateFinalizer(databases);

        var result = await service.FinalizeAsync(
            BuildRequest(panel, "ORD-9A", firstEmail, "aaaaaaaa-1111-1111-1111-111111111111", "sub-9a"),
            CancellationToken.None);

        Assert.Equal(TenantCardProvisionalFinalizationStatus.Finalized, result.Status);

        // The untouched order still carries its provisional quota and its courtesy usage.
        var other = panel.ReadClient(secondEmail);
        Assert.Equal(OneGib, other["totalGB"]!.Value<long>());
        Assert.Equal(300_000_000, other["up"]!.Value<long>());
        Assert.Equal(200_000_000, other["down"]!.Value<long>());
    }

    /// <summary>
    /// Proves a lifetime order keeps the panel's lifetime representation instead of being given a computed expiry.
    /// </summary>
    [Fact]
    public async Task Finalization_preserves_lifetime_semantics_for_an_unlimited_purchase()
    {
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        const string email = "provisional-10";
        const string uuid = "cccccccc-3333-3333-3333-333333333333";
        SeedProvisionalClient(panel, email, uuid, "sub-10");
        var (service, _) = CreateFinalizer(databases);

        var request = new TenantCardProvisionalFinalizationRequest(
            TenantBotOrderId: 4242,
            OrderId: "ORD-10",
            ServerInfo: panel.ServerInfo,
            ExpectedEmail: email,
            ExpectedUuid: uuid,
            ExpectedSubId: "sub-10",
            ExpectedInboundIds: Array.Empty<int>(),
            PurchasedQuotaBytes: 0,
            PurchasedDurationDays: null);

        var result = await service.FinalizeAsync(request, CancellationToken.None);

        Assert.Equal(TenantCardProvisionalFinalizationStatus.Finalized, result.Status);
        var client = panel.ReadClient(email);
        // Zero quota means unlimited and zero expiry means lifetime; neither may be replaced by a computed date.
        Assert.Equal(0, client["totalGB"]!.Value<long>());
        Assert.Equal(0, client["expiryTime"]!.Value<long>());
        Assert.True(client["enable"]!.Value<bool>());
    }

    /// <summary>Proves the durable row ends at the proven step so a settlement tail can gate on it.</summary>
    [Fact]
    public async Task Finalization_records_the_proven_step_on_the_durable_row()
    {
        using var databases = new Databases();
        await using var panel = await FakeUpstreamPanel.StartAsync();
        const string email = "provisional-11";
        const string uuid = "dddddddd-4444-4444-4444-444444444444";
        SeedProvisionalClient(panel, email, uuid, "sub-11");
        var (service, store) = CreateFinalizer(databases);

        await service.FinalizeAsync(BuildRequest(panel, "ORD-11", email, uuid, "sub-11"), CancellationToken.None);

        var row = await store.FindAsync(FinalizeKey("ORD-11"), CancellationToken.None);
        Assert.NotNull(row);
        Assert.Equal(TenantCardProvisionalSteps.Proven, row!.Step);
        Assert.NotNull(row.FinalizationEffectiveAtUtc);
        Assert.Equal(PurchasedQuota, row.FinalQuotaBytes);
        Assert.NotNull(row.FinalExpiryTimeMs);
        Assert.NotNull(row.ProvenAtUtc);
        Assert.NotNull(row.CompletedAtUtc);
        Assert.True(TenantCardProvisionalSteps.HasReached(row.Step, TenantCardProvisionalSteps.QuotaWritten));
        Assert.False(string.Equals(row.Step, TenantCardProvisionalSteps.ManualReview, StringComparison.Ordinal));
    }
}
