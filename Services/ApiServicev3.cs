using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class ApiServicev3
{
    private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
    {
        NullValueHandling = NullValueHandling.Ignore
    };

    /// <summary>
    /// Detects whether a panel can answer the v3 API. If a version is forced on ServerInfo or configuration,
    /// that value wins. Otherwise it probes the v3 clients endpoint with Bearer auth, then falls back to v2 cookie auth.
    /// </summary>
    public static async Task<XuiPanelApiVersion> DetectPanelVersionAsync(
        ServerInfo serverInfo,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
        var forced = (serverInfo.ApiVersion ?? appConfig.XuiApiVersionMode ?? "auto").Trim().ToLowerInvariant();

        if (serverInfo.PanelMajorVersion >= 3 || forced == "v3" || forced == "3")
            return XuiPanelApiVersion.V3;

        if (serverInfo.PanelMajorVersion > 0 && serverInfo.PanelMajorVersion < 3 || forced == "v2" || forced == "2")
            return XuiPanelApiVersion.V2;

        try
        {
            var response = await GetClientsAsync(serverInfo, configuration, cancellationToken);
            if (response.Success)
                return XuiPanelApiVersion.V3;
        }
        catch
        {
            // v3 probe failed; cookie probe below decides whether the old API is still usable.
        }

        try
        {
            var cookie = await ApiService.LoginAndGetSessionCookie(serverInfo);
            if (!string.IsNullOrWhiteSpace(cookie))
                return XuiPanelApiVersion.V2;
        }
        catch
        {
        }

        return XuiPanelApiVersion.Unknown;
    }

    /// <summary>
    /// Creates a user account after automatic API version detection. v2 calls the existing ApiService;
    /// v3 uses Bearer auth and the first matching/mapped inbound IDs.
    /// </summary>
    public static async Task<XuiV3AccountCreationResult> CreateUserAccountAutoAsync(
        AccountDto accountDto,
        IConfiguration configuration,
        XuiV3CreateAccountOptions options = null,
        CancellationToken cancellationToken = default)
    {
        var version = await DetectPanelVersionAsync(accountDto.ServerInfo, configuration, cancellationToken);
        if (version == XuiPanelApiVersion.V2)
        {
            var ok = await ApiService.CreateUserAccount(accountDto);
            return new XuiV3AccountCreationResult
            {
                Success = ok,
                ApiVersion = version,
                Email = ok ? accountDto.TelegramUserId.ToString() : null
            };
        }

        if (version != XuiPanelApiVersion.V3)
        {
            return new XuiV3AccountCreationResult
            {
                Success = false,
                ApiVersion = version,
                Message = "Could not detect a usable 3X-UI API version."
            };
        }

        return await CreateUserAccountAsync(accountDto, configuration, options, cancellationToken);
    }

    /// <summary>
    /// Creates a v3 client and attaches it to one or more inbounds.
    /// The inbound IDs must be supplied explicitly through options.InboundIds.
    /// </summary>
    public static async Task<XuiV3AccountCreationResult> CreateUserAccountAsync(
        AccountDto accountDto,
        IConfiguration configuration,
        XuiV3CreateAccountOptions options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new XuiV3CreateAccountOptions();

        var inboundIds = options.InboundIds?.Distinct().ToList() ?? new List<int>();
        if (inboundIds.Count == 0)
            throw new InvalidOperationException("No inbound IDs were provided for the v3 account.");

        var trafficGb = options.TrafficGb > 0 ? options.TrafficGb : Convert.ToInt32(accountDto.TotoalGB);
        var trafficBytes = options.TrafficBytes > 0 ? options.TrafficBytes : ApiService.ConvertGBToBytes(trafficGb);
        var client = BuildClientPayload(accountDto, options);
        var response = await AddClientAsync(
            accountDto.ServerInfo,
            configuration,
            new XuiV3ClientCreateRequest { Client = client, InboundIds = inboundIds },
            cancellationToken);

        if (!response.Success)
        {
            return new XuiV3AccountCreationResult
            {
                Success = false,
                ApiVersion = XuiPanelApiVersion.V3,
                Email = client.Email,
                Message = response.Msg
            };
        }

        var panelClientResponse = await GetClientAsync(accountDto.ServerInfo, configuration, client.Email, cancellationToken);
        var panelClient = panelClientResponse.Success && panelClientResponse.Obj != null
            ? panelClientResponse.Obj
            : null;
        var links = await GetClientLinksAsync(accountDto.ServerInfo, configuration, client.Email, cancellationToken);
        var configLink = links.Obj?.FirstOrDefault();
        var linkUuid = TryExtractUuidFromConfigLink(configLink, out var extractedUuid) ? extractedUuid : null;
        var subLink = BuildSubscriptionLink(accountDto.ServerInfo, client.SubId ?? client.Email);

        if (options.SaveUserStatus)
        {
            var userDbContext = new UserDbContext();
            await userDbContext.SaveUserStatus(new User
            {
                Id = accountDto.TelegramUserId,
                ConfigLink = configLink,
                Email = client.Email,
                SubLink = subLink,
                SelectedCountry = accountDto.SelectedCountry,
                SelectedPeriod = accountDto.SelectedPeriod,
                TotoalGB = accountDto.TotoalGB,
                Type = accountDto.AccType,
                AccountCounter = accountDto.AccountCounter
            });
            await userDbContext.SaveChangesAsync(cancellationToken);
        }

        return new XuiV3AccountCreationResult
        {
            Success = true,
            ApiVersion = XuiPanelApiVersion.V3,
            Email = panelClient?.Email ?? client.Email,
            Uuid = FirstNonWhiteSpace(panelClient?.Uuid, linkUuid, client.Uuid),
            SubId = FirstNonWhiteSpace(panelClient?.SubId, client.SubId),
            ConfigLink = configLink,
            SubLink = subLink,
            TrafficGb = trafficGb,
            TrafficBytes = trafficBytes,
            ExpiryTime = panelClient?.ExpiryTime ?? client.ExpiryTime,
            DurationDays = options.DurationDays,
            Comment = FirstNonWhiteSpace(panelClient?.Comment, client.Comment),
            InboundIds = inboundIds,
            RawResponse = response.Obj
        };
    }

    /// <summary>POST /login. Cookie-based UI login. Bearer-token callers do not need this method.</summary>
    public static Task<XuiV3ApiResponse<JToken>> LoginAsync(
        ServerInfo serverInfo,
        string twoFactorCode = null,
        CancellationToken cancellationToken = default)
    {
        var body = new
        {
            username = serverInfo.Username,
            password = serverInfo.Password,
            twoFactorCode
        };

        return SendAsync<JToken>(serverInfo, null, HttpMethod.Post, "/login", body, false, cancellationToken);
    }

    /// <summary>POST /logout. Clears a cookie session. API-token callers normally do not need this.</summary>
    public static Task<XuiV3ApiResponse<JToken>> LogoutAsync(
        ServerInfo serverInfo,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        return SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/logout", null, true, cancellationToken);
    }

    /// <summary>GET /csrf-token. Browser session helper. Bearer-token API calls skip CSRF.</summary>
    public static async Task<string> GetCsrfTokenAsync(
        ServerInfo serverInfo,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/csrf-token", null, true, cancellationToken);
        return response.Obj?.Type == JTokenType.String ? response.Obj.Value<string>() : response.Obj?.ToString();
    }

    /// <summary>POST /getTwoFactorEnable. Checks whether OTP is required for cookie login.</summary>
    public static async Task<bool> GetTwoFactorEnabledAsync(
        ServerInfo serverInfo,
        IConfiguration configuration = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/getTwoFactorEnable", null, false, cancellationToken);
        return response.Obj?.Value<bool>() == true;
    }

    /// <summary>GET /panel/api/inbounds/list. Lists full inbounds with settings, streamSettings, sniffing and clientStats.</summary>
    public static Task<XuiV3ApiResponse<List<XuiV3Inbound>>> GetInboundsAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<List<XuiV3Inbound>>(serverInfo, configuration, HttpMethod.Get, "/panel/api/inbounds/list", null, true, cancellationToken);

    /// <summary>GET /panel/api/inbounds/list/slim. Lists inbounds with smaller client payloads for picker/list screens.</summary>
    public static Task<XuiV3ApiResponse<List<XuiV3Inbound>>> GetInboundsSlimAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<List<XuiV3Inbound>>(serverInfo, configuration, HttpMethod.Get, "/panel/api/inbounds/list/slim", null, true, cancellationToken);

    /// <summary>GET /panel/api/inbounds/options. Lightweight inbound options for dropdowns and attach pickers.</summary>
    public static Task<XuiV3ApiResponse<List<XuiV3InboundOption>>> GetInboundOptionsAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<List<XuiV3InboundOption>>(serverInfo, configuration, HttpMethod.Get, "/panel/api/inbounds/options", null, true, cancellationToken);

    /// <summary>GET /panel/api/inbounds/get/{id}. Gets a single inbound by numeric ID.</summary>
    public static Task<XuiV3ApiResponse<XuiV3Inbound>> GetInboundAsync(ServerInfo serverInfo, IConfiguration configuration, int id, CancellationToken cancellationToken = default)
        => SendAsync<XuiV3Inbound>(serverInfo, configuration, HttpMethod.Get, $"/panel/api/inbounds/get/{id}", null, true, cancellationToken);

    /// <summary>POST /panel/api/inbounds/add. Creates an inbound. Body can include nested JSON settings and streamSettings.</summary>
    public static Task<XuiV3ApiResponse<JToken>> AddInboundAsync(ServerInfo serverInfo, IConfiguration configuration, XuiV3Inbound inbound, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/inbounds/add", inbound, true, cancellationToken);

    /// <summary>POST /panel/api/inbounds/del/{id}. Deletes an inbound and its client stats rows.</summary>
    public static Task<XuiV3ApiResponse<JToken>> DeleteInboundAsync(ServerInfo serverInfo, IConfiguration configuration, int id, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/inbounds/del/{id}", null, true, cancellationToken);

    /// <summary>POST /panel/api/inbounds/bulkDel. Deletes many inbounds in one call.</summary>
    public static Task<XuiV3ApiResponse<JToken>> BulkDeleteInboundsAsync(ServerInfo serverInfo, IConfiguration configuration, IEnumerable<int> ids, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/inbounds/bulkDel", new { ids = ids?.ToList() ?? new List<int>() }, true, cancellationToken);

    /// <summary>POST /panel/api/inbounds/import. Imports an inbound JSON blob exported from a panel.</summary>
    public static Task<XuiV3ApiResponse<JToken>> ImportInboundAsync(ServerInfo serverInfo, IConfiguration configuration, object importPayload, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/inbounds/import", importPayload, true, cancellationToken);

    /// <summary>POST /panel/api/inbounds/update/{id}. Replaces an inbound configuration.</summary>
    public static Task<XuiV3ApiResponse<JToken>> UpdateInboundAsync(ServerInfo serverInfo, IConfiguration configuration, int id, XuiV3Inbound inbound, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/inbounds/update/{id}", inbound, true, cancellationToken);

    /// <summary>POST /panel/api/inbounds/setEnable/{id}. Toggles only the inbound enable flag.</summary>
    public static Task<XuiV3ApiResponse<JToken>> SetInboundEnabledAsync(ServerInfo serverInfo, IConfiguration configuration, int id, bool enable, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/inbounds/setEnable/{id}", new { enable }, true, cancellationToken);

    /// <summary>POST /panel/api/inbounds/{id}/resetTraffic. Resets traffic counters for one inbound.</summary>
    public static Task<XuiV3ApiResponse<JToken>> ResetInboundTrafficAsync(ServerInfo serverInfo, IConfiguration configuration, int id, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/inbounds/{id}/resetTraffic", null, true, cancellationToken);

    /// <summary>POST /panel/api/inbounds/resetAllTraffics. Resets traffic counters for every inbound.</summary>
    public static Task<XuiV3ApiResponse<JToken>> ResetAllInboundTrafficsAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/inbounds/resetAllTraffics", null, true, cancellationToken);

    /// <summary>POST /panel/api/inbounds/{id}/delAllClients. Removes all clients from one inbound but keeps the inbound.</summary>
    public static Task<XuiV3ApiResponse<JToken>> DeleteAllInboundClientsAsync(ServerInfo serverInfo, IConfiguration configuration, int id, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/inbounds/{id}/delAllClients", null, true, cancellationToken);

    /// <summary>GET /panel/api/inbounds/{id}/fallbacks. Lists fallback rules for a master inbound.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetInboundFallbacksAsync(ServerInfo serverInfo, IConfiguration configuration, int id, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, $"/panel/api/inbounds/{id}/fallbacks", null, true, cancellationToken);

    /// <summary>POST /panel/api/inbounds/{id}/fallbacks. Replaces all fallback rules for a master inbound.</summary>
    public static Task<XuiV3ApiResponse<JToken>> ReplaceInboundFallbacksAsync(ServerInfo serverInfo, IConfiguration configuration, int id, object fallbacks, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/inbounds/{id}/fallbacks", fallbacks, true, cancellationToken);

    /// <summary>GET /panel/api/clients/list. Lists every client with inbound IDs and traffic data.</summary>
    public static Task<XuiV3ApiResponse<List<XuiV3Client>>> GetClientsAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<List<XuiV3Client>>(serverInfo, configuration, HttpMethod.Get, "/panel/api/clients/list", null, true, cancellationToken);

    /// <summary>GET /panel/api/clients/list/paged. Server-side filtering, sorting and paging for clients.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetClientsPagedAsync(
        ServerInfo serverInfo,
        IConfiguration configuration,
        XuiV3ClientPageQuery query,
        CancellationToken cancellationToken = default)
    {
        var queryValues = new Dictionary<string, string>
        {
            ["page"] = query?.Page.ToString() ?? "1",
            ["pageSize"] = query?.PageSize.ToString() ?? "25",
            ["search"] = query?.Search ?? "",
            ["filter"] = query?.Filter ?? "",
            ["protocol"] = query?.Protocol ?? "",
            ["sort"] = query?.Sort ?? "",
            ["order"] = query?.Order ?? ""
        };

        return SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/panel/api/clients/list/paged", null, true, cancellationToken, queryValues);
    }

    /// <summary>GET /panel/api/clients/get/{email}. Fetches one client by email.</summary>
    public static Task<XuiV3ApiResponse<XuiV3Client>> GetClientAsync(ServerInfo serverInfo, IConfiguration configuration, string email, CancellationToken cancellationToken = default)
        => SendAsync<XuiV3Client>(serverInfo, configuration, HttpMethod.Get, $"/panel/api/clients/get/{EscapePath(email)}", null, true, cancellationToken);

    /// <summary>POST /panel/api/clients/add. Creates a client and attaches it to one or more inbounds.</summary>
    public static Task<XuiV3ApiResponse<JToken>> AddClientAsync(ServerInfo serverInfo, IConfiguration configuration, XuiV3ClientCreateRequest request, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/add", request, true, cancellationToken);

    /// <summary>POST /panel/api/clients/bulkCreate. Creates many clients in one request.</summary>
    public static Task<XuiV3ApiResponse<JToken>> BulkCreateClientsAsync(ServerInfo serverInfo, IConfiguration configuration, IEnumerable<XuiV3ClientCreateRequest> requests, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/bulkCreate", requests?.ToList() ?? new List<XuiV3ClientCreateRequest>(), true, cancellationToken);

    /// <summary>POST /panel/api/clients/update/{email}. Replaces a client row by email.</summary>
    public static Task<XuiV3ApiResponse<JToken>> UpdateClientAsync(ServerInfo serverInfo, IConfiguration configuration, string email, XuiV3ClientPayload client, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/clients/update/{EscapePath(email)}", client, true, cancellationToken);

    /// <summary>Fetches a client, changes its enable flag, then updates it through the v3 API.</summary>
    public static Task<XuiV3ApiResponse<JToken>> SetClientEnabledAsync(
        ServerInfo serverInfo,
        IConfiguration configuration,
        string email,
        bool enable,
        CancellationToken cancellationToken = default)
        => SetClientEnabledAsync(serverInfo, configuration, email, enable, null, cancellationToken);

    /// <summary>Fetches a client, changes only its enable flag, and preserves ownership and other client fields.</summary>
    public static async Task<XuiV3ApiResponse<JToken>> SetClientEnabledAsync(
        ServerInfo serverInfo,
        IConfiguration configuration,
        string email,
        bool enable,
        long? expectedOwnerTelegramUserId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return new XuiV3ApiResponse<JToken>
            {
                Success = false,
                Msg = "Client email is required."
            };
        }

        email = email.Trim();
        var clientResponse = await GetClientAsync(serverInfo, configuration, email, cancellationToken);
        if (!clientResponse.Success || clientResponse.Obj == null)
        {
            return new XuiV3ApiResponse<JToken>
            {
                Success = false,
                Msg = clientResponse.Msg ?? "Client was not found."
            };
        }

        var client = clientResponse.Obj;
        XuiV3Client listClient = null;
        var clientsResponse = await GetClientsAsync(serverInfo, configuration, cancellationToken);
        if (clientsResponse.Success && clientsResponse.Obj != null)
        {
            listClient = clientsResponse.Obj.FirstOrDefault(item =>
                EmailEquals(item.Email, email) ||
                (client.Id > 0 && item.Id == client.Id));
        }

        var source = listClient ?? client;
        var resolvedEmail = FirstNonWhiteSpace(client.Email, source.Email, email).Trim();
        var resolvedSubId = FirstNonWhiteSpace(client.SubId, source.SubId, resolvedEmail).Trim();
        var resolvedTgId = ResolveClientTelegramUserId(client, listClient, expectedOwnerTelegramUserId);

        var updatePayload = new XuiV3ClientPayload
        {
            Email = resolvedEmail,
            TotalGB = source.TotalGB,
            ExpiryTime = source.ExpiryTime,
            TgId = resolvedTgId,
            LimitIp = source.LimitIp,
            Enable = enable,
            SubId = resolvedSubId,
            Flow = FirstNonWhiteSpace(client.Flow, source.Flow),
            Comment = FirstNonWhiteSpace(client.Comment, source.Comment),
            Group = FirstNonWhiteSpace(client.Group, source.Group),
            Reverse = client.Reverse ?? source.Reverse,
            Uuid = FirstNonWhiteSpace(client.Uuid, source.Uuid),
            Password = FirstNonWhiteSpace(client.Password, source.Password),
            Extra = client.Extra ?? source.Extra
        };

        return await UpdateClientAsync(serverInfo, configuration, email, updatePayload, cancellationToken);
    }

    private static long ResolveClientTelegramUserId(
        XuiV3Client primaryClient,
        XuiV3Client listClient,
        long? expectedOwnerTelegramUserId)
    {
        if (primaryClient?.TgId > 0)
            return primaryClient.TgId;

        if (listClient?.TgId > 0)
            return listClient.TgId;

        var metadata = TryReadClientMetadata(primaryClient?.Comment) ??
                       TryReadClientMetadata(listClient?.Comment);
        if (metadata?.TelegramUserId > 0)
            return metadata.TelegramUserId;

        return expectedOwnerTelegramUserId.GetValueOrDefault();
    }

    private static XuiV3ClientMetadata TryReadClientMetadata(string comment)
    {
        if (string.IsNullOrWhiteSpace(comment))
            return null;

        try
        {
            return JsonConvert.DeserializeObject<XuiV3ClientMetadata>(comment);
        }
        catch
        {
            return null;
        }
    }

    private static string FirstNonWhiteSpace(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static bool EmailEquals(string left, string right)
    {
        return string.Equals(
            (left ?? string.Empty).Trim(),
            (right ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>POST /panel/api/clients/del/{email}. Deletes one client from every attached inbound.</summary>
    public static Task<XuiV3ApiResponse<JToken>> DeleteClientAsync(ServerInfo serverInfo, IConfiguration configuration, string email, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/clients/del/{EscapePath(email)}", null, true, cancellationToken);

    /// <summary>POST /panel/api/clients/bulkDel. Deletes many clients by email.</summary>
    public static Task<XuiV3ApiResponse<JToken>> BulkDeleteClientsAsync(ServerInfo serverInfo, IConfiguration configuration, IEnumerable<string> emails, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/bulkDel", new { emails = emails?.ToList() ?? new List<string>() }, true, cancellationToken);

    /// <summary>POST /panel/api/clients/{email}/attach. Attaches an existing client to additional inbounds.</summary>
    public static Task<XuiV3ApiResponse<JToken>> AttachClientAsync(ServerInfo serverInfo, IConfiguration configuration, string email, IEnumerable<int> inboundIds, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/clients/{EscapePath(email)}/attach", new { inboundIds = inboundIds?.ToList() ?? new List<int>() }, true, cancellationToken);

    /// <summary>POST /panel/api/clients/{email}/detach. Detaches a client from selected inbounds without deleting it.</summary>
    public static Task<XuiV3ApiResponse<JToken>> DetachClientAsync(ServerInfo serverInfo, IConfiguration configuration, string email, IEnumerable<int> inboundIds, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/clients/{EscapePath(email)}/detach", new { inboundIds = inboundIds?.ToList() ?? new List<int>() }, true, cancellationToken);

    /// <summary>POST /panel/api/clients/bulkAttach. Attaches many existing clients to many inbounds.</summary>
    public static Task<XuiV3ApiResponse<JToken>> BulkAttachClientsAsync(ServerInfo serverInfo, IConfiguration configuration, object request, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/bulkAttach", request, true, cancellationToken);

    /// <summary>POST /panel/api/clients/bulkDetach. Detaches many clients from many inbounds.</summary>
    public static Task<XuiV3ApiResponse<JToken>> BulkDetachClientsAsync(ServerInfo serverInfo, IConfiguration configuration, object request, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/bulkDetach", request, true, cancellationToken);

    /// <summary>POST /panel/api/clients/bulkAdjust. Shifts expiry and/or quota for many clients.</summary>
    public static Task<XuiV3ApiResponse<JToken>> BulkAdjustClientsAsync(ServerInfo serverInfo, IConfiguration configuration, object request, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/bulkAdjust", request, true, cancellationToken);

    /// <summary>POST /panel/api/clients/resetTraffic/{email}. Resets one client's up/down counters.</summary>
    public static Task<XuiV3ApiResponse<JToken>> ResetClientTrafficAsync(ServerInfo serverInfo, IConfiguration configuration, string email, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/clients/resetTraffic/{EscapePath(email)}", null, true, cancellationToken);

    /// <summary>POST /panel/api/clients/bulkResetTraffic. Resets traffic for many clients.</summary>
    public static Task<XuiV3ApiResponse<JToken>> BulkResetClientTrafficAsync(ServerInfo serverInfo, IConfiguration configuration, IEnumerable<string> emails, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/bulkResetTraffic", new { emails = emails?.ToList() ?? new List<string>() }, true, cancellationToken);

    /// <summary>POST /panel/api/clients/resetAllTraffics. Resets all client traffic counters globally.</summary>
    public static Task<XuiV3ApiResponse<JToken>> ResetAllClientTrafficsAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/resetAllTraffics", null, true, cancellationToken);

    /// <summary>POST /panel/api/clients/updateTraffic/{email}. Manually adjusts upload and download counters.</summary>
    public static Task<XuiV3ApiResponse<JToken>> UpdateClientTrafficAsync(ServerInfo serverInfo, IConfiguration configuration, string email, long up, long down, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/clients/updateTraffic/{EscapePath(email)}", new { up, down }, true, cancellationToken);

    /// <summary>POST /panel/api/clients/delDepleted. Deletes clients whose traffic quota is exhausted.</summary>
    public static Task<XuiV3ApiResponse<JToken>> DeleteDepletedClientsAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/delDepleted", null, true, cancellationToken);

    /// <summary>POST /panel/api/clients/clearIps/{email}. Clears the recorded IP list for one client.</summary>
    public static Task<XuiV3ApiResponse<JToken>> ClearClientIpsAsync(ServerInfo serverInfo, IConfiguration configuration, string email, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/clients/clearIps/{EscapePath(email)}", null, true, cancellationToken);

    /// <summary>POST /panel/api/clients/ips/{email}. Lists source IPs for one client.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetClientIpsAsync(ServerInfo serverInfo, IConfiguration configuration, string email, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/clients/ips/{EscapePath(email)}", null, true, cancellationToken);

    /// <summary>POST /panel/api/clients/onlines. Lists currently online client emails.</summary>
    public static Task<XuiV3ApiResponse<List<string>>> GetOnlineClientsAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<List<string>>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/onlines", null, true, cancellationToken);

    /// <summary>POST /panel/api/clients/onlinesByNode. Lists online emails grouped by reporting node.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetOnlineClientsByNodeAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/onlinesByNode", null, true, cancellationToken);

    /// <summary>POST /panel/api/clients/lastOnline. Returns email to last-seen unix timestamp map.</summary>
    public static Task<XuiV3ApiResponse<Dictionary<string, long>>> GetLastOnlineClientsAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<Dictionary<string, long>>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/lastOnline", null, true, cancellationToken);

    /// <summary>POST /panel/api/clients/activeInbounds. Returns inbound tags that carried traffic recently.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetActiveInboundsAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/activeInbounds", null, true, cancellationToken);

    /// <summary>GET /panel/api/clients/traffic/{email}. Gets traffic counters for one client.</summary>
    public static Task<XuiV3ApiResponse<XuiV3ClientTraffic>> GetClientTrafficAsync(ServerInfo serverInfo, IConfiguration configuration, string email, CancellationToken cancellationToken = default)
        => SendAsync<XuiV3ClientTraffic>(serverInfo, configuration, HttpMethod.Get, $"/panel/api/clients/traffic/{EscapePath(email)}", null, true, cancellationToken);

    /// <summary>GET /panel/api/clients/links/{email}. Gets all proxy URLs for a client across attached inbounds.</summary>
    public static Task<XuiV3ApiResponse<List<string>>> GetClientLinksAsync(ServerInfo serverInfo, IConfiguration configuration, string email, CancellationToken cancellationToken = default)
        => SendAsync<List<string>>(serverInfo, configuration, HttpMethod.Get, $"/panel/api/clients/links/{EscapePath(email)}", null, true, cancellationToken);

    /// <summary>GET /panel/api/clients/subLinks/{subId}. Gets all protocol URLs for a subscription ID as JSON.</summary>
    public static Task<XuiV3ApiResponse<List<string>>> GetClientSubLinksAsync(ServerInfo serverInfo, IConfiguration configuration, string subId, CancellationToken cancellationToken = default)
        => SendAsync<List<string>>(serverInfo, configuration, HttpMethod.Get, $"/panel/api/clients/subLinks/{EscapePath(subId)}", null, true, cancellationToken);

    /// <summary>GET /panel/api/clients/groups. Lists client groups with member counts.</summary>
    public static Task<XuiV3ApiResponse<List<XuiV3ClientGroup>>> GetClientGroupsAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<List<XuiV3ClientGroup>>(serverInfo, configuration, HttpMethod.Get, "/panel/api/clients/groups", null, true, cancellationToken);

    /// <summary>GET /panel/api/clients/groups/{name}/emails. Lists emails in one group.</summary>
    public static Task<XuiV3ApiResponse<List<string>>> GetClientGroupEmailsAsync(ServerInfo serverInfo, IConfiguration configuration, string name, CancellationToken cancellationToken = default)
        => SendAsync<List<string>>(serverInfo, configuration, HttpMethod.Get, $"/panel/api/clients/groups/{EscapePath(name)}/emails", null, true, cancellationToken);

    /// <summary>POST /panel/api/clients/groups/create. Creates a placeholder group.</summary>
    public static Task<XuiV3ApiResponse<JToken>> CreateClientGroupAsync(ServerInfo serverInfo, IConfiguration configuration, string name, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/groups/create", new { name }, true, cancellationToken);

    /// <summary>POST /panel/api/clients/groups/delete. Deletes a group and clears that label from clients.</summary>
    public static Task<XuiV3ApiResponse<JToken>> DeleteClientGroupAsync(ServerInfo serverInfo, IConfiguration configuration, string name, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/groups/delete", new { name }, true, cancellationToken);

    /// <summary>POST /panel/api/clients/groups/rename. Renames a group.</summary>
    public static Task<XuiV3ApiResponse<JToken>> RenameClientGroupAsync(ServerInfo serverInfo, IConfiguration configuration, string oldName, string newName, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/groups/rename", new { oldName, newName }, true, cancellationToken);

    /// <summary>POST /panel/api/clients/groups/bulkAdd. Adds many clients to one group.</summary>
    public static Task<XuiV3ApiResponse<JToken>> BulkAddClientsToGroupAsync(ServerInfo serverInfo, IConfiguration configuration, string group, IEnumerable<string> emails, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/groups/bulkAdd", new { group, emails = emails?.ToList() ?? new List<string>() }, true, cancellationToken);

    /// <summary>POST /panel/api/clients/groups/bulkRemove. Clears the group label on many clients.</summary>
    public static Task<XuiV3ApiResponse<JToken>> BulkRemoveClientsFromGroupAsync(ServerInfo serverInfo, IConfiguration configuration, IEnumerable<string> emails, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/clients/groups/bulkRemove", new { emails = emails?.ToList() ?? new List<string>() }, true, cancellationToken);

    /// <summary>GET /panel/api/nodes/list. Lists configured remote nodes.</summary>
    public static Task<XuiV3ApiResponse<List<XuiV3Node>>> GetNodesAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<List<XuiV3Node>>(serverInfo, configuration, HttpMethod.Get, "/panel/api/nodes/list", null, true, cancellationToken);

    /// <summary>GET /panel/api/nodes/get/{id}. Gets one node by ID.</summary>
    public static Task<XuiV3ApiResponse<XuiV3Node>> GetNodeAsync(ServerInfo serverInfo, IConfiguration configuration, int id, CancellationToken cancellationToken = default)
        => SendAsync<XuiV3Node>(serverInfo, configuration, HttpMethod.Get, $"/panel/api/nodes/get/{id}", null, true, cancellationToken);

    /// <summary>POST /panel/api/nodes/add. Registers a remote node with URL and API token.</summary>
    public static Task<XuiV3ApiResponse<JToken>> AddNodeAsync(ServerInfo serverInfo, IConfiguration configuration, XuiV3Node node, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/nodes/add", node, true, cancellationToken);

    /// <summary>POST /panel/api/nodes/update/{id}. Replaces a node connection definition.</summary>
    public static Task<XuiV3ApiResponse<JToken>> UpdateNodeAsync(ServerInfo serverInfo, IConfiguration configuration, int id, XuiV3Node node, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/nodes/update/{id}", node, true, cancellationToken);

    /// <summary>POST /panel/api/nodes/del/{id}. Deletes a remote node.</summary>
    public static Task<XuiV3ApiResponse<JToken>> DeleteNodeAsync(ServerInfo serverInfo, IConfiguration configuration, int id, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/nodes/del/{id}", null, true, cancellationToken);

    /// <summary>POST /panel/api/nodes/setEnable/{id}. Pauses or resumes node sync.</summary>
    public static Task<XuiV3ApiResponse<JToken>> SetNodeEnabledAsync(ServerInfo serverInfo, IConfiguration configuration, int id, bool enable, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/nodes/setEnable/{id}", new { enable }, true, cancellationToken);

    /// <summary>POST /panel/api/nodes/probe/{id}. Probes a saved node and refreshes cached health data.</summary>
    public static Task<XuiV3ApiResponse<JToken>> ProbeNodeAsync(ServerInfo serverInfo, IConfiguration configuration, int id, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/nodes/probe/{id}", null, true, cancellationToken);

    /// <summary>POST /panel/api/nodes/test. Probes a node definition without saving it.</summary>
    public static Task<XuiV3ApiResponse<JToken>> TestNodeAsync(ServerInfo serverInfo, IConfiguration configuration, XuiV3Node node, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/nodes/test", node, true, cancellationToken);

    /// <summary>GET /panel/api/nodes/history/{id}/{metric}/{bucket}. Gets node metric history.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetNodeHistoryAsync(ServerInfo serverInfo, IConfiguration configuration, int id, string metric, string bucket, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, $"/panel/api/nodes/history/{id}/{EscapePath(metric)}/{EscapePath(bucket)}", null, true, cancellationToken);

    /// <summary>GET /panel/api/nodes/webCert/{id}. Gets node web TLS cert/key paths.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetNodeWebCertAsync(ServerInfo serverInfo, IConfiguration configuration, int id, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, $"/panel/api/nodes/webCert/{id}", null, true, cancellationToken);

    /// <summary>POST /panel/api/nodes/certFingerprint. Reads an HTTPS certificate fingerprint without verifying it.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetNodeCertificateFingerprintAsync(ServerInfo serverInfo, IConfiguration configuration, object request, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/nodes/certFingerprint", request, true, cancellationToken);

    /// <summary>POST /panel/api/nodes/updatePanel. Triggers the official panel updater on selected nodes.</summary>
    public static Task<XuiV3ApiResponse<JToken>> UpdateNodesPanelAsync(ServerInfo serverInfo, IConfiguration configuration, object request, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/nodes/updatePanel", request, true, cancellationToken);

    /// <summary>GET /panel/api/custom-geo/list. Lists custom GeoIP/GeoSite sources.</summary>
    public static Task<XuiV3ApiResponse<List<XuiV3CustomGeoSource>>> GetCustomGeoSourcesAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<List<XuiV3CustomGeoSource>>(serverInfo, configuration, HttpMethod.Get, "/panel/api/custom-geo/list", null, true, cancellationToken);

    /// <summary>GET /panel/api/custom-geo/aliases. Lists aliases usable in routing rules.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetCustomGeoAliasesAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/panel/api/custom-geo/aliases", null, true, cancellationToken);

    /// <summary>POST /panel/api/custom-geo/add. Adds a custom geo source URL.</summary>
    public static Task<XuiV3ApiResponse<JToken>> AddCustomGeoSourceAsync(ServerInfo serverInfo, IConfiguration configuration, XuiV3CustomGeoSource source, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/custom-geo/add", source, true, cancellationToken);

    /// <summary>POST /panel/api/custom-geo/update/{id}. Replaces a custom geo source.</summary>
    public static Task<XuiV3ApiResponse<JToken>> UpdateCustomGeoSourceAsync(ServerInfo serverInfo, IConfiguration configuration, int id, XuiV3CustomGeoSource source, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/custom-geo/update/{id}", source, true, cancellationToken);

    /// <summary>POST /panel/api/custom-geo/delete/{id}. Deletes a custom geo source and cached file.</summary>
    public static Task<XuiV3ApiResponse<JToken>> DeleteCustomGeoSourceAsync(ServerInfo serverInfo, IConfiguration configuration, int id, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/custom-geo/delete/{id}", null, true, cancellationToken);

    /// <summary>POST /panel/api/custom-geo/download/{id}. Downloads one custom geo source now.</summary>
    public static Task<XuiV3ApiResponse<JToken>> DownloadCustomGeoSourceAsync(ServerInfo serverInfo, IConfiguration configuration, int id, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/custom-geo/download/{id}", null, true, cancellationToken);

    /// <summary>POST /panel/api/custom-geo/update-all. Downloads every custom geo source.</summary>
    public static Task<XuiV3ApiResponse<JToken>> UpdateAllCustomGeoSourcesAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/custom-geo/update-all", null, true, cancellationToken);

    /// <summary>POST /panel/api/backuptotgbot. Sends a DB backup to configured Telegram chats.</summary>
    public static Task<XuiV3ApiResponse<JToken>> BackupToTelegramBotAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/backuptotgbot", null, true, cancellationToken);

    /// <summary>GET /panel/api/server/status. Real-time machine and Xray status.</summary>
    public static Task<XuiV3ApiResponse<XuiV3ServerStatus>> GetServerStatusAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<XuiV3ServerStatus>(serverInfo, configuration, HttpMethod.Get, "/panel/api/server/status", null, true, cancellationToken);

    /// <summary>GET /panel/api/server/history/{metric}/{bucket}. Aggregated server metric history.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetServerHistoryAsync(ServerInfo serverInfo, IConfiguration configuration, string metric, string bucket, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, $"/panel/api/server/history/{EscapePath(metric)}/{EscapePath(bucket)}", null, true, cancellationToken);

    /// <summary>GET /panel/api/server/cpuHistory/{bucket}. Legacy CPU history endpoint.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetServerCpuHistoryAsync(ServerInfo serverInfo, IConfiguration configuration, string bucket, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, $"/panel/api/server/cpuHistory/{EscapePath(bucket)}", null, true, cancellationToken);

    /// <summary>GET /panel/api/server/getConfigJson. Returns the assembled active Xray config JSON.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetServerConfigJsonAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/panel/api/server/getConfigJson", null, true, cancellationToken);

    /// <summary>GET /panel/api/server/getDb. Downloads the SQLite database as bytes.</summary>
    public static Task<byte[]> GetDatabaseBackupAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendBytesAsync(serverInfo, configuration, HttpMethod.Get, "/panel/api/server/getDb", null, true, cancellationToken);

    /// <summary>GET /panel/api/server/getNewUUID. Generates a UUID v4.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetNewUuidAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/panel/api/server/getNewUUID", null, true, cancellationToken);

    /// <summary>GET /panel/api/server/getNewX25519Cert. Generates a Reality X25519 keypair.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetNewX25519CertificateAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/panel/api/server/getNewX25519Cert", null, true, cancellationToken);

    /// <summary>GET /panel/api/server/getNewVlessEnc. Generates VLESS encryption auth options.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetNewVlessEncryptionAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/panel/api/server/getNewVlessEnc", null, true, cancellationToken);

    /// <summary>GET /panel/api/server/getNewmlkem768. Generates an ML-KEM-768 keypair.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetNewMlKem768Async(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/panel/api/server/getNewmlkem768", null, true, cancellationToken);

    /// <summary>GET /panel/api/server/getNewmldsa65. Generates an ML-DSA-65 keypair.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetNewMlDsa65Async(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/panel/api/server/getNewmldsa65", null, true, cancellationToken);

    /// <summary>POST /panel/api/server/getNewEchCert. Generates an ECH keypair and config list for an SNI.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetNewEchCertificateAsync(ServerInfo serverInfo, IConfiguration configuration, object request, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/server/getNewEchCert", request, true, cancellationToken);

    /// <summary>GET /panel/api/server/getPanelUpdateInfo. Checks whether a newer 3x-ui release exists.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetPanelUpdateInfoAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/panel/api/server/getPanelUpdateInfo", null, true, cancellationToken);

    /// <summary>GET /panel/api/server/getWebCertFiles. Returns panel web TLS certificate/key paths.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetWebCertificateFilesAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/panel/api/server/getWebCertFiles", null, true, cancellationToken);

    /// <summary>GET /panel/api/server/getXrayVersion. Lists Xray versions available for install.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetXrayVersionsAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/panel/api/server/getXrayVersion", null, true, cancellationToken);

    /// <summary>POST /panel/api/server/installXray/{version}. Downloads and installs a selected Xray version.</summary>
    public static Task<XuiV3ApiResponse<JToken>> InstallXrayAsync(ServerInfo serverInfo, IConfiguration configuration, string version, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/server/installXray/{EscapePath(version)}", null, true, cancellationToken);

    /// <summary>POST /panel/api/server/restartXrayService. Reloads Xray with the current config.</summary>
    public static Task<XuiV3ApiResponse<JToken>> RestartXrayServiceAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/server/restartXrayService", null, true, cancellationToken);

    /// <summary>POST /panel/api/server/stopXrayService. Stops Xray immediately.</summary>
    public static Task<XuiV3ApiResponse<JToken>> StopXrayServiceAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/server/stopXrayService", null, true, cancellationToken);

    /// <summary>POST /panel/api/server/updateGeofile. Refreshes default GeoIP/GeoSite files.</summary>
    public static Task<XuiV3ApiResponse<JToken>> UpdateGeofileAsync(ServerInfo serverInfo, IConfiguration configuration, object request = null, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/server/updateGeofile", request, true, cancellationToken);

    /// <summary>POST /panel/api/server/updateGeofile/{fileName}. Refreshes a single geo file.</summary>
    public static Task<XuiV3ApiResponse<JToken>> UpdateGeofileAsync(ServerInfo serverInfo, IConfiguration configuration, string fileName, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/server/updateGeofile/{EscapePath(fileName)}", null, true, cancellationToken);

    /// <summary>POST /panel/api/server/updatePanel. Self-updates the panel.</summary>
    public static Task<XuiV3ApiResponse<JToken>> UpdatePanelAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/api/server/updatePanel", null, true, cancellationToken);

    /// <summary>POST /panel/api/server/logs/{count}. Returns the last N panel log lines.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetPanelLogsAsync(ServerInfo serverInfo, IConfiguration configuration, int count, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/server/logs/{count}", null, true, cancellationToken);

    /// <summary>POST /panel/api/server/xraylogs/{count}. Returns the last N Xray log lines.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetXrayLogsAsync(ServerInfo serverInfo, IConfiguration configuration, int count, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/api/server/xraylogs/{count}", null, true, cancellationToken);

    /// <summary>POST /panel/api/server/importDB. Restores the panel DB from a local SQLite file path.</summary>
    public static async Task<XuiV3ApiResponse<JToken>> ImportDatabaseAsync(ServerInfo serverInfo, IConfiguration configuration, string dbFilePath, CancellationToken cancellationToken = default)
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(await File.ReadAllBytesAsync(dbFilePath, cancellationToken)), "db", Path.GetFileName(dbFilePath));
        var raw = await SendRawAsync(serverInfo, configuration, HttpMethod.Post, "/panel/api/server/importDB", content, true, cancellationToken);
        return JsonConvert.DeserializeObject<XuiV3ApiResponse<JToken>>(raw) ?? new XuiV3ApiResponse<JToken>();
    }

    /// <summary>GET /panel/api/server/xrayMetricsState. Returns whether Xray metrics are enabled and reachable.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetXrayMetricsStateAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/panel/api/server/xrayMetricsState", null, true, cancellationToken);

    /// <summary>GET /panel/api/server/xrayMetricsHistory/{metric}/{bucket}. Gets Xray runtime metric history.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetXrayMetricsHistoryAsync(ServerInfo serverInfo, IConfiguration configuration, string metric, string bucket, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, $"/panel/api/server/xrayMetricsHistory/{EscapePath(metric)}/{EscapePath(bucket)}", null, true, cancellationToken);

    /// <summary>GET /panel/api/server/xrayObservatory. Gets the latest Xray observatory snapshot.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetXrayObservatoryAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/panel/api/server/xrayObservatory", null, true, cancellationToken);

    /// <summary>GET /panel/api/server/xrayObservatoryHistory/{tag}/{bucket}. Gets observatory history for one outbound tag.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetXrayObservatoryHistoryAsync(ServerInfo serverInfo, IConfiguration configuration, string tag, string bucket, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, $"/panel/api/server/xrayObservatoryHistory/{EscapePath(tag)}/{EscapePath(bucket)}", null, true, cancellationToken);

    /// <summary>POST /panel/setting/all. Returns the full settings JSON.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetAllSettingsAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/setting/all", null, true, cancellationToken);

    /// <summary>POST /panel/setting/defaultSettings. Returns computed default settings.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetDefaultSettingsAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/setting/defaultSettings", null, true, cancellationToken);

    /// <summary>GET /panel/setting/getDefaultJsonConfig. Returns built-in default Xray JSON config.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetDefaultJsonConfigAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/panel/setting/getDefaultJsonConfig", null, true, cancellationToken);

    /// <summary>POST /panel/setting/update. Persists every panel setting at once.</summary>
    public static Task<XuiV3ApiResponse<JToken>> UpdateSettingsAsync(ServerInfo serverInfo, IConfiguration configuration, object settings, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/setting/update", settings, true, cancellationToken);

    /// <summary>POST /panel/setting/updateUser. Changes panel admin username and password.</summary>
    public static Task<XuiV3ApiResponse<JToken>> UpdatePanelUserAsync(ServerInfo serverInfo, IConfiguration configuration, string username, string password, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/setting/updateUser", new { username, password }, true, cancellationToken);

    /// <summary>POST /panel/setting/restartPanel. Restarts the 3x-ui process after a short grace period.</summary>
    public static Task<XuiV3ApiResponse<JToken>> RestartPanelAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/setting/restartPanel", null, true, cancellationToken);

    /// <summary>GET /panel/setting/apiTokens. Lists API token metadata. Plain token values are not returned.</summary>
    public static Task<XuiV3ApiResponse<List<XuiV3ApiTokenInfo>>> GetApiTokensAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<List<XuiV3ApiTokenInfo>>(serverInfo, configuration, HttpMethod.Get, "/panel/setting/apiTokens", null, true, cancellationToken);

    /// <summary>POST /panel/setting/apiTokens/create. Creates a Bearer token. The plaintext token is returned only once.</summary>
    public static Task<XuiV3ApiResponse<XuiV3ApiTokenCreated>> CreateApiTokenAsync(ServerInfo serverInfo, IConfiguration configuration, string name, CancellationToken cancellationToken = default)
        => SendAsync<XuiV3ApiTokenCreated>(serverInfo, configuration, HttpMethod.Post, "/panel/setting/apiTokens/create", new { name }, true, cancellationToken);

    /// <summary>POST /panel/setting/apiTokens/delete/{id}. Permanently deletes an API token.</summary>
    public static Task<XuiV3ApiResponse<JToken>> DeleteApiTokenAsync(ServerInfo serverInfo, IConfiguration configuration, int id, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/setting/apiTokens/delete/{id}", null, true, cancellationToken);

    /// <summary>POST /panel/setting/apiTokens/setEnabled/{id}. Enables or disables an API token.</summary>
    public static Task<XuiV3ApiResponse<JToken>> SetApiTokenEnabledAsync(ServerInfo serverInfo, IConfiguration configuration, int id, bool enabled, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/setting/apiTokens/setEnabled/{id}", new { enabled }, true, cancellationToken);

    /// <summary>POST /panel/xray/. Returns Xray template, inbound tags, reverse tags and outbound test URL.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetXraySettingsAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/xray/", null, true, cancellationToken);

    /// <summary>GET /panel/xray/getDefaultJsonConfig. Returns built-in default Xray config.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetXrayDefaultJsonConfigAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/panel/xray/getDefaultJsonConfig", null, true, cancellationToken);

    /// <summary>GET /panel/xray/getOutboundsTraffic. Returns traffic counters for every outbound.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetOutboundsTrafficAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/panel/xray/getOutboundsTraffic", null, true, cancellationToken);

    /// <summary>GET /panel/xray/getXrayResult. Returns recent Xray stdout/stderr output.</summary>
    public static Task<XuiV3ApiResponse<JToken>> GetXrayResultAsync(ServerInfo serverInfo, IConfiguration configuration, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Get, "/panel/xray/getXrayResult", null, true, cancellationToken);

    /// <summary>POST /panel/xray/update. Saves Xray JSON config and optional outbound test URL as form fields.</summary>
    public static Task<XuiV3ApiResponse<JToken>> UpdateXrayConfigAsync(ServerInfo serverInfo, IConfiguration configuration, string xraySetting, string outboundTestUrl = null, CancellationToken cancellationToken = default)
    {
        var fields = new Dictionary<string, string> { ["xraySetting"] = xraySetting ?? "" };
        if (!string.IsNullOrWhiteSpace(outboundTestUrl))
            fields["outboundTestUrl"] = outboundTestUrl;

        return SendFormAsync<JToken>(serverInfo, configuration, "/panel/xray/update", fields, cancellationToken);
    }

    /// <summary>POST /panel/xray/warp/{action}. Runs Warp action: data, del, config, reg or license.</summary>
    public static Task<XuiV3ApiResponse<JToken>> ManageWarpAsync(ServerInfo serverInfo, IConfiguration configuration, string action, object request = null, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/xray/warp/{EscapePath(action)}", request, true, cancellationToken);

    /// <summary>POST /panel/xray/nord/{action}. Runs NordVPN action: countries, servers, reg, setKey, data or del.</summary>
    public static Task<XuiV3ApiResponse<JToken>> ManageNordAsync(ServerInfo serverInfo, IConfiguration configuration, string action, object request = null, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, $"/panel/xray/nord/{EscapePath(action)}", request, true, cancellationToken);

    /// <summary>POST /panel/xray/resetOutboundsTraffic. Resets traffic counters for an outbound tag.</summary>
    public static Task<XuiV3ApiResponse<JToken>> ResetOutboundTrafficAsync(ServerInfo serverInfo, IConfiguration configuration, string tag, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/xray/resetOutboundsTraffic", new { tag }, true, cancellationToken);

    /// <summary>POST /panel/xray/testOutbound. Tests an outbound JSON configuration.</summary>
    public static Task<XuiV3ApiResponse<JToken>> TestOutboundAsync(ServerInfo serverInfo, IConfiguration configuration, object request, CancellationToken cancellationToken = default)
        => SendAsync<JToken>(serverInfo, configuration, HttpMethod.Post, "/panel/xray/testOutbound", request, true, cancellationToken);

    /// <summary>GET /{subPath}{subid}. Returns base64 subscription text from the subscription server.</summary>
    public static Task<string> GetSubscriptionAsync(ServerInfo serverInfo, string subId, string subPath = "sub/", CancellationToken cancellationToken = default)
        => SendSubscriptionRawAsync(serverInfo, subPath, subId, "text/plain", cancellationToken);

    /// <summary>GET /{jsonPath}{subid}. Returns JSON subscription array from the subscription server.</summary>
    public static async Task<JToken> GetJsonSubscriptionAsync(ServerInfo serverInfo, string subId, string jsonPath = "json/", CancellationToken cancellationToken = default)
    {
        var raw = await SendSubscriptionRawAsync(serverInfo, jsonPath, subId, "application/json", cancellationToken);
        return JToken.Parse(raw);
    }

    /// <summary>GET /{clashPath}{subid}. Returns Clash/Mihomo YAML subscription from the subscription server.</summary>
    public static Task<string> GetClashSubscriptionAsync(ServerInfo serverInfo, string subId, string clashPath = "clash/", CancellationToken cancellationToken = default)
        => SendSubscriptionRawAsync(serverInfo, clashPath, subId, "text/yaml", cancellationToken);

    /// <summary>GET /ws. Builds the websocket URI. Bearer auth is not supported by this endpoint; use session cookie auth.</summary>
    public static Uri BuildWebSocketUri(ServerInfo serverInfo)
    {
        var panelUri = BuildPanelUri(serverInfo, "/ws", null);
        var builder = new UriBuilder(panelUri)
        {
            Scheme = panelUri.Scheme == "https" ? "wss" : "ws"
        };
        return builder.Uri;
    }

    public static async Task<XuiV3ApiResponse<T>> SendAsync<T>(
        ServerInfo serverInfo,
        IConfiguration configuration,
        HttpMethod method,
        string relativePath,
        object body = null,
        bool authenticate = true,
        CancellationToken cancellationToken = default,
        IDictionary<string, string> query = null)
    {
        var raw = await SendRawAsync(serverInfo, configuration, method, relativePath, body, authenticate, cancellationToken, query);
        var result = JsonConvert.DeserializeObject<XuiV3ApiResponse<T>>(raw);
        if (result == null)
            throw new XuiV3ApiException(method.ToString(), BuildPanelUri(serverInfo, relativePath, query).ToString(), 0, raw, null);

        return result;
    }

    private static async Task<XuiV3ApiResponse<T>> SendFormAsync<T>(
        ServerInfo serverInfo,
        IConfiguration configuration,
        string relativePath,
        IDictionary<string, string> fields,
        CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(fields ?? new Dictionary<string, string>());
        var raw = await SendRawAsync(serverInfo, configuration, HttpMethod.Post, relativePath, content, true, cancellationToken);
        return JsonConvert.DeserializeObject<XuiV3ApiResponse<T>>(raw) ?? new XuiV3ApiResponse<T>();
    }

    private static async Task<byte[]> SendBytesAsync(
        ServerInfo serverInfo,
        IConfiguration configuration,
        HttpMethod method,
        string relativePath,
        object body,
        bool authenticate,
        CancellationToken cancellationToken)
    {
        var uri = BuildPanelUri(serverInfo, relativePath, null);
        using var httpClient = CreateHttpClient(configuration);
        using var request = BuildRequest(method, uri, body, serverInfo, configuration, authenticate);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var text = Encoding.UTF8.GetString(bytes);
            throw new XuiV3ApiException(method.ToString(), uri.ToString(), (int)response.StatusCode, text, null);
        }

        return bytes;
    }

    private static async Task<string> SendRawAsync(
        ServerInfo serverInfo,
        IConfiguration configuration,
        HttpMethod method,
        string relativePath,
        object body,
        bool authenticate,
        CancellationToken cancellationToken,
        IDictionary<string, string> query = null)
    {
        var uri = BuildPanelUri(serverInfo, relativePath, query);
        using var httpClient = CreateHttpClient(configuration);
        using var request = BuildRequest(method, uri, body, serverInfo, configuration, authenticate);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var requestBody = body is HttpContent ? "<http-content>" : SerializeBody(body);
            throw new XuiV3ApiException(method.ToString(), uri.ToString(), (int)response.StatusCode, responseText, requestBody);
        }

        return responseText;
    }

    private static async Task<string> SendSubscriptionRawAsync(
        ServerInfo serverInfo,
        string pathPrefix,
        string subId,
        string accept,
        CancellationToken cancellationToken)
    {
        var baseUrl = string.IsNullOrWhiteSpace(serverInfo.SubLinkUrl)
            ? serverInfo.Url
            : serverInfo.SubLinkUrl;

        var normalizedBase = baseUrl.TrimEnd('/') + "/";
        var normalizedPath = (pathPrefix ?? "sub/").Trim('/');
        var uri = new Uri(normalizedBase + normalizedPath + "/" + EscapePath(subId));

        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Accept", accept);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        var raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new XuiV3ApiException("GET", uri.ToString(), (int)response.StatusCode, raw, null);

        return raw;
    }

    private static HttpClient CreateHttpClient(IConfiguration configuration)
    {
        var appConfig = configuration?.Get<AppConfig>() ?? new AppConfig();
        var timeoutSeconds = appConfig.XuiV3RequestTimeoutSeconds <= 0 ? 60 : appConfig.XuiV3RequestTimeoutSeconds;
        return new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    }

    private static HttpRequestMessage BuildRequest(
        HttpMethod method,
        Uri uri,
        object body,
        ServerInfo serverInfo,
        IConfiguration configuration,
        bool authenticate)
    {
        var request = new HttpRequestMessage(method, uri);

        if (authenticate)
        {
            var token = ResolveBearerToken(serverInfo, configuration);
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", StripBearerPrefix(token));
        }

        if (body is HttpContent httpContent)
        {
            request.Content = httpContent;
        }
        else if (body != null)
        {
            request.Content = new StringContent(SerializeBody(body), Encoding.UTF8, "application/json");
        }

        return request;
    }

    private static Uri BuildPanelUri(ServerInfo serverInfo, string relativePath, IDictionary<string, string> query)
    {
        if (serverInfo == null)
            throw new ArgumentNullException(nameof(serverInfo));
        if (string.IsNullOrWhiteSpace(serverInfo.Url))
            throw new InvalidOperationException("ServerInfo.Url is required.");

        var baseUrl = serverInfo.Url.TrimEnd('/');
        var rootPath = (serverInfo.RootPath ?? "").Trim('/');
        var path = string.IsNullOrWhiteSpace(relativePath) ? "/" : relativePath;
        if (!path.StartsWith("/"))
            path = "/" + path;

        if (!string.IsNullOrWhiteSpace(rootPath) &&
            !path.StartsWith("/" + rootPath + "/", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(path, "/" + rootPath, StringComparison.OrdinalIgnoreCase))
        {
            path = "/" + rootPath + path;
        }

        var queryString = BuildQueryString(query);
        return new Uri(baseUrl + path + queryString);
    }

    private static string BuildQueryString(IDictionary<string, string> query)
    {
        if (query == null || query.Count == 0)
            return "";

        var parts = query
            .Where(kv => kv.Value != null)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}");
        return "?" + string.Join("&", parts);
    }

    private static string ResolveBearerToken(ServerInfo serverInfo, IConfiguration configuration)
    {
        var appConfig = configuration?.Get<AppConfig>();
        return !string.IsNullOrWhiteSpace(serverInfo.ApiToken)
            ? serverInfo.ApiToken
            : appConfig?.XuiV3ApiToken;
    }

    private static string StripBearerPrefix(string token)
    {
        const string prefix = "Bearer ";
        return token.Trim().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? token.Trim().Substring(prefix.Length)
            : token.Trim();
    }

    private static string SerializeBody(object body)
    {
        return body == null ? null : JsonConvert.SerializeObject(body, JsonSettings);
    }

    private static string EscapePath(string value)
    {
        return Uri.EscapeDataString(value ?? "");
    }

    /// <summary>
    /// Builds the client object that is sent to the XUI v3 <c>addClient</c> API.
    /// </summary>
    /// <param name="accountDto">
    /// User-facing account request from the bot flow. It supplies Telegram owner id, selected period, service key,
    /// traffic fallback, generated email counter, and target panel information.
    /// </param>
    /// <param name="options">
    /// Resolved v3 creation options from the purchase service, including traffic bytes, duration, IP limit, comment
    /// metadata, subscription id override, and first-use-expiry behavior.
    /// </param>
    /// <returns>
    /// A panel payload with traffic limit in bytes, expiry time in milliseconds, Telegram id, subscription id, and JSON
    /// comment metadata. The UUID is intentionally left to 3x-ui so the returned result can use the panel's real value.
    /// </returns>
    /// <remarks>
    /// After the add request succeeds, <see cref="CreateUserAccountAsync"/> reads the client back from the panel and
    /// exposes that real panel UUID through <see cref="XuiV3AccountCreationResult.Uuid"/>. Do not generate an arbitrary
    /// UUID here, because the Gozargah website sync must match the account actually stored in 3x-ui.
    /// </remarks>
    private static XuiV3ClientPayload BuildClientPayload(AccountDto accountDto, XuiV3CreateAccountOptions options)
    {
        var trafficGb = options.TrafficGb > 0 ? options.TrafficGb : Convert.ToInt32(accountDto.TotoalGB);
        var trafficBytes = options.TrafficBytes > 0 ? options.TrafficBytes : ApiService.ConvertGBToBytes(trafficGb);
        var durationDays = options.DurationDays
            ?? ApiService.ConvertPeriodToDays(accountDto.SelectedPeriod);

        var email = string.IsNullOrWhiteSpace(options.Email)
            ? AccountGenerator.GenerateRandomAccountName()
            : options.Email;

        if (accountDto.AccountCounter > 0)
            email += "_" + accountDto.AccountCounter;

        var expiryTime = durationDays <= 0
            ? 0
            : DateTimeOffset.UtcNow.AddDays(durationDays).ToUnixTimeMilliseconds();

        if (options.StartExpiryAfterFirstUse && durationDays > 0)
            expiryTime = -(long)TimeSpan.FromDays(durationDays).TotalMilliseconds;

        return new XuiV3ClientPayload
        {
            Email = email,
            TotalGB = trafficBytes,
            ExpiryTime = expiryTime,
            TgId = accountDto.TelegramUserId,
            LimitIp = options.LimitIp,
            Enable = true,
            SubId = string.IsNullOrWhiteSpace(options.SubId) ? email : options.SubId,
            Flow = options.UseVisionFlow ? "xtls-rprx-vision" : null,
            Comment = options.Comment
        };
    }

    /// <summary>
    /// Extracts the real panel UUID from a direct VLESS or VMess link returned by 3x-ui.
    /// </summary>
    /// <param name="configLink">
    /// Direct configuration link returned by <c>GetClientLinksAsync</c>. VLESS links store the UUID before
    /// <c>@</c>; VMess links store it in the decoded JSON <c>id</c> field.
    /// </param>
    /// <param name="uuid">Extracted UUID when the method returns <c>true</c>; otherwise <c>null</c>.</param>
    /// <returns><c>true</c> when a valid UUID was found in the panel-generated link; otherwise <c>false</c>.</returns>
    /// <remarks>
    /// This is a fallback for panels that omit <c>uuid</c> from the client lookup response but still return a valid
    /// direct config link. The value is panel-derived and must not be replaced with a locally generated UUID.
    /// </remarks>
    private static bool TryExtractUuidFromConfigLink(string configLink, out string uuid)
    {
        uuid = null;
        if (string.IsNullOrWhiteSpace(configLink))
            return false;

        var value = configLink.Trim();
        if (value.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
        {
            var rest = value["vless://".Length..];
            var atIndex = rest.IndexOf('@');
            if (atIndex > 0 && Guid.TryParse(rest[..atIndex], out var parsed))
            {
                uuid = parsed.ToString();
                return true;
            }
        }

        if (value.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
        {
            var encoded = value["vmess://".Length..].Trim();
            try
            {
                encoded = encoded.PadRight(encoded.Length + ((4 - encoded.Length % 4) % 4), '=');
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                var token = JObject.Parse(json);
                var candidate = token["id"]?.ToString();
                if (Guid.TryParse(candidate, out var parsed))
                {
                    uuid = parsed.ToString();
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    public static string BuildSubscriptionLink(ServerInfo serverInfo, string subId)
    {
        if (serverInfo == null || string.IsNullOrWhiteSpace(subId))
            return null;

        if (!string.IsNullOrWhiteSpace(serverInfo.SubLinkUrl))
            return serverInfo.SubLinkUrl.TrimEnd('/') + "/" + subId;

        if (string.IsNullOrWhiteSpace(serverInfo.Url))
            return null;

        var baseUrl = serverInfo.Url.TrimEnd('/');
        var rootPath = (serverInfo.RootPath ?? string.Empty).Trim('/');
        var subPath = string.IsNullOrWhiteSpace(rootPath)
            ? "sub"
            : rootPath + "/sub";

        return baseUrl + "/" + subPath + "/" + EscapePath(subId);
    }
}

public enum XuiPanelApiVersion
{
    Unknown = 0,
    V2 = 2,
    V3 = 3
}

public class XuiV3ApiResponse<T>
{
    [JsonProperty("success")]
    public bool Success { get; set; }

    [JsonProperty("msg")]
    public string Msg { get; set; }

    [JsonProperty("obj")]
    public T Obj { get; set; }
}

public class XuiV3ApiException : Exception
{
    public string RequestMethod { get; }
    public string RequestUri { get; }
    public int StatusCode { get; }
    public string ResponseBody { get; }
    public string RequestBody { get; }

    public XuiV3ApiException(string requestMethod, string requestUri, int statusCode, string responseBody, string requestBody)
        : base($"3X-UI v3 API request failed with status {statusCode} for {requestMethod} {requestUri}: {responseBody}")
    {
        RequestMethod = requestMethod;
        RequestUri = requestUri;
        StatusCode = statusCode;
        ResponseBody = responseBody;
        RequestBody = requestBody;
    }
}

public class XuiV3CreateAccountOptions
{
    public IEnumerable<int> InboundIds { get; set; }
    public bool SaveUserStatus { get; set; } = true;
    public int TrafficGb { get; set; }
    public long TrafficBytes { get; set; }
    public int? DurationDays { get; set; }
    public int LimitIp { get; set; }
    public string Email { get; set; }
    public string SubId { get; set; }
    public string Comment { get; set; }
    public bool UseVisionFlow { get; set; }
    public bool StartExpiryAfterFirstUse { get; set; }
}

/// <summary>
/// Result returned after the bot creates one XUI v3 client on the configured 3x-ui panel.
/// </summary>
/// <remarks>
/// The object is used both for Telegram delivery and for downstream integrations such as tenant order
/// fulfillment and the Gozargah site sync outbox. Values come from the panel payload that was accepted by
/// 3x-ui, so callers may safely use <see cref="Email"/>, <see cref="Uuid"/>, and <see cref="SubId"/> as
/// idempotency keys for external systems after <see cref="Success"/> is <c>true</c>.
/// </remarks>
public class XuiV3AccountCreationResult
{
    /// <summary>
    /// Indicates whether 3x-ui accepted the client creation request.
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// API version used for the successful or failed panel operation.
    /// </summary>
    public XuiPanelApiVersion ApiVersion { get; set; }

    /// <summary>
    /// Human-readable result message from the bot or panel API.
    /// </summary>
    public string Message { get; set; }

    /// <summary>
    /// XUI client email/name generated by the bot and used as the main account identifier.
    /// </summary>
    public string Email { get; set; }

    /// <summary>
    /// UUID assigned to the XUI client. This value is required by VLESS links and by website sync payloads.
    /// </summary>
    public string Uuid { get; set; }

    /// <summary>
    /// Subscription id attached to the client. When empty, callers usually fall back to <see cref="Email"/>.
    /// </summary>
    public string SubId { get; set; }

    /// <summary>
    /// Direct configuration link returned or built for the created account.
    /// </summary>
    public string ConfigLink { get; set; }

    /// <summary>
    /// Subscription URL that can be sent to the Telegram user.
    /// </summary>
    public string SubLink { get; set; }

    /// <summary>
    /// Traffic allowance in gigabytes for display and audit metadata.
    /// </summary>
    public int TrafficGb { get; set; }

    /// <summary>
    /// Traffic allowance in bytes as sent to the 3x-ui panel.
    /// </summary>
    public long TrafficBytes { get; set; }

    /// <summary>
    /// Expiry timestamp in milliseconds, or a negative first-use duration for unlimited accounts.
    /// </summary>
    public long ExpiryTime { get; set; }

    /// <summary>
    /// Duration in days selected by the user, or <c>null</c> when the plan does not have a fixed day count.
    /// </summary>
    public int? DurationDays { get; set; }

    /// <summary>
    /// JSON comment stored on the XUI client. It contains bot, user, service, plan, and audit metadata.
    /// </summary>
    public string Comment { get; set; }

    /// <summary>
    /// XUI inbound ids used when attaching the client to the panel.
    /// </summary>
    public List<int> InboundIds { get; set; } = new List<int>();

    /// <summary>
    /// Raw panel response retained for diagnostics. Do not expose this value directly to Telegram users.
    /// </summary>
    public JToken RawResponse { get; set; }
}

public class XuiV3ClientCreateRequest
{
    [JsonProperty("client")]
    public XuiV3ClientPayload Client { get; set; }

    [JsonProperty("inboundIds")]
    public List<int> InboundIds { get; set; } = new List<int>();
}

public class XuiV3ClientPayload
{
    [JsonProperty("email")]
    public string Email { get; set; }

    [JsonProperty("uuid")]
    public string Uuid { get; set; }

    [JsonProperty("password")]
    public string Password { get; set; }

    [JsonProperty("totalGB")]
    public long TotalGB { get; set; }

    [JsonProperty("expiryTime")]
    public long ExpiryTime { get; set; }

    [JsonProperty("tgId")]
    public long TgId { get; set; }

    [JsonProperty("limitIp")]
    public int LimitIp { get; set; }

    [JsonProperty("enable")]
    public bool Enable { get; set; } = true;

    [JsonProperty("subId")]
    public string SubId { get; set; }

    [JsonProperty("flow")]
    public string Flow { get; set; }

    [JsonProperty("comment")]
    public string Comment { get; set; }

    [JsonProperty("group")]
    public string Group { get; set; }

    [JsonProperty("reverse")]
    public JToken Reverse { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JToken> Extra { get; set; }
}

public class XuiV3Client : XuiV3ClientPayload
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("inboundIds")]
    public List<int> InboundIds { get; set; } = new List<int>();

    [JsonProperty("traffic")]
    public XuiV3ClientTraffic Traffic { get; set; }
}

public class XuiV3ClientTraffic
{
    [JsonProperty("email")]
    public string Email { get; set; }

    [JsonProperty("up")]
    public long Up { get; set; }

    [JsonProperty("down")]
    public long Down { get; set; }

    [JsonProperty("total")]
    public long Total { get; set; }

    [JsonProperty("totalGB")]
    public long TotalGB { get; set; }

    [JsonProperty("expiryTime")]
    public long ExpiryTime { get; set; }

    [JsonProperty("enable")]
    public bool Enable { get; set; }
}

public class XuiV3ClientPageQuery
{
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 25;
    public string Search { get; set; }
    public string Filter { get; set; }
    public string Protocol { get; set; }
    public string Sort { get; set; }
    public string Order { get; set; }
}

public class XuiV3ClientGroup
{
    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("count")]
    public int Count { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JToken> Extra { get; set; }
}

public class XuiV3Inbound
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("userId")]
    public int UserId { get; set; }

    [JsonProperty("up")]
    public long Up { get; set; }

    [JsonProperty("down")]
    public long Down { get; set; }

    [JsonProperty("total")]
    public long Total { get; set; }

    [JsonProperty("remark")]
    public string Remark { get; set; }

    [JsonProperty("enable")]
    public bool Enable { get; set; } = true;

    [JsonProperty("expiryTime")]
    public long ExpiryTime { get; set; }

    [JsonProperty("listen")]
    public string Listen { get; set; }

    [JsonProperty("port")]
    public int Port { get; set; }

    [JsonProperty("protocol")]
    public string Protocol { get; set; }

    [JsonProperty("settings")]
    public JToken Settings { get; set; }

    [JsonProperty("streamSettings")]
    public JToken StreamSettings { get; set; }

    [JsonProperty("tag")]
    public string Tag { get; set; }

    [JsonProperty("sniffing")]
    public JToken Sniffing { get; set; }

    [JsonProperty("clientStats")]
    public JToken ClientStats { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JToken> Extra { get; set; }
}

public class XuiV3InboundOption
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("remark")]
    public string Remark { get; set; }

    [JsonProperty("protocol")]
    public string Protocol { get; set; }

    [JsonProperty("port")]
    public int Port { get; set; }

    [JsonProperty("tlsFlowCapable")]
    public bool TlsFlowCapable { get; set; }
}

public class XuiV3Node
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("remark")]
    public string Remark { get; set; }

    [JsonProperty("scheme")]
    public string Scheme { get; set; } = "https";

    [JsonProperty("address")]
    public string Address { get; set; }

    [JsonProperty("port")]
    public int Port { get; set; }

    [JsonProperty("basePath")]
    public string BasePath { get; set; } = "/";

    [JsonProperty("apiToken")]
    public string ApiToken { get; set; }

    [JsonProperty("enable")]
    public bool Enable { get; set; } = true;

    [JsonProperty("allowPrivateAddress")]
    public bool AllowPrivateAddress { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JToken> Extra { get; set; }
}

public class XuiV3CustomGeoSource
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("type")]
    public string Type { get; set; }

    [JsonProperty("alias")]
    public string Alias { get; set; }

    [JsonProperty("url")]
    public string Url { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JToken> Extra { get; set; }
}

public class XuiV3ServerStatus
{
    [JsonProperty("cpu")]
    public decimal Cpu { get; set; }

    [JsonProperty("mem")]
    public XuiV3SizePair Mem { get; set; }

    [JsonProperty("swap")]
    public XuiV3SizePair Swap { get; set; }

    [JsonProperty("disk")]
    public XuiV3SizePair Disk { get; set; }

    [JsonProperty("netIO")]
    public XuiV3NetIo NetIO { get; set; }

    [JsonProperty("xray")]
    public XuiV3XrayState Xray { get; set; }

    [JsonProperty("tcpCount")]
    public int TcpCount { get; set; }

    [JsonProperty("load")]
    public JToken Load { get; set; }

    [JsonExtensionData]
    public IDictionary<string, JToken> Extra { get; set; }
}

public class XuiV3SizePair
{
    [JsonProperty("current")]
    public long Current { get; set; }

    [JsonProperty("total")]
    public long Total { get; set; }
}

public class XuiV3NetIo
{
    [JsonProperty("up")]
    public long Up { get; set; }

    [JsonProperty("down")]
    public long Down { get; set; }
}

public class XuiV3XrayState
{
    [JsonProperty("state")]
    public string State { get; set; }

    [JsonProperty("version")]
    public string Version { get; set; }
}

public class XuiV3ApiTokenInfo
{
    [JsonProperty("id")]
    public int Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("enabled")]
    public bool Enabled { get; set; }

    [JsonProperty("createdAt")]
    public long CreatedAt { get; set; }
}

public class XuiV3ApiTokenCreated : XuiV3ApiTokenInfo
{
    [JsonProperty("token")]
    public string Token { get; set; }
}
