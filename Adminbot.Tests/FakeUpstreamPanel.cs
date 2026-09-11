using Adminbot.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

/// <summary>
/// In-process fake 3x-ui panel that models the traffic and enable semantics proven from upstream
/// MHSanaei/3x-ui v3.7.0 source.
/// </summary>
/// <remarks>
/// <para>
/// The model deliberately reproduces upstream's most surprising behaviours so the provisional saga cannot be designed
/// around a convenient fiction:
/// <list type="bullet">
/// <item>the counter endpoint binds only <c>upload</c>/<c>download</c>, so a body spelled <c>up</c>/<c>down</c> writes
/// zeros (matching the silent-data-loss hazard that previously wiped customer usage on a link change);</item>
/// <item>traffic reads raise <c>up</c>/<c>down</c> to the maximum of the shared row and any master-pushed global row, so
/// a counter write cannot clear pushed usage;</item>
/// <item>the official reset zeroes the shared row and deletes every global and per-inbound node row;</item>
/// <item><b>the reset only force-enables and only re-admits the runtime user when the client was disabled when the reset
/// ran</b>, mirroring <c>if !rec.Enable</c> in <c>ClientService.ResetTrafficByEmail</c> and <c>if !traffic.Enable</c> in
/// <c>InboundService.resetClientTrafficLocked</c>.</item>
/// </list>
/// </para>
/// <para>
/// The panel is not a panel emulator and serves only the endpoints these tests exercise. The
/// <see cref="RuntimeAddUserCount" /> and <see cref="RuntimeRemoveUserCount" /> counters exist so a test can assert that
/// a mutation did not re-admit a credential, which is the property the finalization saga is built on.
/// </para>
/// </remarks>
internal sealed class FakeUpstreamPanel : IAsyncDisposable
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

    /// <summary>Gets the number of client updates observed.</summary>
    public int UpdateRequestCount { get; private set; }

    /// <summary>Gets the number of counter writes observed.</summary>
    public int CounterWriteRequestCount { get; private set; }

    /// <summary>
    /// Gets the number of times a request changed a client's enable flag.
    /// </summary>
    /// <remarks>
    /// A finalization saga that never disables the client expects at most one enable transition, and expects none at all
    /// when the client was already enabled.
    /// </remarks>
    public int EnableMutationCount { get; private set; }

    /// <summary>Gets the number of runtime <c>AddUser</c> admissions the panel would have performed.</summary>
    public int RuntimeAddUserCount { get; private set; }

    /// <summary>Gets the number of runtime <c>RemoveUser</c> admissions the panel would have performed.</summary>
    public int RuntimeRemoveUserCount { get; private set; }

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

    /// <summary>Seeds a client's shared traffic row and client configuration.</summary>
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

    /// <summary>Sets the quota and expiry a client currently carries.</summary>
    /// <param name="email">Client email.</param>
    /// <param name="totalGb">Absolute quota in bytes currently configured on the client.</param>
    /// <param name="expiryTimeMs">Absolute expiry in Unix milliseconds, or <c>0</c> for lifetime.</param>
    public void SetClientQuota(string email, long totalGb, long expiryTimeMs)
    {
        lock (_sync)
        {
            _clients[email]["totalGB"] = totalGb;
            _clients[email]["expiryTime"] = expiryTimeMs;
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

    /// <summary>Simulates the customer consuming transfer, which the periodic traffic poll would report.</summary>
    /// <param name="email">Client email.</param>
    /// <param name="up">Uploaded bytes to add.</param>
    /// <param name="down">Downloaded bytes to add.</param>
    /// <remarks>
    /// Used to model a customer who is still actively using the provisional account while finalization runs, so a test
    /// can prove which intermediate states could expose usable service.
    /// </remarks>
    public void ConsumeTraffic(string email, long up, long down)
    {
        lock (_sync)
        {
            _clients[email]["up"] = _clients[email]["up"]!.Value<long>() + up;
            _clients[email]["down"] = _clients[email]["down"]!.Value<long>() + down;
        }
    }

    /// <summary>Overwrites a client's recorded usage counters.</summary>
    /// <param name="email">Client email.</param>
    /// <param name="up">Absolute uploaded bytes to record.</param>
    /// <param name="down">Absolute downloaded bytes to record.</param>
    /// <remarks>
    /// Used to model a panel state a saga resumes into, such as a reset that was applied before the process crashed.
    /// </remarks>
    public void SetUsage(string email, long up, long down)
    {
        lock (_sync)
        {
            _clients[email]["up"] = up;
            _clients[email]["down"] = down;
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
                CounterWriteRequestCount++;
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
                    // Upstream guard #1: the reset re-enables, and therefore re-admits, only a client that was
                    // disabled when the reset ran. An already-enabled client produces no enable mutation at all.
                    if (!_clients[email]["enable"]!.Value<bool>())
                    {
                        _clients[email]["enable"] = true;
                        EnableMutationCount++;
                        RuntimeAddUserCount++;
                    }

                    _clients[email]["up"] = 0;
                    _clients[email]["down"] = 0;
                    // And it deletes master-pushed global rows plus per-inbound node rows.
                    _globalTraffic.Remove(email);
                    _nodeTraffic.Remove(email);
                }

                await WriteSuccessAsync(context, new JObject());
                return;
            }

            if (path.Contains("/update/", StringComparison.OrdinalIgnoreCase))
            {
                UpdateRequestCount++;
                var payload = string.IsNullOrWhiteSpace(body) ? new JObject() : JObject.Parse(body);
                lock (_sync)
                {
                    var wasEnabled = _clients[email]["enable"]!.Value<bool>();
                    foreach (var property in payload.Properties())
                    {
                        // The panel's client-update contract never carries usage counters; those live in
                        // client_traffics and are written only by the traffic endpoints. Modeling that keeps a stale
                        // read-back value in the update payload from clobbering the counters.
                        if (string.Equals(property.Name, "up", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(property.Name, "down", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // `id` is the protocol UUID in the update contract, not the numeric record id.
                        if (string.Equals(property.Name, "id", StringComparison.OrdinalIgnoreCase))
                        {
                            _clients[email]["uuid"] = property.Value.DeepClone();
                            continue;
                        }

                        _clients[email][property.Name] = property.Value.DeepClone();
                    }

                    var isEnabled = _clients[email]["enable"]!.Value<bool>();
                    if (wasEnabled != isEnabled)
                    {
                        EnableMutationCount++;
                        if (isEnabled)
                            RuntimeAddUserCount++;
                        else
                            RuntimeRemoveUserCount++;
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
