using System.Net.Http.Headers;
using System.Text;
using Adminbot.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;
using Xunit;

/// <summary>
/// Pins the exact wire contract, the post-reset panel semantics, and the reset's enable guards for the client-traffic and
/// client-update endpoints the provisional tenant card flow depends on.
/// </summary>
/// <remarks>
/// <b>Why these tests exist.</b> The provisional flow must, after owner approval, leave the SAME panel client carrying
/// exactly the ordered quota with zero provisional usage counted against it. That requires knowing precisely (a) what
/// field names the panel binds for a counter write and (b) what a reset actually clears. Both were previously assumed
/// incorrectly: our counter write sent <c>{ up, down }</c> while the panel binds <c>{ upload, download }</c>, and a reset
/// was assumed to leave a disabled client disabled, and the disable-before-reset ordering that assumption implied would
/// have turned the reset's own auto-enable into the one mutation that re-admits a credential mid-saga.
///
/// <b>What is proven from upstream source (not from these tests).</b> Against MHSanaei/3x-ui v3.7.0 and v3.4.2:
/// <list type="bullet">
/// <item><c>ClientController.updateTrafficByEmail</c> binds a struct with <c>json:"upload"</c> and
/// <c>json:"download"</c>, then calls <c>InboundService.UpdateClientTrafficByEmail</c>, a single
/// <c>UPDATE client_traffics SET up = ?, down = ? WHERE email = ?</c>.</item>
/// <item><c>ClientService.ResetTrafficByEmail</c> resolves every inbound the client is attached to, <b>auto-enables a
/// disabled client</b> through the normal update path, then per inbound zeroes <c>client_traffics</c>, forces
/// <c>enable = true</c>, deletes master-pushed <c>client_global_traffics</c> rows, deletes <c>node_client_traffics</c>
/// rows, bumps <c>last_traffic_reset_time</c>, and marks remote nodes dirty.</item>
/// <item><b>Both re-admission paths are guarded on the client being disabled:</b> <c>ResetTrafficByEmail</c> runs its
/// enabling update only under <c>if !rec.Enable</c>, and <c>InboundService.resetClientTrafficLocked</c> builds its runtime
/// <c>AddUser</c> plan only under <c>if !traffic.Enable</c>. An already-enabled client therefore makes the reset perform no
/// enable mutation and touch no running Xray user. Because <c>enable</c> is 3x-ui's only instantaneous admission gate
/// (quota and expiry are enforced by the periodic traffic poll, not by Xray when a connection opens), this guard is why
/// the finalization saga reaches the reset with an enabled client rather than disabling first.</item>
/// </list>
///
/// <b>Honest limitation.</b> No real 3x-ui panel is reachable from this test project, so these tests cannot observe
/// upstream Go code executing. They prove two different things instead: that OUR client emits the field names and paths
/// upstream requires (observed on a real loopback HTTP request), and that the saga design is correct <i>given</i> the
/// upstream semantics above, which are encoded in <see cref="FakeUpstreamPanel" />. The semantic half is a model, and the
/// model is only as good as the cited upstream source. That distinction is deliberate rather than hidden.
/// </remarks>
public sealed class XuiV3TrafficContractTests
{
    /// <summary>One GiB in bytes; the exact quota a provisional "1 GB" promise must produce.</summary>
    private const long OneGibBytes = 1073741824L;

    /// <summary>
    /// Proves our counter write reaches the panel with the field names the panel actually binds, and that the previously
    /// used names are absent from the wire body.
    /// </summary>
    /// <remarks>
    /// This is the direct regression guard for the silent-data-loss bug: the panel ignores unknown JSON fields and
    /// answers HTTP 200, so a body spelled <c>up</c>/<c>down</c> would zero the counters instead of restoring them. The
    /// link-change traffic-preservation call site depended on that write actually storing the values.
    /// </remarks>
    [Fact]
    public async Task Counter_write_uses_upload_download_property_names()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        panel.SeedClient("cust-1", up: 111, down: 222);
        var configuration = BuildConfiguration();

        var response = await ApiServicev3.UpdateClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-1", 4444, 5555);

        Assert.True(response.Success, response.Msg);
        var body = Assert.IsType<string>(panel.LastRawBody);
        var parsed = JObject.Parse(body);

        // Field names the panel binds. A rename must fail here rather than silently write zeros in production.
        Assert.Equal(4444, parsed["upload"]!.Value<long>());
        Assert.Equal(5555, parsed["download"]!.Value<long>());
        // The old, silently-ignored names must not be present at all.
        Assert.Null(parsed["up"]);
        Assert.Null(parsed["down"]);

        // And the write really stored the values in the model, so the counters were not zeroed.
        var stored = panel.ReadTraffic("cust-1");
        Assert.Equal(4444, stored["up"]!.Value<long>());
        Assert.Equal(5555, stored["down"]!.Value<long>());
    }

    /// <summary>
    /// Proves a counter write alone cannot clear usage that the panel tracks outside the shared traffic row.
    /// </summary>
    /// <remarks>
    /// This is the proof that forces the finalization saga onto the official reset rather than a
    /// <c>UpdateClientTraffic(0, 0)</c> shortcut. A client whose usage is master-pushed from a parent panel keeps a
    /// <c>client_global_traffics</c> row; read paths raise <c>up</c>/<c>down</c> to the maximum of fresh global rows, so
    /// zeroing the local row still reads back as consumed traffic and would silently reduce the purchased allowance
    /// derived from it.
    /// </remarks>
    [Fact]
    public async Task Counter_write_cannot_clear_master_pushed_global_traffic()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        panel.SeedClient("cust-global", up: 111, down: 222);
        panel.PushGlobalTraffic("cust-global", up: 900, down: 800);
        var configuration = BuildConfiguration();

        var response = await ApiServicev3.UpdateClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-global", 0, 0);
        Assert.True(response.Success, response.Msg);

        var readBack = await ApiServicev3.GetClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-global");
        Assert.True(readBack.Success, readBack.Msg);

        // The local row is zero, but the offline overlay still reports the consumed usage.
        Assert.Equal(0, panel.ReadTraffic("cust-global")["up"]!.Value<long>());
        Assert.Equal(900, readBack.Obj.Up);
        Assert.Equal(800, readBack.Obj.Down);
    }

    /// <summary>
    /// Proves the official reset clears every usage source AND leaves the client enabled, which is the side effect the
    /// saga has to design around instead of assuming away.
    /// </summary>
    /// <remarks>
    /// <b>Read this together with the guard proofs below.</b> The enable side effect fires only when the client was
    /// disabled when the reset ran, so the ordering that avoids re-admitting a credential is the one that reaches the
    /// reset with the client already enabled — not the intuitive disable-then-reset sequence, which makes the reset's own
    /// auto-enable the single mutation that restores access.
    /// </remarks>
    [Fact]
    public async Task Reset_clears_all_usage_sources_and_enables_client()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        panel.SeedClient("cust-reset", up: 111, down: 222, enable: false);
        panel.PushGlobalTraffic("cust-reset", up: 900, down: 800);
        panel.PushNodeTraffic("cust-reset", inboundId: 3, up: 700, down: 600);
        panel.PushNodeTraffic("cust-reset", inboundId: 9, up: 70, down: 60);
        var configuration = BuildConfiguration();

        var response = await ApiServicev3.ResetClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-reset");
        Assert.True(response.Success, response.Msg);

        var readBack = await ApiServicev3.GetClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-reset");
        Assert.True(readBack.Success, readBack.Msg);

        // Every usage source is cleared, including the sources a counter write cannot reach.
        Assert.Equal(0, readBack.Obj.Up);
        Assert.Equal(0, readBack.Obj.Down);
        Assert.Empty(panel.GlobalTrafficRows("cust-reset"));
        Assert.Empty(panel.NodeTrafficRowsForAllInbounds("cust-reset"));

        // Contractual side effect: the reset force-enabled the client even though it was disabled before.
        Assert.True(readBack.Obj.Enable);
        Assert.True(panel.ReadTraffic("cust-reset")["enable"]!.Value<bool>());
    }

    /// <summary>
    /// Proves the reset is value-idempotent, so an ambiguous response can be reconciled by replaying it without
    /// double-counting usage.
    /// </summary>
    /// <remarks>
    /// This is what makes the reset the safe reconciliation step after a lost reset response: replay converges on the
    /// same zeroed, enabled state rather than compounding.
    /// </remarks>
    [Fact]
    public async Task Reset_is_value_idempotent_under_replay()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        panel.SeedClient("cust-replay", up: 111, down: 222);
        panel.PushGlobalTraffic("cust-replay", up: 900, down: 800);
        var configuration = BuildConfiguration();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var response = await ApiServicev3.ResetClientTrafficAsync(
                panel.ServerInfo, configuration, "cust-replay");
            Assert.True(response.Success, response.Msg);
        }

        var readBack = await ApiServicev3.GetClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-replay");
        Assert.True(readBack.Success, readBack.Msg);
        Assert.Equal(0, readBack.Obj.Up);
        Assert.Equal(0, readBack.Obj.Down);
        Assert.True(readBack.Obj.Enable);
        Assert.Equal(3, panel.ResetRequestCount);
    }

    /// <summary>
    /// Proves the reset fans out across every inbound a client is attached to, so a multi-inbound or remote-node client
    /// cannot retain per-inbound node usage after the reset.
    /// </summary>
    /// <remarks>
    /// A provisional client is created with the ordered plan's full inbound placement, which is commonly more than one
    /// inbound. Upstream deletes node accounting per attached inbound, so the model asserts that no inbound retains a
    /// node row after the reset.
    /// </remarks>
    [Fact]
    public async Task Reset_clears_node_accounting_for_every_attached_inbound()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        panel.SeedClient("cust-multi", up: 5, down: 6);
        panel.AttachInbounds("cust-multi", 2, 4, 7);
        panel.PushNodeTraffic("cust-multi", inboundId: 2, up: 100, down: 100);
        panel.PushNodeTraffic("cust-multi", inboundId: 4, up: 200, down: 200);
        panel.PushNodeTraffic("cust-multi", inboundId: 7, up: 300, down: 300);
        var configuration = BuildConfiguration();

        var response = await ApiServicev3.ResetClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-multi");
        Assert.True(response.Success, response.Msg);

        // The client stays attached to every inbound the ordered plan placed it on; only its node usage is cleared.
        var attachedInbounds = panel.ReadClient("cust-multi")["inboundIds"]!
            .Values<int>().Order().ToArray();
        Assert.Equal(new[] { 2, 4, 7 }, attachedInbounds);

        var nodeRows = panel.NodeTrafficRowsForAllInbounds("cust-multi");
        Assert.All(attachedInbounds, inboundId =>
            Assert.True(!nodeRows.TryGetValue(inboundId, out var rows) || rows.Count == 0,
                $"Inbound {inboundId} retained node traffic rows after the reset."));
    }

    /// <summary>
    /// Proves the disable/re-enable controls the saga needs before and after the reset preserve the client's credentials
    /// and subscription identity, so no step of the saga rotates the customer's UUID, password, or subId.
    /// </summary>
    /// <remarks>
    /// The proven saga reaches the reset with an enabled client and therefore never disables it, but the enable controls
    /// remain the only way to re-admit a provisional client the panel had already disabled (an exhausted quota or an
    /// elapsed provisional expiry disables it). If either direction dropped identity fields the customer's existing link
    /// would break during finalization, so this test pins identity preservation across both directions.
    /// </remarks>
    [Fact]
    public async Task Disable_and_reenable_preserve_uuid_email_and_sub_id()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        panel.SeedClient("cust-identity", up: 0, down: 0, enable: true);
        panel.SetClientIdentity("cust-identity", uuid: "11111111-2222-3333-4444-555555555555", subId: "sub-abc");
        var configuration = BuildConfiguration();

        var disabled = await ApiServicev3.SetClientEnabledAsync(
            panel.ServerInfo, configuration, "cust-identity", false);
        Assert.True(disabled.Success, disabled.Msg);
        Assert.False(panel.ReadClient("cust-identity")["enable"]!.Value<bool>());

        var enabled = await ApiServicev3.SetClientEnabledAsync(
            panel.ServerInfo, configuration, "cust-identity", true);
        Assert.True(enabled.Success, enabled.Msg);

        var client = panel.ReadClient("cust-identity");
        Assert.True(client["enable"]!.Value<bool>());
        Assert.Equal("11111111-2222-3333-4444-555555555555", client["uuid"]!.Value<string>());
        Assert.Equal("sub-abc", client["subId"]!.Value<string>());
        Assert.Equal("cust-identity", client["email"]!.Value<string>());
    }

    /// <summary>
    /// Proves the quota the saga writes after the reset is an absolute value in bytes, so the final client carries
    /// exactly the ordered entitlement rather than the ordered entitlement plus provisional usage.
    /// </summary>
    /// <remarks>
    /// This is the assertion that replaces the rejected <c>TotalGB = purchasedBytes + usedBytesAtFinalize</c> design. The
    /// reset zeroes usage first, so the absolute write can be the exact ordered quota and cannot drift upward.
    /// </remarks>
    [Fact]
    public async Task Final_quota_write_is_absolute_and_equals_exact_ordered_entitlement()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        panel.SeedClient("cust-quota", up: 0, down: 0);
        panel.SetClientIdentity("cust-quota", uuid: "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", subId: "sub-quota");
        var configuration = BuildConfiguration();
        var expiry = DateTimeOffset.UtcNow.AddDays(30).ToUnixTimeMilliseconds();

        // Proven ordering: the client stays enabled, the reset clears every usage source including the ones a counter
        // write cannot reach, a side-effect-free counter write re-proves zero, and only then is the exact quota written.
        // Disabling before the reset is deliberately absent: that is the sequence that would trigger the reset's
        // auto-enable branch and re-admit the credential.
        panel.ConsumeTraffic("cust-quota", up: 400_000_000, down: 300_000_000);
        Assert.True((await ApiServicev3.ResetClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-quota")).Success);
        Assert.True((await ApiServicev3.UpdateClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-quota", 0, 0)).Success);
        Assert.Equal(0, panel.EnableMutationCount);

        var update = await ApiServicev3.UpdateClientAsync(panel.ServerInfo, configuration, "cust-quota",
            new XuiV3ClientPayload
            {
                Email = "cust-quota",
                TotalGB = OneGibBytes * 50,
                ExpiryTime = expiry,
                Enable = true,
                SubId = "sub-quota",
                Uuid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
            });
        Assert.True(update.Success, update.Msg);

        var traffic = await ApiServicev3.GetClientTrafficAsync(panel.ServerInfo, configuration, "cust-quota");
        Assert.True(traffic.Success, traffic.Msg);

        // Exact ordered entitlement, with zero provisional usage deducted from it.
        Assert.Equal(OneGibBytes * 50, traffic.Obj.TotalGB);
        Assert.Equal(0, traffic.Obj.Up);
        Assert.Equal(0, traffic.Obj.Down);
        Assert.Equal(expiry, traffic.Obj.ExpiryTime);
        Assert.True(traffic.Obj.Enable);

        // Identity survived the whole sequence.
        var client = panel.ReadClient("cust-quota");
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", client["uuid"]!.Value<string>());
        Assert.Equal("sub-quota", client["subId"]!.Value<string>());
    }

    /// <summary>
    /// Proves the barrier the finalization saga depends on: the official reset performs no enable mutation and no
    /// runtime admission when the client it is given is already enabled.
    /// </summary>
    /// <remarks>
    /// This is the single most load-bearing contract in the finalize design. 3x-ui's <c>enable</c> flag is the only
    /// instantaneous admission gate — quota and expiry are enforced by the periodic traffic poll, not by Xray when a
    /// connection opens — so any mutation that flips <c>enable</c> back on is a mutation that re-admits a credential.
    /// Upstream guards both re-admission paths on the client being disabled (<c>if !rec.Enable</c> in
    /// <c>ClientService.ResetTrafficByEmail</c>, <c>if !traffic.Enable</c> in <c>InboundService.resetClientTrafficLocked</c>),
    /// which is exactly why the saga reaches the reset with the client enabled. If either guard ever becomes
    /// unconditional, this test fails and the saga must be redesigned rather than shipped.
    /// </remarks>
    [Fact]
    public async Task Reset_on_an_enabled_client_performs_no_enable_mutation_and_no_runtime_admission()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        panel.SeedClient("cust-barrier", up: 500_000_000, down: 400_000_000, enable: true);
        panel.PushGlobalTraffic("cust-barrier", up: 900_000_000, down: 800_000_000);
        panel.PushNodeTraffic("cust-barrier", inboundId: 2, up: 700, down: 600);
        var configuration = BuildConfiguration();

        var response = await ApiServicev3.ResetClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-barrier");
        Assert.True(response.Success, response.Msg);

        var readBack = await ApiServicev3.GetClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-barrier");
        Assert.True(readBack.Success, readBack.Msg);

        // The reset still does its job: every usage source is cleared.
        Assert.Equal(0, readBack.Obj.Up);
        Assert.Equal(0, readBack.Obj.Down);
        Assert.Empty(panel.GlobalTrafficRows("cust-barrier"));
        Assert.Empty(panel.NodeTrafficRowsForAllInbounds("cust-barrier"));
        Assert.True(readBack.Obj.Enable);

        // And it did none of it by touching enable or the running Xray user set.
        Assert.Equal(0, panel.EnableMutationCount);
        Assert.Equal(0, panel.RuntimeAddUserCount);
        Assert.Equal(0, panel.RuntimeRemoveUserCount);
    }

    /// <summary>
    /// Pins the opposite case: handed a disabled client, the official reset force-enables it and re-admits the runtime
    /// user.
    /// </summary>
    /// <remarks>
    /// The finalization saga must never produce this state, but the hazard has to stay proven rather than assumed — if
    /// the reset ever stopped re-enabling, the saga's ordering choice would no longer be the conservative one and the
    /// design would need re-deriving. The revoke saga, which deliberately disables the client, must also know that a
    /// later reset on that client would re-admit it.
    /// </remarks>
    [Fact]
    public async Task Reset_on_a_disabled_client_force_enables_and_rerecruits_the_runtime_user()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        panel.SeedClient("cust-readmit", up: 10, down: 20, enable: false);
        var configuration = BuildConfiguration();

        var response = await ApiServicev3.ResetClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-readmit");
        Assert.True(response.Success, response.Msg);

        var readBack = await ApiServicev3.GetClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-readmit");
        Assert.True(readBack.Success, readBack.Msg);

        Assert.True(readBack.Obj.Enable);
        Assert.Equal(1, panel.EnableMutationCount);
        Assert.Equal(1, panel.RuntimeAddUserCount);
    }

    /// <summary>
    /// Proves the counter write the saga uses to re-zero usage after the reset has no enable or runtime side effect.
    /// </summary>
    /// <remarks>
    /// This is what makes the post-reset re-zero safe: it erases usage accrued inside the reset window without ever
    /// re-admitting a credential, which the reset itself cannot promise.
    /// </remarks>
    [Fact]
    public async Task Counter_write_has_no_enable_or_runtime_side_effect()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        panel.SeedClient("cust-sideeffect", up: 111, down: 222, enable: false);
        var configuration = BuildConfiguration();

        var response = await ApiServicev3.UpdateClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-sideeffect", 0, 0);
        Assert.True(response.Success, response.Msg);

        Assert.Equal(0, panel.ReadTraffic("cust-sideeffect")["up"]!.Value<long>());
        // Still disabled, and nothing was added to or removed from the running runtime.
        Assert.False(panel.ReadTraffic("cust-sideeffect")["enable"]!.Value<bool>());
        Assert.Equal(0, panel.EnableMutationCount);
        Assert.Equal(0, panel.RuntimeAddUserCount);
        Assert.Equal(0, panel.RuntimeRemoveUserCount);
    }

    /// <summary>
    /// Regression for the silent usage loss on the link-change traffic-preservation path.
    /// </summary>
    /// <remarks>
    /// The link-change repair path reads a client's counters, rewrites the client, and restores the previously observed
    /// usage by calling <see cref="ApiServicev3.UpdateClientTrafficAsync" />. While that call sent <c>up</c>/<c>down</c>,
    /// the panel ignored both fields, answered HTTP 200, and wrote zeros, so every link change silently wiped the
    /// customer's usage. This test reproduces that exact sequence — read, update, restore, read — and asserts the restored
    /// bytes really are the bytes that were read before the mutation.
    /// </remarks>
    [Fact]
    public async Task Link_change_traffic_preservation_restores_previously_recorded_usage()
    {
        await using var panel = await FakeUpstreamPanel.StartAsync();
        panel.SeedClient("cust-preserve", up: 12_345_678, down: 87_654_321);
        panel.SetClientIdentity("cust-preserve", uuid: "99999999-8888-7777-6666-555555555555", subId: "sub-preserve");
        var configuration = BuildConfiguration();

        var before = await ApiServicev3.GetClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-preserve");
        Assert.True(before.Success, before.Msg);
        var preservedUp = before.Obj.Up;
        var preservedDown = before.Obj.Down;
        Assert.NotEqual(0, preservedUp);
        Assert.NotEqual(0, preservedDown);

        // The link-change path rewrites the client, then restores the usage it read before the rewrite.
        var update = await ApiServicev3.UpdateClientAsync(panel.ServerInfo, configuration, "cust-preserve",
            new XuiV3ClientPayload
            {
                Email = "cust-preserve",
                TotalGB = OneGibBytes,
                ExpiryTime = 0,
                Enable = true,
                SubId = "sub-preserve",
                Uuid = "99999999-8888-7777-6666-555555555555"
            });
        Assert.True(update.Success, update.Msg);

        var restore = await ApiServicev3.UpdateClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-preserve", preservedUp, preservedDown);
        Assert.True(restore.Success, restore.Msg);

        var after = await ApiServicev3.GetClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-preserve");
        Assert.True(after.Success, after.Msg);

        // With the corrected body these round-trip; with the old `up`/`down` body both reads were zero.
        Assert.Equal(preservedUp, after.Obj.Up);
        Assert.Equal(preservedDown, after.Obj.Down);
        Assert.NotEqual(0, after.Obj.Up);
        Assert.NotEqual(0, after.Obj.Down);
    }

    /// <summary>Creates the configuration used by the contract tests, with retries disabled so tests are deterministic.</summary>
    /// <returns>In-memory configuration with no live panel or provider credentials.</returns>
    private static IConfiguration BuildConfiguration()
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["XuiV3ApiToken"] = "test-only",
            ["xuiV3TransientRetryCount"] = "0",
            ["xuiV3RequestTimeoutSeconds"] = "5"
        }).Build();
}
