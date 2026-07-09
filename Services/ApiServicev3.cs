using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class ApiServicev3
{
    private const int DefaultXuiV3TimeoutSeconds = 60;
    private const int DefaultXuiV3TransientRetryCount = 3;
    private const int DefaultXuiV3TransientRetryBaseDelayMs = 1500;
    private const int DefaultXuiV3TransientRetryMaxDelayMs = 12000;

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
        XuiV3ApiResponse<JToken> response;
        try
        {
            response = await AddClientAsync(
                accountDto.ServerInfo,
                configuration,
                new XuiV3ClientCreateRequest { Client = client, InboundIds = inboundIds },
                cancellationToken);
        }
        catch (Exception ex) when (IsTransientXuiTransportException(ex, cancellationToken))
        {
            var recovered = await TryRecoverCreatedClientAsync(
                accountDto,
                configuration,
                client,
                inboundIds,
                trafficGb,
                trafficBytes,
                options,
                responseObj: null,
                cancellationToken);

            return recovered ?? BuildTransientCreationFailure(client.Email, ex);
        }
        catch (XuiV3ApiException ex) when (CouldIndicateExistingClient(ex))
        {
            var recovered = await TryRecoverCreatedClientAsync(
                accountDto,
                configuration,
                client,
                inboundIds,
                trafficGb,
                trafficBytes,
                options,
                responseObj: null,
                cancellationToken);

            if (recovered != null)
                return recovered;

            throw;
        }

        if (!response.Success)
        {
            if (CouldIndicateExistingClient(response.Msg))
            {
                var recovered = await TryRecoverCreatedClientAsync(
                    accountDto,
                    configuration,
                    client,
                    inboundIds,
                    trafficGb,
                    trafficBytes,
                    options,
                    response.Obj,
                    cancellationToken);

                if (recovered != null)
                    return recovered;
            }

            return new XuiV3AccountCreationResult
            {
                Success = false,
                ApiVersion = XuiPanelApiVersion.V3,
                Email = client.Email,
                Message = response.Msg
            };
        }

        try
        {
            return await BuildAccountCreationResultAsync(
                accountDto,
                configuration,
                client,
                inboundIds,
                trafficGb,
                trafficBytes,
                options,
                response.Obj,
                cancellationToken);
        }
        catch (Exception ex) when (IsTransientXuiTransportException(ex, cancellationToken))
        {
            var recovered = await TryRecoverCreatedClientAsync(
                accountDto,
                configuration,
                client,
                inboundIds,
                trafficGb,
                trafficBytes,
                options,
                response.Obj,
                cancellationToken);

            return recovered ?? BuildTransientCreationFailure(client.Email, ex);
        }
    }

    /// <summary>
    /// Builds the final account creation result after the panel has accepted the add-client request.
    /// </summary>
    /// <param name="accountDto">
    /// Bot-facing account request that contains the Telegram owner id, target panel, selected service, period, and
    /// user-facing traffic values.
    /// </param>
    /// <param name="configuration">Application configuration used for XUI v3 API timeout, retry, and token settings.</param>
    /// <param name="client">The client payload that was submitted to 3x-ui.</param>
    /// <param name="inboundIds">The active XUI inbound ids that the client was attached to.</param>
    /// <param name="trafficGb">Display traffic limit in GB for the bot message and saved state.</param>
    /// <param name="trafficBytes">Traffic limit in bytes that was sent to the panel.</param>
    /// <param name="options">Resolved creation options that control duration, status persistence, and metadata.</param>
    /// <param name="responseObj">Raw successful add-client API response from the panel, stored for diagnostics.</param>
    /// <param name="cancellationToken">Cancellation token for follow-up panel reads and optional state persistence.</param>
    /// <returns>
    /// A successful creation result using the real panel client when it can be read, including the real UUID and subId.
    /// </returns>
    /// <remarks>
    /// The add-client endpoint can succeed while later reads are slow after a 3x-ui update. This method is deliberately
    /// separated from the add step so callers can retry/recover the read side without sending another add request.
    /// </remarks>
    private static async Task<XuiV3AccountCreationResult> BuildAccountCreationResultAsync(
        AccountDto accountDto,
        IConfiguration configuration,
        XuiV3ClientPayload client,
        List<int> inboundIds,
        int trafficGb,
        long trafficBytes,
        XuiV3CreateAccountOptions options,
        JToken responseObj,
        CancellationToken cancellationToken)
    {
        var panelClientResponse = await GetClientAsync(accountDto.ServerInfo, configuration, client.Email, cancellationToken);
        var panelClient = panelClientResponse.Success && panelClientResponse.Obj != null
            ? panelClientResponse.Obj
            : null;

        return await BuildAccountCreationResultFromKnownClientAsync(
            accountDto,
            configuration,
            client,
            panelClient,
            inboundIds,
            trafficGb,
            trafficBytes,
            options,
            responseObj,
            cancellationToken);
    }

    /// <summary>
    /// Builds a creation result from a panel client that is already known or has just been recovered by email.
    /// </summary>
    /// <param name="accountDto">Original bot account request that owns the saved state and subscription base link.</param>
    /// <param name="configuration">Application configuration used for XUI v3 link lookup.</param>
    /// <param name="client">Submitted client payload used as a fallback when the panel omits a field.</param>
    /// <param name="panelClient">Client row read back from the panel, or <c>null</c> when only add-client success is known.</param>
    /// <param name="inboundIds">Inbound ids that were selected from the active service plan.</param>
    /// <param name="trafficGb">Traffic amount in GB shown to the Telegram user.</param>
    /// <param name="trafficBytes">Traffic amount in bytes stored in the result for downstream sync and logs.</param>
    /// <param name="options">Creation options that control whether legacy user state is saved.</param>
    /// <param name="responseObj">Raw successful add-client response, when available.</param>
    /// <param name="cancellationToken">Cancellation token for optional link lookup and state persistence.</param>
    /// <returns>
    /// A successful creation result. The config link may be <c>null</c> when link retrieval fails after the account row
    /// is already confirmed on the panel.
    /// </returns>
    /// <remarks>
    /// Link lookup is best-effort once the panel client has been confirmed. Returning the real panel UUID is more
    /// important than failing the whole purchase because the link endpoint timed out.
    /// </remarks>
    private static async Task<XuiV3AccountCreationResult> BuildAccountCreationResultFromKnownClientAsync(
        AccountDto accountDto,
        IConfiguration configuration,
        XuiV3ClientPayload client,
        XuiV3Client panelClient,
        List<int> inboundIds,
        int trafficGb,
        long trafficBytes,
        XuiV3CreateAccountOptions options,
        JToken responseObj,
        CancellationToken cancellationToken)
    {
        XuiV3ApiResponse<List<string>> links = null;
        try
        {
            links = await GetClientLinksAsync(accountDto.ServerInfo, configuration, client.Email, cancellationToken);
        }
        catch (Exception ex) when (IsTransientXuiTransportException(ex, cancellationToken))
        {
            Console.WriteLine($"[XUIv3] Link lookup failed after account creation. email={client.Email}, error={ex.Message}");
        }

        var configLink = links?.Obj?.FirstOrDefault();
        var linkUuid = TryExtractUuidFromConfigLink(configLink, out var extractedUuid) ? extractedUuid : null;
        var subLink = BuildSubscriptionLink(accountDto.ServerInfo, FirstNonWhiteSpace(panelClient?.SubId, client.SubId, client.Email));

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
            RawResponse = responseObj
        };
    }

    /// <summary>
    /// Attempts to recover a successful creation result by reading the client email after a transient add/read failure.
    /// </summary>
    /// <param name="accountDto">Original account request containing the target XUI server and Telegram owner id.</param>
    /// <param name="configuration">Application configuration for XUI v3 retries and authentication.</param>
    /// <param name="client">Client payload whose email is used as the idempotency key on the panel.</param>
    /// <param name="inboundIds">Inbound ids selected for the account creation attempt.</param>
    /// <param name="trafficGb">Traffic amount in GB that should be reported if recovery succeeds.</param>
    /// <param name="trafficBytes">Traffic amount in bytes that should be reported if recovery succeeds.</param>
    /// <param name="options">Creation options used for state persistence and metadata.</param>
    /// <param name="responseObj">Raw add-client response, when the add call completed before the later failure.</param>
    /// <param name="cancellationToken">Cancellation token for the recovery lookup.</param>
    /// <returns>
    /// A successful creation result when the panel contains the client; otherwise <c>null</c> so the caller can return a
    /// controlled transient failure without creating a duplicate account.
    /// </returns>
    /// <remarks>
    /// This is the duplicate-protection path for ambiguous network failures. It never sends another add-client request;
    /// it only reads the panel by email and builds the result from the existing row.
    /// </remarks>
    private static async Task<XuiV3AccountCreationResult> TryRecoverCreatedClientAsync(
        AccountDto accountDto,
        IConfiguration configuration,
        XuiV3ClientPayload client,
        List<int> inboundIds,
        int trafficGb,
        long trafficBytes,
        XuiV3CreateAccountOptions options,
        JToken responseObj,
        CancellationToken cancellationToken)
    {
        try
        {
            var panelClientResponse = await GetClientAsync(accountDto.ServerInfo, configuration, client.Email, cancellationToken);
            if (!panelClientResponse.Success || panelClientResponse.Obj == null)
                return null;

            Console.WriteLine($"[XUIv3] Recovered created client after transient failure. email={client.Email}");
            return await BuildAccountCreationResultFromKnownClientAsync(
                accountDto,
                configuration,
                client,
                panelClientResponse.Obj,
                inboundIds,
                trafficGb,
                trafficBytes,
                options,
                responseObj,
                cancellationToken);
        }
        catch (Exception ex) when (IsTransientXuiTransportException(ex, cancellationToken) || ex is XuiV3ApiException)
        {
            Console.WriteLine($"[XUIv3] Could not recover created client after transient failure. email={client.Email}, error={ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Creates a user-facing failed creation result for a transient XUI v3 transport error.
    /// </summary>
    /// <param name="email">Generated account email that was being created.</param>
    /// <param name="exception">Transient transport exception observed while talking to the panel.</param>
    /// <returns>
    /// A failed account creation result that does not expose panel secrets and can be shown by bot flows as a retryable
    /// panel problem.
    /// </returns>
    /// <remarks>
    /// Returning a normal result object prevents a network glitch from escaping to Telegram polling and stopping the
    /// active receiver.
    /// </remarks>
    private static XuiV3AccountCreationResult BuildTransientCreationFailure(string email, Exception exception)
    {
        Console.WriteLine($"[XUIv3] Account creation failed with transient panel transport error. email={email}, error={exception.Message}");
        return new XuiV3AccountCreationResult
        {
            Success = false,
            ApiVersion = XuiPanelApiVersion.V3,
            Email = email,
            Message = "ارتباط با پنل 3x-ui پایدار نبود. لطفاً چند دقیقه دیگر دوباره تلاش کنید."
        };
    }

    /// <summary>
    /// Checks whether an add-client response may mean the previous ambiguous attempt already created the same email.
    /// </summary>
    /// <param name="exception">XUI v3 API exception thrown by the add-client endpoint.</param>
    /// <returns>
    /// <c>true</c> when the response status/body looks like a duplicate-client response that should be verified by
    /// reading the client by email; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// This is not a retry rule. It is an idempotency guard used only after add-client returns an existing-client style
    /// response, which can happen when a previous transport failure hid a successful server-side creation.
    /// </remarks>
    private static bool CouldIndicateExistingClient(XuiV3ApiException exception)
    {
        if (exception == null)
            return false;

        return exception.StatusCode == 409 || CouldIndicateExistingClient(exception.ResponseBody);
    }

    /// <summary>
    /// Checks whether a panel message appears to describe an already existing client/email.
    /// </summary>
    /// <param name="message">Response message or body returned by 3x-ui.</param>
    /// <returns><c>true</c> when the message contains common duplicate-client wording; otherwise <c>false</c>.</returns>
    private static bool CouldIndicateExistingClient(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("already", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("exist", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("重复", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("تکراری", StringComparison.OrdinalIgnoreCase);
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
        var fileName = Path.GetFileName(dbFilePath);
        var raw = await SendRawWithRetryAsync(
            serverInfo,
            configuration,
            HttpMethod.Post,
            "/panel/api/server/importDB",
            async token =>
            {
                var content = new MultipartFormDataContent();
                content.Add(new ByteArrayContent(await File.ReadAllBytesAsync(dbFilePath, token)), "db", fileName);
                return content;
            },
            true,
            cancellationToken,
            query: null,
            requestBodyForLog: "<multipart-db-content>");
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
        var safeFields = fields ?? new Dictionary<string, string>();
        var raw = await SendRawWithRetryAsync(
            serverInfo,
            configuration,
            HttpMethod.Post,
            relativePath,
            _ => Task.FromResult<HttpContent>(new FormUrlEncodedContent(safeFields)),
            true,
            cancellationToken,
            query: null,
            requestBodyForLog: "<form-content>");
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
        return await SendWithRetryAsync(
            configuration,
            method,
            uri,
            async () =>
            {
                using var httpClient = CreateHttpClient(configuration);
                using var content = BuildContent(body);
                using var request = BuildRequest(method, uri, content, serverInfo, configuration, authenticate);
                using var response = await httpClient.SendAsync(request, cancellationToken);
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    var text = Encoding.UTF8.GetString(bytes);
                    throw new XuiV3ApiException(method.ToString(), uri.ToString(), (int)response.StatusCode, text, SerializeBodyForLog(body));
                }

                return bytes;
            },
            cancellationToken);
    }

    /// <summary>
    /// Sends one XUI v3 request and retries transient transport or gateway failures with bounded backoff.
    /// </summary>
    /// <param name="serverInfo">Target XUI panel configuration that provides base URL, root path, and bearer token.</param>
    /// <param name="configuration">Application configuration containing timeout and retry settings.</param>
    /// <param name="method">HTTP method used by the XUI endpoint.</param>
    /// <param name="relativePath">Panel-relative path, without duplicating the root path.</param>
    /// <param name="contentFactory">
    /// Factory that creates a fresh HTTP content object for each attempt. This is required because <see cref="HttpContent"/>
    /// instances cannot be safely re-sent after a failed attempt.
    /// </param>
    /// <param name="authenticate">Whether the request should include the XUI v3 bearer token.</param>
    /// <param name="cancellationToken">Cancellation token for the active Telegram update or background operation.</param>
    /// <param name="query">Optional query string values for endpoints that support filtering or paging.</param>
    /// <param name="requestBodyForLog">Sanitized request body label used only in exceptions; never include tokens here.</param>
    /// <returns>The response body as UTF-8 text when the panel returns a successful status code.</returns>
    /// <remarks>
    /// HTTP 400/401/403/404/422 responses are treated as panel/business failures and are not retried. HTTP 429 and
    /// gateway 5xx responses are retried because 3x-ui 3.4.x panels can temporarily stall under account creation load.
    /// </remarks>
    private static Task<string> SendRawWithRetryAsync(
        ServerInfo serverInfo,
        IConfiguration configuration,
        HttpMethod method,
        string relativePath,
        Func<CancellationToken, Task<HttpContent>> contentFactory,
        bool authenticate,
        CancellationToken cancellationToken,
        IDictionary<string, string> query = null,
        string requestBodyForLog = null)
    {
        var uri = BuildPanelUri(serverInfo, relativePath, query);
        return SendWithRetryAsync(
            configuration,
            method,
            uri,
            async () =>
            {
                using var httpClient = CreateHttpClient(configuration);
                using var content = contentFactory == null ? null : await contentFactory(cancellationToken);
                using var request = BuildRequest(method, uri, content, serverInfo, configuration, authenticate);
                using var response = await httpClient.SendAsync(request, cancellationToken);
                var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                    throw new XuiV3ApiException(method.ToString(), uri.ToString(), (int)response.StatusCode, responseText, requestBodyForLog);

                return responseText;
            },
            cancellationToken);
    }

    /// <summary>
    /// Executes an XUI v3 HTTP operation with retry semantics shared by JSON, form, and byte-response endpoints.
    /// </summary>
    /// <typeparam name="T">Response type produced by the supplied HTTP operation.</typeparam>
    /// <param name="configuration">Application configuration that supplies retry count and delay settings.</param>
    /// <param name="method">HTTP method being sent, used for retry diagnostics.</param>
    /// <param name="uri">Fully built XUI panel request URI. It must not include bearer tokens or other secrets.</param>
    /// <param name="operation">Factory that sends exactly one HTTP attempt and returns the parsed response data.</param>
    /// <param name="cancellationToken">Cancellation token that stops retries when the Telegram update is cancelled.</param>
    /// <returns>The value returned by the first successful HTTP attempt.</returns>
    /// <remarks>
    /// This method deliberately logs retry attempts to the process console instead of the Telegram logger channel. The
    /// retry details are operational diagnostics and should not spam the private payment/audit channel.
    /// </remarks>
    private static async Task<T> SendWithRetryAsync<T>(
        IConfiguration configuration,
        HttpMethod method,
        Uri uri,
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        var appConfig = configuration?.Get<AppConfig>() ?? new AppConfig();
        var retryCount = appConfig.XuiV3TransientRetryCount < 0
            ? DefaultXuiV3TransientRetryCount
            : appConfig.XuiV3TransientRetryCount;
        var maxAttempts = Math.Max(1, retryCount + 1);
        Exception lastException = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await operation();
            }
            catch (Exception ex) when (ShouldRetryXuiRequest(ex, cancellationToken, attempt, maxAttempts))
            {
                lastException = ex;
                var delay = GetXuiRetryDelay(appConfig, attempt);
                Console.WriteLine(
                    $"[XUIv3] transient API failure; retrying. attempt={attempt}/{maxAttempts}, method={method}, uri={uri}, delayMs={delay.TotalMilliseconds:0}, error={ex.Message}");
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw lastException ?? new InvalidOperationException("XUI v3 request failed without an exception.");
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
        return await SendRawWithRetryAsync(
            serverInfo,
            configuration,
            method,
            relativePath,
            _ => Task.FromResult(BuildContent(body)),
            authenticate,
            cancellationToken,
            query,
            SerializeBodyForLog(body));
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
        var timeoutSeconds = appConfig.XuiV3RequestTimeoutSeconds <= 0 ? DefaultXuiV3TimeoutSeconds : appConfig.XuiV3RequestTimeoutSeconds;
        return new HttpClient { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
    }

    /// <summary>
    /// Determines whether a failed XUI v3 HTTP attempt is safe to retry.
    /// </summary>
    /// <param name="exception">
    /// Exception thrown by one HTTP attempt. It may be a provider response wrapped in <see cref="XuiV3ApiException"/>
    /// or a transport exception such as <see cref="HttpRequestException"/>.
    /// </param>
    /// <param name="cancellationToken">
    /// Cancellation token for the active Telegram update. Cancelled shutdown operations are not treated as retryable.
    /// </param>
    /// <param name="attempt">The 1-based attempt number that just failed.</param>
    /// <param name="maxAttempts">Maximum number of attempts allowed for this request.</param>
    /// <returns>
    /// <c>true</c> when another attempt should be made; otherwise <c>false</c> so the original error is surfaced.
    /// </returns>
    /// <remarks>
    /// HTTP 429 and gateway 5xx errors are retryable. Normal 3x-ui API validation/auth/not-found errors are not,
    /// because retrying them can hide a real configuration or business-rule problem.
    /// </remarks>
    private static bool ShouldRetryXuiRequest(Exception exception, CancellationToken cancellationToken, int attempt, int maxAttempts)
    {
        if (attempt >= maxAttempts || cancellationToken.IsCancellationRequested)
            return false;

        if (exception is XuiV3ApiException apiException)
            return IsTransientXuiStatusCode(apiException.StatusCode);

        return IsTransientXuiTransportException(exception, cancellationToken);
    }

    /// <summary>
    /// Detects transient network and TLS failures that can happen while the bot talks to the XUI v3 panel.
    /// </summary>
    /// <param name="exception">Exception thrown by an XUI v3 request or by a follow-up read after account creation.</param>
    /// <param name="cancellationToken">Cancellation token for the active operation; shutdown cancellations are ignored.</param>
    /// <returns>
    /// <c>true</c> for retryable transport failures such as timeout, DNS/socket failure, TLS bad record MAC, gateway
    /// failure, or connection reset; otherwise <c>false</c>.
    /// </returns>
    /// <remarks>
    /// 3x-ui 3.4.x panels can be slow after add-client operations. Treating these failures as transient prevents one
    /// failed panel read from escaping to Telegram polling and stopping an owned or tenant bot receiver.
    /// </remarks>
    public static bool IsTransientXuiTransportException(Exception exception, CancellationToken cancellationToken = default)
    {
        if (exception == null || cancellationToken.IsCancellationRequested)
            return false;

        if (exception is TimeoutException or TaskCanceledException or OperationCanceledException)
            return true;

        if (exception is XuiV3ApiException apiException)
            return IsTransientXuiStatusCode(apiException.StatusCode);

        foreach (var current in FlattenExceptionChain(exception))
        {
            if (current is HttpRequestException httpRequestException)
            {
                if (httpRequestException.StatusCode.HasValue &&
                    IsTransientXuiStatusCode((int)httpRequestException.StatusCode.Value))
                {
                    return true;
                }

                return true;
            }

            if (current is IOException or SocketException)
                return true;

            var message = current.Message ?? string.Empty;
            if (ContainsTransientXuiTransportMarker(message))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Checks whether an HTTP status code represents a temporary panel or gateway failure.
    /// </summary>
    /// <param name="statusCode">Numeric HTTP status code returned by 3x-ui or the proxy in front of it.</param>
    /// <returns>
    /// <c>true</c> for HTTP 429, 502, 503, and 504; otherwise <c>false</c>. Business 4xx errors are intentionally
    /// excluded.
    /// </returns>
    private static bool IsTransientXuiStatusCode(int statusCode)
    {
        return statusCode == 429 ||
               statusCode == 502 ||
               statusCode == 503 ||
               statusCode == 504;
    }

    /// <summary>
    /// Looks for common transport error fragments emitted by .NET, OpenSSL, proxies, and Telegram-hosted Linux builds.
    /// </summary>
    /// <param name="message">Exception message from any level of the exception chain.</param>
    /// <returns><c>true</c> when the text describes a retryable transport failure.</returns>
    private static bool ContainsTransientXuiTransportMarker(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;

        return message.Contains("HttpClient.Timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("request timed out", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("decryption failed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("bad record mac", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("SSL_ERROR_SSL", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("connection reset", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("broken pipe", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("No such host", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("Bad Gateway", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("gateway timeout", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("service unavailable", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Calculates the bounded exponential backoff delay for a retry attempt.
    /// </summary>
    /// <param name="appConfig">Runtime configuration containing XUI v3 retry delay settings.</param>
    /// <param name="failedAttempt">The 1-based attempt number that just failed.</param>
    /// <returns>A delay between the configured base and maximum retry delay.</returns>
    private static TimeSpan GetXuiRetryDelay(AppConfig appConfig, int failedAttempt)
    {
        var baseDelay = appConfig.XuiV3TransientRetryBaseDelayMs <= 0
            ? DefaultXuiV3TransientRetryBaseDelayMs
            : appConfig.XuiV3TransientRetryBaseDelayMs;
        var maxDelay = appConfig.XuiV3TransientRetryMaxDelayMs <= 0
            ? DefaultXuiV3TransientRetryMaxDelayMs
            : appConfig.XuiV3TransientRetryMaxDelayMs;
        var multiplier = Math.Pow(2, Math.Max(0, failedAttempt - 1));
        var delayMs = Math.Min(maxDelay, baseDelay * multiplier);
        return TimeSpan.FromMilliseconds(delayMs);
    }

    /// <summary>
    /// Enumerates an exception and all nested inner exceptions from outermost to innermost.
    /// </summary>
    /// <param name="exception">Root exception caught from an XUI v3 request or Telegram update handler.</param>
    /// <returns>An enumerable containing the root exception followed by its inner exceptions.</returns>
    private static IEnumerable<Exception> FlattenExceptionChain(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
            yield return current;
    }

    /// <summary>
    /// Creates fresh HTTP content for a request attempt from a JSON body or prebuilt content.
    /// </summary>
    /// <param name="body">
    /// Request body object. JSON objects are serialized into a new <see cref="StringContent"/> for every attempt.
    /// Prebuilt <see cref="HttpContent"/> values should only be used by non-retried legacy callers.
    /// </param>
    /// <returns>A disposable HTTP content object, or <c>null</c> for body-less requests.</returns>
    private static HttpContent BuildContent(object body)
    {
        if (body is HttpContent httpContent)
            return httpContent;

        return body == null
            ? null
            : new StringContent(SerializeBody(body), Encoding.UTF8, "application/json");
    }

    /// <summary>
    /// Serializes a request body for exception diagnostics without exposing bearer tokens.
    /// </summary>
    /// <param name="body">The request body object passed to an XUI v3 API helper.</param>
    /// <returns>A JSON string, a content placeholder, or <c>null</c> when no body was sent.</returns>
    private static string SerializeBodyForLog(object body)
    {
        return body is HttpContent ? "<http-content>" : SerializeBody(body);
    }

    private static HttpRequestMessage BuildRequest(
        HttpMethod method,
        Uri uri,
        HttpContent content,
        ServerInfo serverInfo,
        IConfiguration configuration,
        bool authenticate)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Version = HttpVersion.Version11;
        request.Headers.ConnectionClose = true;

        if (authenticate)
        {
            var token = ResolveBearerToken(serverInfo, configuration);
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", StripBearerPrefix(token));
        }

        if (content != null)
            request.Content = content;

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
