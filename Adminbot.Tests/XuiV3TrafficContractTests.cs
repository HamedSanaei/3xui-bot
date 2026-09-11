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
/// Pins the exact wire contract and post-reset panel semantics of the two client-traffic endpoints the provisional
/// tenant card flow depends on: <c>POST /panel/api/clients/updateTraffic/{email}</c> and
/// <c>POST /panel/api/clients/resetTraffic/{email}</c>.
/// </summary>
/// <remarks>
/// <b>Why these tests exist.</b> The provisional flow must, after owner approval, leave the SAME panel client carrying
/// exactly the ordered quota with zero provisional usage counted against it. That requires knowing precisely (a) what
/// field names the panel binds for a counter write and (b) what a reset actually clears. Both were previously assumed
/// incorrectly: our counter write sent <c>{ up, down }</c> while the panel binds <c>{ upload, download }</c>, and a reset
/// was assumed to leave a disabled client disabled.
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
    /// The enable side effect is the reason the ordering is disable-then-reset rather than reset-then-disable: the reset
    /// would undo a preceding disable. The saga therefore treats "reset" as the point where the client becomes live
    /// again, not as a step that preserves a disabled flag.
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
    /// The saga disables the client so traffic cannot accrue while counters transition, then re-enables the same client.
    /// If either write dropped identity fields the customer's existing link would break mid-finalization, so this test
    /// pins identity preservation across both directions.
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

        // Ordering the saga uses: disable -> reset (this is where usage is zeroed) -> write exact quota on the SAME client.
        Assert.True((await ApiServicev3.SetClientEnabledAsync(
            panel.ServerInfo, configuration, "cust-quota", false)).Success);
        Assert.True((await ApiServicev3.ResetClientTrafficAsync(
            panel.ServerInfo, configuration, "cust-quota")).Success);

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

    /// <summary>Creates the configuration used by the contract tests, with retries disabled so tests are deterministic.</summary>
    /// <returns>In-memory configuration with no live panel or provider credentials.</returns>
    private static IConfiguration BuildConfiguration()
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["XuiV3ApiToken"] = "test-only",
            ["xuiV3TransientRetryCount"] = "0",
            ["xuiV3RequestTimeoutSeconds"] = "5"
        }).Build();

    /// <summary>
    /// In-process fake 3x-ui panel that models the traffic semantics proven from upstream v3.7.0/v3.4.2 source.
    /// </summary>
    /// <remarks>
    /// The model deliberately reproduces upstream's most surprising behaviours so the saga cannot be designed around a
    /// convenient fiction:
    /// <list type="bullet">
    /// <item>the counter endpoint binds only <c>upload</c>/<c>download</c>, so a body spelled <c>up</c>/<c>down</c>
    /// writes zeros (matching the silent-data-loss hazard);</item>
    /// <item>traffic reads raise <c>up</c>/<c>down</c> to the maximum of the shared row and any master-pushed global
    /// row, so a counter write cannot clear pushed usage;</item>
    /// <item>the official reset zeroes the shared row, force-enables the client, and deletes every global and per-inbound
    /// node row.</item>
    /// </list>
    /// The panel is not a panel emulator and serves only the endpoints these tests exercise.
    /// </remarks>
    private sealed class FakeUpstreamPanel : IAsyncDisposable
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, JObject> _clients = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<JObject>> _globalTraffic = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Dictionary<int, List<JObject>>> _nodeTraffic = new(StringComparer.OrdinalIgnoreCase);

        private WebApplication? _app;

        /// <summary>Gets the loopback base URL the fake panel listens on.</summary>
        public string Url { get; private set; } = string.Empty;

        /// <summary>Gets connection settings pointing at this fake panel.</summary>
        public ServerInfo ServerInfo { get; private set; } = new();

        /// <summary>Gets the raw body of the most recent request that carried one.</summary>
        public string? LastRawBody { get; private set; }

        /// <summary>Gets the number of reset requests observed.</summary>
        public int ResetRequestCount { get; private set; }

        /// <summary>Starts the fake panel on an ephemeral loopback port.</summary>
        /// <returns>The started panel.</returns>
        public static async Task<FakeUpstreamPanel> StartAsync()
        {
            var panel = new FakeUpstreamPanel();
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            var app = builder.Build();
            app.Run(async context => await panel.HandleAsync(context));
            await app.StartAsync();
            panel._app = app;
            panel.Url = app.Urls.Single();
            panel.ServerInfo = new ServerInfo { Url = panel.Url, ApiToken = "test-only" };
            return panel;
        }

        /// <summary>Seeds a client's shared traffic row.</summary>
        /// <param name="email">Client email, the stable key of the shared traffic row.</param>
        /// <param name="up">Initial uploaded bytes.</param>
        /// <param name="down">Initial downloaded bytes.</param>
        /// <param name="enable">Initial enable flag.</param>
        public void SeedClient(string email, long up, long down, bool enable = true)
        {
            lock (_sync)
            {
                _clients[email] = new JObject
                {
                    ["id"] = 1,
                    ["email"] = email,
                    ["up"] = up,
                    ["down"] = down,
                    ["enable"] = enable,
                    ["totalGB"] = 0,
                    ["expiryTime"] = 0,
                    ["uuid"] = string.Empty,
                    ["subId"] = email
                };
            }
        }

        /// <summary>Sets the identity fields that must survive finalization.</summary>
        /// <param name="email">Client email.</param>
        /// <param name="uuid">Client UUID that the existing subscription link embeds.</param>
        /// <param name="subId">Client subscription id.</param>
        public void SetClientIdentity(string email, string uuid, string subId)
        {
            lock (_sync)
            {
                _clients[email]["uuid"] = uuid;
                _clients[email]["subId"] = subId;
            }
        }

        /// <summary>Records the inbounds a client is attached to, mirroring upstream per-inbound fanout.</summary>
        /// <param name="email">Client email.</param>
        /// <param name="inboundIds">Inbound ids the client is attached to.</param>
        public void AttachInbounds(string email, params int[] inboundIds)
        {
            lock (_sync)
            {
                var ids = inboundIds.Select(value => (long)value).ToArray();
                _clients[email]["inboundIds"] = new JArray(ids);
            }
        }

        /// <summary>Adds a master-pushed global traffic row that overrides a counter write.</summary>
        /// <param name="email">Client email.</param>
        /// <param name="up">Uploaded bytes reported by the parent panel.</param>
        /// <param name="down">Downloaded bytes reported by the parent panel.</param>
        public void PushGlobalTraffic(string email, long up, long down)
        {
            lock (_sync)
            {
                if (!_globalTraffic.TryGetValue(email, out var rows))
                    _globalTraffic[email] = rows = new List<JObject>();
                rows.Add(new JObject { ["up"] = up, ["down"] = down });
            }
        }

        /// <summary>Adds a per-inbound node traffic row.</summary>
        /// <param name="email">Client email.</param>
        /// <param name="inboundId">Inbound the node accounting belongs to.</param>
        /// <param name="up">Uploaded bytes recorded on the node.</param>
        /// <param name="down">Downloaded bytes recorded on the node.</param>
        public void PushNodeTraffic(string email, int inboundId, long up, long down)
        {
            lock (_sync)
            {
                if (!_nodeTraffic.TryGetValue(email, out var byInbound))
                    _nodeTraffic[email] = byInbound = new Dictionary<int, List<JObject>>();
                if (!byInbound.TryGetValue(inboundId, out var rows))
                    byInbound[inboundId] = rows = new List<JObject>();
                rows.Add(new JObject { ["up"] = up, ["down"] = down });
            }
        }

        /// <summary>Reads the shared traffic row for a client.</summary>
        /// <param name="email">Client email.</param>
        /// <returns>The shared traffic row.</returns>
        public JObject ReadTraffic(string email)
        {
            lock (_sync)
                return (JObject)_clients[email].DeepClone();
        }

        /// <summary>Reads the full client record for a client.</summary>
        /// <param name="email">Client email.</param>
        /// <returns>The stored client record.</returns>
        public JObject ReadClient(string email)
        {
            lock (_sync)
                return (JObject)_clients[email].DeepClone();
        }

        /// <summary>Reads the master-pushed global traffic rows for a client.</summary>
        /// <param name="email">Client email.</param>
        /// <returns>Global traffic rows; empty when none exist.</returns>
        public IReadOnlyList<JObject> GlobalTrafficRows(string email)
        {
            lock (_sync)
                return _globalTraffic.TryGetValue(email, out var rows) ? rows.ToArray() : Array.Empty<JObject>();
        }

        /// <summary>Reads node traffic rows keyed by inbound for a client.</summary>
        /// <param name="email">Client email.</param>
        /// <returns>Per-inbound node rows; missing inbounds map to an empty list.</returns>
        public IReadOnlyDictionary<int, IReadOnlyList<JObject>> NodeTrafficRowsForAllInbounds(string email)
        {
            lock (_sync)
            {
                var result = new Dictionary<int, IReadOnlyList<JObject>>();
                if (!_nodeTraffic.TryGetValue(email, out var byInbound))
                    return result;

                foreach (var pair in byInbound)
                    result[pair.Key] = pair.Value.ToArray();

                return result;
            }
        }

        /// <summary>Dispatches one fake panel request.</summary>
        /// <param name="context">Request being served.</param>
        /// <returns>A task that completes when the response has been written.</returns>
        private async Task HandleAsync(HttpContext context)
        {
            var path = context.Request.Path.Value ?? string.Empty;
            var email = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);

            if (context.Request.Method == "POST")
            {
                var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
                LastRawBody = body;

                if (path.Contains("/updateTraffic/", StringComparison.OrdinalIgnoreCase))
                {
                    // Upstream binds ONLY these two field names. Unknown names are ignored and the counters are
                    // overwritten with the bound defaults, which is exactly the silent zero-write hazard.
                    var payload = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
                    lock (_sync)
                    {
                        _clients[email]["up"] = payload.Value<long?>("upload") ?? 0;
                        _clients[email]["down"] = payload.Value<long?>("download") ?? 0;
                    }

                    await WriteSuccessAsync(context, new JObject());
                    return;
                }

                if (path.Contains("/resetTraffic/", StringComparison.OrdinalIgnoreCase))
                {
                    ResetRequestCount++;
                    lock (_sync)
                    {
                        _clients[email]["up"] = 0;
                        _clients[email]["down"] = 0;
                        // Contractual upstream side effect: the reset force-enables a disabled client.
                        _clients[email]["enable"] = true;
                        // And it deletes master-pushed global rows plus per-inbound node rows.
                        _globalTraffic.Remove(email);
                        _nodeTraffic.Remove(email);
                    }

                    await WriteSuccessAsync(context, new JObject());
                    return;
                }

                if (path.Contains("/update/", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
                    lock (_sync)
                    {
                        foreach (var property in payload.Properties())
                        {
                            if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase))
                                continue;
                            _clients[email][property.Name] = property.Value.DeepClone();
                        }
                    }

                    await WriteSuccessAsync(context, new JObject());
                    return;
                }

                await WriteSuccessAsync(context, new JObject());
                return;
            }

            if (path.Contains("/traffic/", StringComparison.OrdinalIgnoreCase))
            {
                await WriteSuccessAsync(context, BuildTrafficView(email));
                return;
            }

            if (path.Contains("/get/", StringComparison.OrdinalIgnoreCase))
            {
                await WriteSuccessAsync(context, ReadClient(email));
                return;
            }

            if (path.Contains("/list", StringComparison.OrdinalIgnoreCase))
            {
                JArray list;
                lock (_sync)
                    list = new JArray(_clients.Values.Select(item => item.DeepClone()));

                await WriteSuccessAsync(context, list);
                return;
            }

            await WriteSuccessAsync(context, new JObject());
        }

        /// <summary>Builds the traffic view the panel returns, including the master-pushed overlay.</summary>
        /// <param name="email">Client email.</param>
        /// <returns>The traffic object a read returns.</returns>
        private JObject BuildTrafficView(string email)
        {
            lock (_sync)
            {
                var client = _clients[email];
                var up = client["up"]!.Value<long>();
                var down = client["down"]!.Value<long>();

                if (_globalTraffic.TryGetValue(email, out var globals))
                {
                    up = Math.Max(up, globals.Max(row => row["up"]!.Value<long>()));
                    down = Math.Max(down, globals.Max(row => row["down"]!.Value<long>()));
                }

                return new JObject
                {
                    ["email"] = email,
                    ["up"] = up,
                    ["down"] = down,
                    ["total"] = client["totalGB"]!.Value<long>(),
                    ["totalGB"] = client["totalGB"]!.Value<long>(),
                    ["expiryTime"] = client["expiryTime"]!.Value<long>(),
                    ["enable"] = client["enable"]!.Value<bool>()
                };
            }
        }

        /// <summary>Writes a successful panel envelope.</summary>
        /// <param name="context">Request being served.</param>
        /// <param name="obj">Object to place in the envelope's <c>obj</c> property.</param>
        /// <returns>A task that completes when the response has been written.</returns>
        private static async Task WriteSuccessAsync(HttpContext context, JToken obj)
        {
            context.Response.ContentType = "application/json";
            context.Response.StatusCode = StatusCodes.Status200OK;
            await context.Response.WriteAsync(new JObject { ["success"] = true, ["obj"] = obj }.ToString());
        }

        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            if (_app != null)
                await _app.StopAsync();
        }
    }
}
