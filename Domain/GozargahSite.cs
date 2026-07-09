using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Adminbot.Domain
{
    /// <summary>
    /// Operation names stored in <see cref="GozargahSiteSyncEvent.Operation"/> for the Gozargah site outbox.
    /// </summary>
    public static class GozargahSiteSyncOperations
    {
        /// <summary>
        /// Registers a newly created XUI v3 account in the Gozargah website orders table.
        /// </summary>
        public const string Create = "create";

        /// <summary>
        /// Updates an existing website order after renewal, account edit, or link replacement.
        /// </summary>
        public const string Update = "update";

        /// <summary>
        /// Renames an existing website order after the bot changes the XUI email, UUID, and subscription id.
        /// </summary>
        public const string Rename = "rename";

        /// <summary>
        /// Deletes an order from the Gozargah website after the matching XUI v3 account is deleted.
        /// </summary>
        public const string Delete = "delete";
    }

    /// <summary>
    /// Processing states for <see cref="GozargahSiteSyncEvent"/> rows.
    /// </summary>
    public static class GozargahSiteSyncStatuses
    {
        /// <summary>
        /// The event is waiting for the background retry worker or immediate sender.
        /// </summary>
        public const string Pending = "pending";

        /// <summary>
        /// The event was successfully accepted by the Gozargah website API.
        /// </summary>
        public const string Succeeded = "succeeded";

        /// <summary>
        /// The event failed but should be retried later.
        /// </summary>
        public const string Failed = "failed";

        /// <summary>
        /// The event was intentionally skipped because sync is disabled, the user is banned, or site user data is missing.
        /// </summary>
        public const string Skipped = "skipped";
    }

    /// <summary>
    /// Outbox row used to synchronize one XUI v3 lifecycle operation with the Gozargah website.
    /// </summary>
    /// <remarks>
    /// Rows are stored in <c>users.db</c>, not <c>credentials.db</c>. The row is tenant-aware through
    /// <see cref="BotId"/> and <see cref="TenantBotId"/> and idempotent through the combination of operation,
    /// email/name, UUID, subscription id, and optional site order id. Successful rows remain as an audit trail.
    /// </remarks>
    public class GozargahSiteSyncEvent
    {
        /// <summary>
        /// Internal users.db id for this outbox row.
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Bot id that initiated the operation. Owned bots use their configured id; tenant sales use the tenant bot id.
        /// </summary>
        public string BotId { get; set; }

        /// <summary>
        /// Tenant storefront id when the operation belongs to a tenant sale; otherwise null for owned bots.
        /// </summary>
        public string TenantBotId { get; set; }

        /// <summary>
        /// Telegram user id of the website account that should own the order on Gozargah.
        /// For tenant sales this is the tenant owner, not the storefront customer.
        /// </summary>
        public long TelegramUserId { get; set; }

        /// <summary>
        /// Tenant owner Telegram user id when a tenant operation is being synced; null for owned-bot customer operations.
        /// </summary>
        public long? OwnerTelegramUserId { get; set; }

        /// <summary>
        /// Buyer Telegram user id when different from <see cref="TelegramUserId"/>, mainly for tenant storefront sales.
        /// </summary>
        public long? BuyerTelegramUserId { get; set; }

        /// <summary>
        /// XUI client email and Gozargah order name used by update and delete requests.
        /// </summary>
        public string Email { get; set; }

        /// <summary>
        /// Previous XUI client email when the operation is a link replacement that renames the site order.
        /// </summary>
        public string PreviousEmail { get; set; }

        /// <summary>
        /// XUI v3 UUID for the account after creation or rename.
        /// </summary>
        public string Uuid { get; set; }

        /// <summary>
        /// XUI v3 subscription id after creation or rename.
        /// </summary>
        public string SubId { get; set; }

        /// <summary>
        /// Full subscription URL sent to the website API.
        /// </summary>
        public string SubLink { get; set; }

        /// <summary>
        /// Operation name from <see cref="GozargahSiteSyncOperations"/>.
        /// </summary>
        public string Operation { get; set; }

        /// <summary>
        /// Last known website order id returned by the API when create or update succeeds.
        /// </summary>
        public string SiteOrderId { get; set; }

        /// <summary>
        /// Current outbox status from <see cref="GozargahSiteSyncStatuses"/>.
        /// </summary>
        public string Status { get; set; } = GozargahSiteSyncStatuses.Pending;

        /// <summary>
        /// Number of send attempts already performed against the website API.
        /// </summary>
        public int RetryCount { get; set; }

        /// <summary>
        /// Last API or validation error. Secrets are never stored here.
        /// </summary>
        public string LastError { get; set; }

        /// <summary>
        /// JSON request payload sent to the website API. Stored for audit and retry.
        /// </summary>
        public string RequestJson { get; set; }

        /// <summary>
        /// Raw JSON response returned by the website API for the last attempt.
        /// </summary>
        public string ResponseJson { get; set; }

        /// <summary>
        /// UTC creation time of the outbox row.
        /// </summary>
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// UTC time of the last status or payload update.
        /// </summary>
        public DateTime? UpdatedAtUtc { get; set; }

        /// <summary>
        /// UTC time when the website API accepted this event.
        /// </summary>
        public DateTime? SucceededAtUtc { get; set; }
    }

    /// <summary>
    /// One normalized order payload sent to the Gozargah website API.
    /// </summary>
    public class GozargahSiteOrderPayload
    {
        /// <summary>
        /// Website plan id: 1 for national, 2 for normal metered, and 5 for unlimited fair-usage plans.
        /// </summary>
        [JsonProperty("plan_id")]
        public int PlanId { get; set; }

        /// <summary>
        /// Comma-separated inbound ids expected by the website.
        /// </summary>
        [JsonProperty("inbound")]
        public string Inbound { get; set; }

        /// <summary>
        /// Website arranged-plan id. Unlimited plans use 6, 7, 8, or 9; metered services use 0.
        /// </summary>
        [JsonProperty("arranged_plan_id")]
        public int ArrangedPlanId { get; set; }

        /// <summary>
        /// Current website order name, which matches the XUI client email.
        /// </summary>
        [JsonProperty("name")]
        public string Name { get; set; }

        /// <summary>
        /// New website order name used only for rename/link-change operations.
        /// </summary>
        [JsonProperty("new_name")]
        public string NewName { get; set; }

        /// <summary>
        /// XUI UUID for the account.
        /// </summary>
        [JsonProperty("uuid")]
        public string Uuid { get; set; }

        /// <summary>
        /// Audit comment stored on the website order.
        /// </summary>
        [JsonProperty("comment")]
        public string Comment { get; set; }

        /// <summary>
        /// Order price in Iranian toman. The website API expects this as a string.
        /// </summary>
        [JsonProperty("price")]
        public string Price { get; set; }

        /// <summary>
        /// Traffic or fair-usage volume in GB.
        /// </summary>
        [JsonProperty("volume")]
        public decimal Volume { get; set; }

        /// <summary>
        /// Account duration in days. A value of 0 represents unlimited time for metered life plans.
        /// </summary>
        [JsonProperty("date")]
        public int Date { get; set; }

        /// <summary>
        /// Gozargah website username that owns the order.
        /// </summary>
        [JsonProperty("username")]
        public string Username { get; set; }

        /// <summary>
        /// Tracking code that links the website order to a local bot operation or tenant order.
        /// </summary>
        [JsonProperty("tracking_code")]
        public string TrackingCode { get; set; }

        /// <summary>
        /// Full subscription link for the account.
        /// </summary>
        [JsonProperty("sub")]
        public string Sub { get; set; }

        /// <summary>
        /// 1 for trial/free accounts and 0 for paid accounts.
        /// </summary>
        [JsonProperty("trial")]
        public int Trial { get; set; }

        /// <summary>
        /// 1 because all payloads created by this integration come from the Telegram bot.
        /// </summary>
        [JsonProperty("bot")]
        public int Bot { get; set; } = 1;
    }

    /// <summary>
    /// Raw response wrapper returned by the Gozargah API.
    /// </summary>
    /// <typeparam name="TData">Expected shape of the <c>data</c> field.</typeparam>
    public class GozargahSiteApiResponse<TData>
    {
        /// <summary>
        /// Whether the website API accepted the request.
        /// </summary>
        [JsonProperty("success")]
        public bool Success { get; set; }

        /// <summary>
        /// Echoed action name returned by the website API.
        /// </summary>
        [JsonProperty("action")]
        public string Action { get; set; }

        /// <summary>
        /// Human-readable API message.
        /// </summary>
        [JsonProperty("message")]
        public string Message { get; set; }

        /// <summary>
        /// Order id returned by create, update, or delete actions when available.
        /// </summary>
        [JsonProperty("id")]
        public long? Id { get; set; }

        /// <summary>
        /// Action-specific response data.
        /// </summary>
        [JsonProperty("data")]
        public TData Data { get; set; }
    }

    /// <summary>
    /// User row returned by the Gozargah <c>get_user</c> action.
    /// </summary>
    public class GozargahSiteUserData
    {
        /// <summary>
        /// Website username used as the owner username in order payloads.
        /// </summary>
        [JsonProperty("username")]
        public string Username { get; set; }

        /// <summary>
        /// Telegram user id string stored on the website.
        /// </summary>
        [JsonProperty("telegram_id")]
        public string TelegramId { get; set; }

        /// <summary>
        /// User email stored on the website when available.
        /// </summary>
        [JsonProperty("email")]
        public string Email { get; set; }

        /// <summary>
        /// Website wallet balance in Iranian toman.
        /// </summary>
        [JsonProperty("wallet")]
        public long Wallet { get; set; }

        /// <summary>
        /// Website ban flag. A value of 1 means the bot must not serve this user through site-connected flows.
        /// </summary>
        [JsonProperty("ban")]
        public int Ban { get; set; }

        /// <summary>
        /// True when the website says this user is banned.
        /// </summary>
        [JsonIgnore]
        public bool IsBanned => Ban == 1;
    }

    /// <summary>
    /// Response data returned by the Gozargah wallet deduction endpoint.
    /// </summary>
    public class GozargahSiteWalletDeductData
    {
        /// <summary>
        /// Telegram id whose website wallet was debited.
        /// </summary>
        [JsonProperty("telegram_id")]
        public string TelegramId { get; set; }

        /// <summary>
        /// Debited amount in Iranian toman.
        /// </summary>
        [JsonProperty("amount")]
        public long Amount { get; set; }

        /// <summary>
        /// Website wallet balance before debit.
        /// </summary>
        [JsonProperty("previous_wallet")]
        public long PreviousWallet { get; set; }

        /// <summary>
        /// Website wallet balance after debit.
        /// </summary>
        [JsonProperty("current_wallet")]
        public long CurrentWallet { get; set; }
    }

    /// <summary>
    /// HTTP client for the Gozargah website API.
    /// </summary>
    public class GozargahSiteApiClient
    {
        private readonly AppConfig _appConfig;
        private readonly ILogger<GozargahSiteApiClient> _logger;

        /// <summary>
        /// Creates a Gozargah website API client from application configuration.
        /// </summary>
        /// <param name="configuration">Application configuration containing API URL and bearer token.</param>
        /// <param name="logger">Logger used for safe diagnostics without writing the API key.</param>
        public GozargahSiteApiClient(IConfiguration configuration, ILogger<GozargahSiteApiClient> logger)
        {
            _appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
            _logger = logger;
        }

        /// <summary>
        /// Gets whether the API has enough configuration to send requests.
        /// </summary>
        /// <returns><c>true</c> when sync is enabled and both API URL and bearer token are configured.</returns>
        public bool IsConfigured()
        {
            return _appConfig.GozargahSiteSyncEnabled &&
                   !string.IsNullOrWhiteSpace(_appConfig.GozargahSiteApiBaseUrl) &&
                   !string.IsNullOrWhiteSpace(_appConfig.GozargahSiteApiKey);
        }

        /// <summary>
        /// Gets a Gozargah website user by Telegram id.
        /// </summary>
        /// <param name="telegramUserId">Numeric Telegram user id stored on the website.</param>
        /// <param name="cancellationToken">Cancellation token for the HTTP request.</param>
        /// <returns>
        /// API response containing user data when found. The response may have <c>Success=false</c> when
        /// the user does not exist on the website or the API rejects the request.
        /// </returns>
        public Task<GozargahSiteApiResponse<GozargahSiteUserData>> GetUserAsync(
            long telegramUserId,
            CancellationToken cancellationToken = default)
        {
            return SendAsync<GozargahSiteUserData>(
                new
                {
                    action = "get_user",
                    telegram_id = telegramUserId.ToString(CultureInfo.InvariantCulture)
                },
                cancellationToken);
        }

        /// <summary>
        /// Creates a website order for a newly created XUI account.
        /// </summary>
        /// <param name="payload">Normalized website order payload.</param>
        /// <param name="cancellationToken">Cancellation token for the HTTP request.</param>
        /// <returns>Raw API response with the website order id when creation succeeds.</returns>
        public Task<GozargahSiteApiResponse<JToken>> CreateOrderAsync(
            GozargahSiteOrderPayload payload,
            CancellationToken cancellationToken = default)
        {
            return SendAsync<JToken>(BuildOrderBody("create_order", payload), cancellationToken);
        }

        /// <summary>
        /// Updates a website order for renewal, metadata edit, historical upsert, or link replacement.
        /// </summary>
        /// <param name="payload">Normalized website order payload. <see cref="GozargahSiteOrderPayload.Name"/> is the lookup key.</param>
        /// <param name="cancellationToken">Cancellation token for the HTTP request.</param>
        /// <returns>Raw API response with updated fields when available.</returns>
        public Task<GozargahSiteApiResponse<JToken>> UpdateOrderAsync(
            GozargahSiteOrderPayload payload,
            CancellationToken cancellationToken = default)
        {
            return SendAsync<JToken>(BuildOrderBody("update_order", payload), cancellationToken);
        }

        /// <summary>
        /// Deletes a website order by XUI email/name.
        /// </summary>
        /// <param name="name">Current XUI email and website order name.</param>
        /// <param name="cancellationToken">Cancellation token for the HTTP request.</param>
        /// <returns>Raw delete response from the website API.</returns>
        public Task<GozargahSiteApiResponse<JToken>> DeleteOrderAsync(
            string name,
            CancellationToken cancellationToken = default)
        {
            return SendAsync<JToken>(
                new
                {
                    action = "delete_order",
                    name
                },
                cancellationToken);
        }

        /// <summary>
        /// Deducts an amount from the user's website wallet after the bot has already confirmed XUI success.
        /// </summary>
        /// <param name="telegramUserId">Telegram id of the website wallet owner.</param>
        /// <param name="amountToman">Debit amount in Iranian toman. Must be greater than zero.</param>
        /// <param name="cancellationToken">Cancellation token for the HTTP request.</param>
        /// <returns>Wallet balances returned by the website API.</returns>
        /// <remarks>
        /// The website endpoint itself can make the wallet negative, so callers must run <see cref="GetUserAsync"/>
        /// first and compare wallet balance before calling this method.
        /// </remarks>
        public Task<GozargahSiteApiResponse<GozargahSiteWalletDeductData>> DeductWalletAsync(
            long telegramUserId,
            long amountToman,
            CancellationToken cancellationToken = default)
        {
            return SendAsync<GozargahSiteWalletDeductData>(
                new
                {
                    action = "deduct_wallet",
                    telegram_id = telegramUserId.ToString(CultureInfo.InvariantCulture),
                    amount = amountToman
                },
                cancellationToken);
        }

        /// <summary>
        /// Sends one JSON action request to the Gozargah website API and deserializes the standard response wrapper.
        /// </summary>
        /// <typeparam name="T">Expected response data type for the requested action.</typeparam>
        /// <param name="body">Request body containing the <c>action</c> field and action-specific arguments.</param>
        /// <param name="cancellationToken">Cancellation token for the outbound HTTP request.</param>
        /// <returns>
        /// Parsed API response. HTTP errors are converted to unsuccessful responses so callers can persist retry state.
        /// Expected <c>get_user</c> misses are returned without warning logs because most bot users do not have a
        /// Gozargah website account.
        /// </returns>
        /// <remarks>
        /// The API key is sent only in the Authorization header and is never written to logs. Configuration errors throw
        /// because callers should not enqueue website traffic when the API endpoint or key is missing.
        ///
        /// Logging rule:
        /// A missing website user during wallet-button eligibility checks is a normal business outcome, not an
        /// operational API failure. Other HTTP failures still emit warnings so sync, wallet debit, and order lifecycle
        /// problems remain visible to operators.
        /// </remarks>
        private async Task<GozargahSiteApiResponse<T>> SendAsync<T>(object body, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(_appConfig.GozargahSiteApiBaseUrl))
                throw new InvalidOperationException("GozargahSiteApiBaseUrl is not configured.");
            if (string.IsNullOrWhiteSpace(_appConfig.GozargahSiteApiKey))
                throw new InvalidOperationException("GozargahSiteApiKey is not configured.");

            var action = GetActionName(body);
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(HttpMethod.Post, _appConfig.GozargahSiteApiBaseUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _appConfig.GozargahSiteApiKey);
            request.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(request, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (!IsExpectedMissingUserResponse(action, response.StatusCode, responseText))
                {
                    _logger.LogWarning(
                        "Gozargah site API returned HTTP {StatusCode}. Body={Body}",
                        (int)response.StatusCode,
                        responseText);
                }

                return new GozargahSiteApiResponse<T>
                {
                    Success = false,
                    Message = $"HTTP {(int)response.StatusCode}: {responseText}"
                };
            }

            return JsonConvert.DeserializeObject<GozargahSiteApiResponse<T>>(responseText) ??
                   new GozargahSiteApiResponse<T>
                   {
                       Success = false,
                       Message = "Gozargah site API returned an empty response."
                   };
        }

        /// <summary>
        /// Reads the Gozargah API action name from an anonymous request body or dictionary body.
        /// </summary>
        /// <param name="body">
        /// Request body passed to <see cref="SendAsync{T}"/>. It should contain an <c>action</c> property, but may be
        /// null or malformed when a caller is incorrectly wired.
        /// </param>
        /// <returns>
        /// The action name as sent to the website API, or an empty string when it cannot be resolved. The value is only
        /// used for logging decisions and is never sent to users.
        /// </returns>
        /// <remarks>
        /// The client accepts both anonymous objects and dictionaries because order payloads are normalized through
        /// <see cref="BuildOrderBody"/> while user and wallet calls are anonymous objects.
        /// </remarks>
        private static string GetActionName(object body)
        {
            if (body == null)
                return string.Empty;

            if (body is IDictionary<string, object> dictionary &&
                dictionary.TryGetValue("action", out var dictionaryAction))
                return dictionaryAction?.ToString() ?? string.Empty;

            var action = JObject.FromObject(body)["action"];
            return action?.ToString() ?? string.Empty;
        }

        /// <summary>
        /// Detects the normal "site user does not exist" response from the Gozargah <c>get_user</c> endpoint.
        /// </summary>
        /// <param name="action">Action name extracted from the request body.</param>
        /// <param name="statusCode">HTTP status code returned by the website API.</param>
        /// <param name="responseText">Raw response body returned by the website API.</param>
        /// <returns>
        /// <c>true</c> when the response is the expected missing-user result for <c>get_user</c>; otherwise
        /// <c>false</c>, meaning the caller should keep normal warning logs.
        /// </returns>
        /// <remarks>
        /// Wallet eligibility checks run before showing the Gozargah website wallet button. Most Telegram users are not
        /// registered on the website, so logging every 404 would flood the private logger channel without indicating a
        /// broken integration.
        /// </remarks>
        private static bool IsExpectedMissingUserResponse(string action, HttpStatusCode statusCode, string responseText)
        {
            return string.Equals(action, "get_user", StringComparison.OrdinalIgnoreCase) &&
                   statusCode == HttpStatusCode.NotFound &&
                   responseText?.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Converts a normalized order payload into the action-shaped dictionary required by the website API.
        /// </summary>
        /// <param name="action">Website API action name such as <c>create_order</c> or <c>update_order</c>.</param>
        /// <param name="payload">Normalized order payload. Null-valued properties are omitted from the request.</param>
        /// <returns>A case-sensitive dictionary that can be serialized directly as the JSON request body.</returns>
        /// <remarks>
        /// The website API treats missing and null fields differently for updates, so null values are intentionally
        /// removed before sending the request.
        /// </remarks>
        private static IDictionary<string, object> BuildOrderBody(string action, GozargahSiteOrderPayload payload)
        {
            var json = JObject.FromObject(payload ?? new GozargahSiteOrderPayload());
            json["action"] = action;
            var body = json.Properties()
                .Where(property => property.Value.Type != JTokenType.Null)
                .ToDictionary(
                    property => property.Name,
                    property => ((JValue)property.Value).Value ?? property.Value.ToString(),
                    StringComparer.Ordinal);
            return body;
        }
    }

    /// <summary>
    /// Builds and sends Gozargah website sync events for XUI v3 account lifecycle changes.
    /// </summary>
    public class GozargahSiteSyncService
    {
        private const int NationalPlanId = 1;
        private const int NormalPlanId = 2;
        private const int UnlimitedPlanId = 5;
        private static readonly TimeSpan OptionalWebsiteLookupTimeout = TimeSpan.FromSeconds(4);
        private readonly UserDbContext _userDbContext;
        private readonly CredentialsDbContext _credentialsDbContext;
        private readonly GozargahSiteApiClient _apiClient;
        private readonly AppConfig _appConfig;
        private readonly ILogger<GozargahSiteSyncService> _logger;

        /// <summary>
        /// Creates the sync service that owns mapping, outbox creation, immediate send, and site-wallet debits.
        /// </summary>
        /// <param name="userDbContext">users.db context that stores sync outbox rows.</param>
        /// <param name="credentialsDbContext">credentials.db context used only for reading bot-side block status and balances.</param>
        /// <param name="apiClient">Gozargah website API client.</param>
        /// <param name="configuration">Application configuration containing sync flags.</param>
        /// <param name="logger">Logger used for diagnostics.</param>
        public GozargahSiteSyncService(
            UserDbContext userDbContext,
            CredentialsDbContext credentialsDbContext,
            GozargahSiteApiClient apiClient,
            IConfiguration configuration,
            ILogger<GozargahSiteSyncService> logger)
        {
            _userDbContext = userDbContext;
            _credentialsDbContext = credentialsDbContext;
            _apiClient = apiClient;
            _appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
            _logger = logger;
        }

        /// <summary>
        /// Checks whether a user can use the Gozargah site wallet for a bot purchase or renewal.
        /// </summary>
        /// <param name="telegramUserId">Telegram id of the wallet owner on the website.</param>
        /// <param name="amountToman">Required payment amount in Iranian toman.</param>
        /// <param name="cancellationToken">Cancellation token for database and API operations.</param>
        /// <returns>
        /// Eligibility result. <c>CanUse</c> is true only when config is enabled, the bot user is not blocked,
        /// the site user exists, the site user is not banned, and the website wallet balance covers the amount.
        /// </returns>
        public async Task<GozargahSiteWalletEligibility> CheckSiteWalletEligibilityAsync(
            long telegramUserId,
            long amountToman,
            CancellationToken cancellationToken = default)
        {
            if (!_appConfig.GozargahSiteWalletPaymentsEnabled || !_apiClient.IsConfigured())
                return GozargahSiteWalletEligibility.Disabled();

            var botUser = await _credentialsDbContext.GetUserStatusWithId(telegramUserId);
            if (botUser?.IsBlocked == true)
                return GozargahSiteWalletEligibility.Blocked("کاربر در ربات مسدود است.");

            GozargahSiteApiResponse<GozargahSiteUserData> siteUser;
            try
            {
                using var lookupTimeout = CreateOptionalWebsiteLookupCancellation(cancellationToken);
                siteUser = await _apiClient.GetUserAsync(telegramUserId, lookupTimeout.Token);
            }
            catch (Exception ex) when (IsTransientWebsiteLookupFailure(ex, cancellationToken))
            {
                _logger.LogWarning(
                    "Gozargah site wallet eligibility lookup failed transiently. telegramUserId={TelegramUserId}, amountToman={AmountToman}, error={ErrorMessage}",
                    telegramUserId,
                    amountToman,
                    ex.Message);
                return GozargahSiteWalletEligibility.Unavailable("در حال حاضر اتصال به کیف پول سایت گذرگاه برقرار نشد.");
            }

            if (!siteUser.Success || siteUser.Data == null)
                return GozargahSiteWalletEligibility.Unavailable(siteUser.Message ?? "کاربر در سایت گذرگاه پیدا نشد.");
            if (siteUser.Data.IsBanned)
                return GozargahSiteWalletEligibility.Blocked("کاربر در سایت گذرگاه مسدود است.");
            if (siteUser.Data.Wallet < amountToman)
                return GozargahSiteWalletEligibility.Insufficient(siteUser.Data.Wallet);

            return GozargahSiteWalletEligibility.Available(siteUser.Data);
        }

        /// <summary>
        /// Promotes a bot user to colleague pricing when the same Telegram id exists on the Gozargah website.
        /// </summary>
        /// <param name="credUser">
        /// Shared credentials profile for the Telegram user currently using an owned bot. The instance is updated in
        /// memory when the database role changes so later price calculations in the same update use colleague prices.
        /// </param>
        /// <param name="cancellationToken">Cancellation token for the website lookup and credentials database update.</param>
        /// <returns>
        /// <c>true</c> when the website user exists, is not banned, and the local credentials profile is now marked as
        /// a colleague; otherwise <c>false</c>. A missing website user is a normal result and is not logged as a warning.
        /// </returns>
        /// <remarks>
        /// This method is used only by owned-bot customer flows before tariff, purchase, or renewal pricing is
        /// calculated. Tenant storefront customers must not call it because tenant sales use the owner storefront
        /// pricing model rather than direct colleague pricing for the buyer.
        /// </remarks>
        public async Task<bool> PromoteToColleagueIfConnectedSiteUserAsync(
            CredUser credUser,
            CancellationToken cancellationToken = default)
        {
            if (credUser == null || credUser.TelegramUserId <= 0)
                return false;
            if (!_appConfig.GozargahSiteWalletPaymentsEnabled || !_apiClient.IsConfigured())
                return credUser.IsColleague;

            GozargahSiteApiResponse<GozargahSiteUserData> siteUser;
            try
            {
                using var lookupTimeout = CreateOptionalWebsiteLookupCancellation(cancellationToken);
                siteUser = await _apiClient.GetUserAsync(credUser.TelegramUserId, lookupTimeout.Token);
            }
            catch (Exception ex) when (IsTransientWebsiteLookupFailure(ex, cancellationToken))
            {
                _logger.LogWarning(
                    "Gozargah colleague role lookup failed transiently; keeping current bot role. telegramUserId={TelegramUserId}, isColleague={IsColleague}, error={ErrorMessage}",
                    credUser.TelegramUserId,
                    credUser.IsColleague,
                    ex.Message);
                return credUser.IsColleague;
            }

            if (!siteUser.Success || siteUser.Data == null || siteUser.Data.IsBanned)
                return false;

            if (!credUser.IsColleague)
            {
                var changed = await _credentialsDbContext.PromotOrDemote(credUser.TelegramUserId, true);
                if (changed)
                    credUser.IsColleague = true;
            }

            return credUser.IsColleague;
        }

        /// <summary>
        /// Detects temporary website lookup failures that must not block owned-bot purchase, tariff, or renewal flows.
        /// </summary>
        /// <param name="exception">Exception thrown while checking the Gozargah website user or wallet state.</param>
        /// <param name="cancellationToken">
        /// Cancellation token for the active Telegram update. A cancelled shutdown token is not treated as a recoverable
        /// website outage.
        /// </param>
        /// <returns>
        /// <c>true</c> for timeout, HTTP transport, DNS/socket, and temporary cancellation failures; otherwise
        /// <c>false</c>.
        /// </returns>
        /// <remarks>
        /// The website lookup is an optional pricing/wallet enrichment step for owned bots. If the website is slow, the
        /// bot must keep serving the user with the locally known role instead of showing the generic panel timeout
        /// message before the user can even pick a plan.
        /// </remarks>
        private static bool IsTransientWebsiteLookupFailure(Exception exception, CancellationToken cancellationToken)
        {
            if (exception == null || cancellationToken.IsCancellationRequested)
                return false;

            return exception is TimeoutException or TaskCanceledException or HttpRequestException;
        }

        /// <summary>
        /// Creates a short-lived cancellation scope for optional Gozargah website user lookups.
        /// </summary>
        /// <param name="outerCancellationToken">
        /// Cancellation token from the Telegram update or background operation. If it is cancelled, the linked lookup is
        /// cancelled immediately as part of normal shutdown or request cancellation.
        /// </param>
        /// <returns>
        /// A linked cancellation token source that cancels after a short timeout suitable for optional pricing and
        /// wallet-button enrichment calls.
        /// </returns>
        /// <remarks>
        /// Website user lookup is not the source of truth for XUI account creation. Keeping this timeout short prevents
        /// a slow website API from delaying owned-bot tariff and purchase menus for every Telegram user.
        /// </remarks>
        private static CancellationTokenSource CreateOptionalWebsiteLookupCancellation(CancellationToken outerCancellationToken)
        {
            var source = CancellationTokenSource.CreateLinkedTokenSource(outerCancellationToken);
            source.CancelAfter(OptionalWebsiteLookupTimeout);
            return source;
        }

        /// <summary>
        /// Deducts the website wallet after XUI v3 account creation or renewal has already succeeded.
        /// </summary>
        /// <param name="telegramUserId">Telegram id of the website wallet owner.</param>
        /// <param name="amountToman">Debit amount in Iranian toman.</param>
        /// <param name="referenceType">Audit reference type, such as <c>xui-v3-bulk</c> or <c>xui-v3-client</c>.</param>
        /// <param name="referenceId">Audit reference id, such as bulk order id or account email.</param>
        /// <param name="description">Human-readable audit description used only for diagnostics.</param>
        /// <param name="cancellationToken">Cancellation token for API and database precheck operations.</param>
        /// <returns>Debit result including website before/after balances.</returns>
        /// <remarks>
        /// This method intentionally validates the wallet again before calling <c>deduct_wallet</c> because the
        /// website endpoint can make balances negative. A successful debit mutates only the Gozargah website wallet;
        /// it must not create a bot-wallet debit or change <c>credentials.db</c>, otherwise users see a double charge.
        /// </remarks>
        public async Task<GozargahSiteWalletDebitResult> DeductSiteWalletAfterPanelSuccessAsync(
            long telegramUserId,
            long amountToman,
            string referenceType,
            string referenceId,
            string description,
            CancellationToken cancellationToken = default)
        {
            var eligibility = await CheckSiteWalletEligibilityAsync(telegramUserId, amountToman, cancellationToken);
            if (!eligibility.CanUse)
                return GozargahSiteWalletDebitResult.Failed(eligibility.Message);

            var response = await _apiClient.DeductWalletAsync(telegramUserId, amountToman, cancellationToken);
            if (!response.Success || response.Data == null)
                return GozargahSiteWalletDebitResult.Failed(response.Message ?? "کسر کیف پول سایت ناموفق بود.");

            _logger.LogInformation(
                "Gozargah site wallet debited without bot-wallet debit. telegramUserId={TelegramUserId}, amountToman={AmountToman}, before={BeforeWallet}, after={AfterWallet}, referenceType={ReferenceType}, referenceId={ReferenceId}, description={Description}",
                telegramUserId,
                amountToman,
                response.Data.PreviousWallet,
                response.Data.CurrentWallet,
                referenceType,
                referenceId,
                description);

            return GozargahSiteWalletDebitResult.Applied(response.Data.PreviousWallet, response.Data.CurrentWallet);
        }

        /// <summary>
        /// Queues and immediately attempts to send a create-order event for a newly created XUI account.
        /// </summary>
        /// <param name="siteOwnerTelegramUserId">Telegram id of the website user that should own the order.</param>
        /// <param name="buyerTelegramUserId">Actual Telegram buyer. This differs from owner for tenant sales.</param>
        /// <param name="created">XUI v3 creation result.</param>
        /// <param name="trackingCode">Local operation or order id used for audit.</param>
        /// <param name="tenantBotId">Tenant bot id when the event belongs to a tenant storefront.</param>
        /// <param name="cancellationToken">Cancellation token for database and API work.</param>
        /// <returns>The outbox event row, or null when sync is disabled or payload cannot be built.</returns>
        public async Task<GozargahSiteSyncEvent> QueueCreateAsync(
            long siteOwnerTelegramUserId,
            long buyerTelegramUserId,
            XuiV3AccountCreationResult created,
            string trackingCode,
            string tenantBotId = null,
            CancellationToken cancellationToken = default)
        {
            if (!_appConfig.GozargahSiteRealtimeCreateSyncEnabled)
                return null;

            var payload = await BuildPayloadAsync(
                siteOwnerTelegramUserId,
                buyerTelegramUserId,
                created?.Email,
                null,
                ResolveSyncUuid(created?.Uuid, created?.ConfigLink, created?.SubLink),
                created?.SubId,
                created?.SubLink,
                created?.Comment,
                created?.TrafficBytes ?? 0,
                created?.TrafficGb ?? 0,
                created?.DurationDays ?? 0,
                trackingCode,
                cancellationToken);

            return await QueueAndSendAsync(
                GozargahSiteSyncOperations.Create,
                siteOwnerTelegramUserId,
                buyerTelegramUserId,
                created?.Email,
                null,
                ResolveSyncUuid(created?.Uuid, created?.ConfigLink, created?.SubLink),
                created?.SubId,
                created?.SubLink,
                payload,
                tenantBotId,
                cancellationToken);
        }

        /// <summary>
        /// Queues and sends an update-order event after renewal or metadata edit succeeds on XUI.
        /// </summary>
        /// <param name="siteOwnerTelegramUserId">Telegram id of the website user that owns the order.</param>
        /// <param name="buyerTelegramUserId">Actual Telegram buyer or actor.</param>
        /// <param name="client">Updated XUI client.</param>
        /// <param name="serverInfo">Panel descriptor used to build subscription links.</param>
        /// <param name="trackingCode">Local operation id for audit.</param>
        /// <param name="tenantBotId">Tenant bot id when applicable.</param>
        /// <param name="cancellationToken">Cancellation token for database and API work.</param>
        /// <returns>The outbox event row, or null when update sync is disabled.</returns>
        public async Task<GozargahSiteSyncEvent> QueueUpdateAsync(
            long siteOwnerTelegramUserId,
            long buyerTelegramUserId,
            XuiV3Client client,
            ServerInfo serverInfo,
            string trackingCode,
            string tenantBotId = null,
            CancellationToken cancellationToken = default)
        {
            if (!_appConfig.GozargahSiteRealtimeUpdateSyncEnabled)
                return null;

            var subId = string.IsNullOrWhiteSpace(client?.SubId) ? client?.Email : client.SubId;
            var payload = await BuildPayloadAsync(
                siteOwnerTelegramUserId,
                buyerTelegramUserId,
                client?.Email,
                null,
                client?.Uuid,
                subId,
                ApiServicev3.BuildSubscriptionLink(serverInfo, subId),
                client?.Comment,
                ReadTotalBytes(client),
                0,
                CalculateDurationDays(client),
                trackingCode,
                cancellationToken);

            return await QueueAndSendAsync(
                GozargahSiteSyncOperations.Update,
                siteOwnerTelegramUserId,
                buyerTelegramUserId,
                client?.Email,
                null,
                client?.Uuid,
                subId,
                payload?.Sub,
                payload,
                tenantBotId,
                cancellationToken);
        }

        /// <summary>
        /// Queues and sends a rename/update event after an XUI account link replacement succeeds.
        /// </summary>
        /// <param name="siteOwnerTelegramUserId">Telegram id of the website user that owns the order.</param>
        /// <param name="buyerTelegramUserId">Actual Telegram actor or tenant buyer.</param>
        /// <param name="oldEmail">Previous XUI email used as the website lookup key.</param>
        /// <param name="client">Updated XUI client with the new email, UUID, and subscription id.</param>
        /// <param name="serverInfo">Panel descriptor used to build the new subscription link.</param>
        /// <param name="trackingCode">Local operation id for audit.</param>
        /// <param name="tenantBotId">Tenant bot id when applicable.</param>
        /// <param name="cancellationToken">Cancellation token for database and API work.</param>
        /// <returns>The outbox event row, or null when update sync is disabled.</returns>
        public async Task<GozargahSiteSyncEvent> QueueRenameAsync(
            long siteOwnerTelegramUserId,
            long buyerTelegramUserId,
            string oldEmail,
            XuiV3Client client,
            ServerInfo serverInfo,
            string trackingCode,
            string tenantBotId = null,
            CancellationToken cancellationToken = default)
        {
            if (!_appConfig.GozargahSiteRealtimeUpdateSyncEnabled)
                return null;

            var subId = string.IsNullOrWhiteSpace(client?.SubId) ? client?.Email : client.SubId;
            var payload = await BuildPayloadAsync(
                siteOwnerTelegramUserId,
                buyerTelegramUserId,
                oldEmail,
                client?.Email,
                client?.Uuid,
                subId,
                ApiServicev3.BuildSubscriptionLink(serverInfo, subId),
                client?.Comment,
                ReadTotalBytes(client),
                0,
                CalculateDurationDays(client),
                trackingCode,
                cancellationToken);

            return await QueueAndSendAsync(
                GozargahSiteSyncOperations.Rename,
                siteOwnerTelegramUserId,
                buyerTelegramUserId,
                client?.Email,
                oldEmail,
                client?.Uuid,
                subId,
                payload?.Sub,
                payload,
                tenantBotId,
                cancellationToken);
        }

        /// <summary>
        /// Queues and sends a delete-order event after XUI deletion succeeds.
        /// </summary>
        /// <param name="siteOwnerTelegramUserId">Telegram id of the website user that owns the order.</param>
        /// <param name="buyerTelegramUserId">Actual buyer or actor Telegram id.</param>
        /// <param name="client">Deleted XUI client as it existed before deletion.</param>
        /// <param name="trackingCode">Local operation id for audit.</param>
        /// <param name="tenantBotId">Tenant bot id when applicable.</param>
        /// <param name="cancellationToken">Cancellation token for database and API work.</param>
        /// <returns>The outbox event row, or null when delete sync is disabled.</returns>
        public async Task<GozargahSiteSyncEvent> QueueDeleteAsync(
            long siteOwnerTelegramUserId,
            long buyerTelegramUserId,
            XuiV3Client client,
            string trackingCode,
            string tenantBotId = null,
            CancellationToken cancellationToken = default)
        {
            if (!_appConfig.GozargahSiteRealtimeDeleteSyncEnabled)
                return null;

            var payload = new GozargahSiteOrderPayload
            {
                Name = client?.Email,
                TrackingCode = trackingCode
            };

            return await QueueAndSendAsync(
                GozargahSiteSyncOperations.Delete,
                siteOwnerTelegramUserId,
                buyerTelegramUserId,
                client?.Email,
                null,
                client?.Uuid,
                client?.SubId,
                null,
                payload,
                tenantBotId,
                cancellationToken);
        }

        /// <summary>
        /// Sends one pending outbox row to the website API.
        /// </summary>
        /// <param name="syncEvent">Tracked outbox row to process.</param>
        /// <param name="cancellationToken">Cancellation token for API and database work.</param>
        /// <returns><c>true</c> when the event reached a terminal succeeded or skipped state.</returns>
        public async Task<bool> TrySendEventAsync(GozargahSiteSyncEvent syncEvent, CancellationToken cancellationToken = default)
        {
            if (syncEvent == null)
                return false;

            if (!_apiClient.IsConfigured())
            {
                MarkSkipped(syncEvent, "Gozargah site sync is disabled or not configured.");
                await _userDbContext.SaveChangesAsync(cancellationToken);
                return true;
            }

            try
            {
                if (syncEvent.TelegramUserId <= 0)
                {
                    MarkSkipped(syncEvent, "Telegram user id is missing.");
                    await _userDbContext.SaveChangesAsync(cancellationToken);
                    return true;
                }

                var siteUser = await _apiClient.GetUserAsync(syncEvent.TelegramUserId, cancellationToken);
                if (!siteUser.Success || siteUser.Data == null)
                {
                    MarkSkipped(syncEvent, siteUser.Message ?? "Gozargah site user was not found.");
                    await _userDbContext.SaveChangesAsync(cancellationToken);
                    return true;
                }

                if (siteUser.Data.IsBanned)
                {
                    MarkSkipped(syncEvent, "Gozargah site user is banned.");
                    await _userDbContext.SaveChangesAsync(cancellationToken);
                    return true;
                }

                var payload = JsonConvert.DeserializeObject<GozargahSiteOrderPayload>(syncEvent.RequestJson ?? "{}") ??
                              new GozargahSiteOrderPayload();
                payload.Username = siteUser.Data.Username;

                if (RequiresUuid(syncEvent.Operation) && string.IsNullOrWhiteSpace(payload.Uuid))
                {
                    MarkSkipped(syncEvent, "XUI UUID is missing. Re-run the super-admin sync so the account is read fresh from the 3x-ui panel.");
                    await _userDbContext.SaveChangesAsync(cancellationToken);
                    return true;
                }

                GozargahSiteApiResponse<JToken> response;
                if (syncEvent.Operation == GozargahSiteSyncOperations.Delete)
                {
                    response = await _apiClient.DeleteOrderAsync(payload.Name ?? syncEvent.Email, cancellationToken);
                }
                else if (syncEvent.Operation == GozargahSiteSyncOperations.Create)
                {
                    response = await _apiClient.CreateOrderAsync(payload, cancellationToken);
                }
                else
                {
                    response = await _apiClient.UpdateOrderAsync(payload, cancellationToken);
                    if (!response.Success && LooksLikeMissingOrder(response.Message))
                        response = await _apiClient.CreateOrderAsync(payload, cancellationToken);
                }

                syncEvent.ResponseJson = JsonConvert.SerializeObject(response);
                syncEvent.RetryCount++;
                syncEvent.UpdatedAtUtc = DateTime.UtcNow;
                if (response.Success)
                {
                    syncEvent.Status = GozargahSiteSyncStatuses.Succeeded;
                    syncEvent.SiteOrderId = response.Id?.ToString(CultureInfo.InvariantCulture) ?? syncEvent.SiteOrderId;
                    syncEvent.SucceededAtUtc = DateTime.UtcNow;
                    syncEvent.LastError = null;
                    await _userDbContext.SaveChangesAsync(cancellationToken);
                    return true;
                }

                if (IsPermanentValidationFailure(response.Message))
                    MarkSkipped(syncEvent, response.Message ?? "Permanent validation failure from Gozargah site API.");
                else
                {
                    syncEvent.Status = GozargahSiteSyncStatuses.Failed;
                    syncEvent.LastError = response.Message;
                }
                await _userDbContext.SaveChangesAsync(cancellationToken);
                return false;
            }
            catch (Exception ex)
            {
                syncEvent.Status = GozargahSiteSyncStatuses.Failed;
                syncEvent.RetryCount++;
                syncEvent.LastError = ex.Message;
                syncEvent.UpdatedAtUtc = DateTime.UtcNow;
                await _userDbContext.SaveChangesAsync(cancellationToken);
                _logger.LogWarning(ex, "Gozargah site sync event failed. eventId={EventId}", syncEvent.Id);
                return false;
            }
        }

        /// <summary>
        /// Persists one sync outbox event and immediately attempts to send it to the website API.
        /// </summary>
        /// <param name="operation">Lifecycle operation stored in the outbox row.</param>
        /// <param name="siteOwnerTelegramUserId">Telegram id of the Gozargah website user that owns the order.</param>
        /// <param name="buyerTelegramUserId">Telegram id of the actual buyer; different from owner for tenant storefronts.</param>
        /// <param name="email">Current XUI email/name for the account.</param>
        /// <param name="previousEmail">Previous XUI email when the operation is a rename/link change.</param>
        /// <param name="uuid">Current XUI UUID for idempotency and diagnostics.</param>
        /// <param name="subId">Current XUI subscription id for idempotency and diagnostics.</param>
        /// <param name="subLink">Current subscription link sent to the website when available.</param>
        /// <param name="payload">Website order payload serialized into the outbox row.</param>
        /// <param name="tenantBotId">Tenant bot id when the event belongs to a colleague storefront; otherwise null.</param>
        /// <param name="cancellationToken">Cancellation token for users.db and API work.</param>
        /// <returns>
        /// Existing succeeded event for the same idempotency key, the newly created event, or null when sync is disabled.
        /// </returns>
        /// <remarks>
        /// The database row is written before the first API attempt. This preserves the operation for retry if the
        /// website API is unavailable after a successful 3x-ui operation.
        /// </remarks>
        private async Task<GozargahSiteSyncEvent> QueueAndSendAsync(
            string operation,
            long siteOwnerTelegramUserId,
            long buyerTelegramUserId,
            string email,
            string previousEmail,
            string uuid,
            string subId,
            string subLink,
            GozargahSiteOrderPayload payload,
            string tenantBotId,
            CancellationToken cancellationToken)
        {
            if (!_appConfig.GozargahSiteSyncEnabled || payload == null)
                return null;

            var existing = await _userDbContext.GozargahSiteSyncEvents.FirstOrDefaultAsync(
                x => x.Operation == operation &&
                     x.Email == email &&
                     x.PreviousEmail == previousEmail &&
                     x.Uuid == uuid &&
                     x.SubId == subId &&
                     x.Status == GozargahSiteSyncStatuses.Succeeded,
                cancellationToken);
            if (existing != null)
                return existing;

            var syncEvent = new GozargahSiteSyncEvent
            {
                BotId = BotContextAccessor.CurrentBotId,
                TenantBotId = tenantBotId,
                TelegramUserId = siteOwnerTelegramUserId,
                OwnerTelegramUserId = tenantBotId == null ? null : siteOwnerTelegramUserId,
                BuyerTelegramUserId = buyerTelegramUserId <= 0 || buyerTelegramUserId == siteOwnerTelegramUserId ? null : buyerTelegramUserId,
                Email = email,
                PreviousEmail = previousEmail,
                Uuid = uuid,
                SubId = subId,
                SubLink = subLink,
                Operation = operation,
                RequestJson = JsonConvert.SerializeObject(payload),
                Status = GozargahSiteSyncStatuses.Pending,
                CreatedAtUtc = DateTime.UtcNow
            };
            _userDbContext.GozargahSiteSyncEvents.Add(syncEvent);
            await _userDbContext.SaveChangesAsync(cancellationToken);
            await TrySendEventAsync(syncEvent, cancellationToken);
            return syncEvent;
        }

        /// <summary>
        /// Resolves the XUI UUID that should be sent to the Gozargah website sync API.
        /// </summary>
        /// <param name="uuid">
        /// UUID already known from the 3x-ui client payload or panel client row. This value is preferred when present.
        /// </param>
        /// <param name="configLink">
        /// Optional direct VLESS or VMess configuration link sent to the Telegram user. It is used as a fallback when
        /// older creation results did not persist the UUID separately.
        /// </param>
        /// <param name="subLink">
        /// Optional subscription link. It is not treated as a UUID because its final path segment is normally the
        /// subscription id, not the VLESS or VMess UUID.
        /// </param>
        /// <returns>
        /// UUID string safe to send in the website payload, or <c>null</c> when no UUID can be resolved.
        /// </returns>
        /// <remarks>
        /// New v3 account creation now assigns a UUID before sending the payload to 3x-ui. This fallback protects
        /// existing code paths and older results where the panel accepted a client but the local result had a blank
        /// <c>Uuid</c> field.
        /// </remarks>
        private static string ResolveSyncUuid(string uuid, string configLink, string subLink)
        {
            if (Guid.TryParse(uuid, out var parsed))
                return parsed.ToString();

            if (TryExtractUuidFromConfigLink(configLink, out var linkUuid))
                return linkUuid;

            return null;
        }

        /// <summary>
        /// Extracts a UUID from a direct VLESS or VMess configuration link.
        /// </summary>
        /// <param name="configLink">
        /// Direct configuration link. VLESS links contain the UUID before <c>@</c>; VMess links contain a base64 JSON
        /// document whose <c>id</c> field is the UUID.
        /// </param>
        /// <param name="uuid">Resolved UUID when the method returns <c>true</c>; otherwise <c>null</c>.</param>
        /// <returns><c>true</c> when a valid UUID was found; otherwise <c>false</c>.</returns>
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

        /// <summary>
        /// Determines whether a website sync operation must include an XUI UUID.
        /// </summary>
        /// <param name="operation">Outbox operation key stored on <see cref="GozargahSiteSyncEvent.Operation"/>.</param>
        /// <returns>
        /// <c>true</c> for create, update, and rename events that write account details to the website; otherwise
        /// <c>false</c> for delete events that can be matched by account name.
        /// </returns>
        /// <remarks>
        /// Missing UUID is a permanent payload problem, not a transient network failure. Such rows are skipped so the
        /// retry worker does not keep sending the same invalid event. Super-admin historical sync can recreate a fresh
        /// event after reading the account from 3x-ui again.
        /// </remarks>
        private static bool RequiresUuid(string operation)
        {
            return string.Equals(operation, GozargahSiteSyncOperations.Create, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(operation, GozargahSiteSyncOperations.Update, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(operation, GozargahSiteSyncOperations.Rename, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Detects non-retryable validation errors returned by the Gozargah website API.
        /// </summary>
        /// <param name="message">API failure message or HTTP wrapper message from <see cref="GozargahSiteApiClient"/>.</param>
        /// <returns><c>true</c> when retrying the same payload cannot succeed without rebuilding the event.</returns>
        /// <remarks>
        /// The most important case is HTTP 422 with "Field 'uuid' is required". These events were created from incomplete
        /// local data and must be recreated by a super-admin sync that reads the real account state from the panel.
        /// </remarks>
        private static bool IsPermanentValidationFailure(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;

            return message.Contains("HTTP 422", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("Field 'uuid' is required", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("uuid", StringComparison.OrdinalIgnoreCase) &&
                   message.Contains("required", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Builds the website order payload from XUI metadata, panel values, and the owning Gozargah user.
        /// </summary>
        /// <param name="siteOwnerTelegramUserId">Telegram id used by <c>get_user</c> to resolve the website username.</param>
        /// <param name="buyerTelegramUserId">Telegram id of the actual buyer, included in the website comment for tenant sales.</param>
        /// <param name="name">Current or lookup XUI email/name for the website order.</param>
        /// <param name="newName">Replacement XUI email/name for link-change operations; null for create/update/delete.</param>
        /// <param name="uuid">Current XUI UUID.</param>
        /// <param name="subId">Current XUI subscription id.</param>
        /// <param name="subLink">Current subscription URL.</param>
        /// <param name="comment">XUI JSON comment containing service, plan, price, and ownership metadata.</param>
        /// <param name="trafficBytes">Traffic allowance in bytes when metadata does not provide a GB value.</param>
        /// <param name="fallbackTrafficGb">Traffic allowance in GB when metadata and bytes are incomplete.</param>
        /// <param name="fallbackDurationDays">Duration in days when metadata does not provide a plan duration.</param>
        /// <param name="trackingCode">Local order id or operation id passed to the website for audit.</param>
        /// <param name="cancellationToken">Cancellation token for the website <c>get_user</c> lookup.</param>
        /// <returns>
        /// Normalized payload ready for create/update, or null when the site user does not exist, is banned, or name is empty.
        /// </returns>
        /// <remarks>
        /// Website ownership is resolved before queueing so missing or banned site users are skipped instead of being retried forever.
        /// </remarks>
        private async Task<GozargahSiteOrderPayload> BuildPayloadAsync(
            long siteOwnerTelegramUserId,
            long buyerTelegramUserId,
            string name,
            string newName,
            string uuid,
            string subId,
            string subLink,
            string comment,
            long trafficBytes,
            int fallbackTrafficGb,
            int fallbackDurationDays,
            string trackingCode,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(name))
                return null;

            var siteUser = await _apiClient.GetUserAsync(siteOwnerTelegramUserId, cancellationToken);
            if (!siteUser.Success || siteUser.Data == null || siteUser.Data.IsBanned)
                return null;

            var metadata = TryReadMetadata(comment);
            var map = MapPlan(metadata, fallbackTrafficGb);
            var volumeGb = ResolveVolumeGb(metadata, trafficBytes, fallbackTrafficGb);
            var durationDays = metadata?.DurationDays ?? fallbackDurationDays;
            var priceToman = metadata?.PriceToman ?? 0;
            var buyerText = buyerTelegramUserId > 0 && buyerTelegramUserId != siteOwnerTelegramUserId
                ? $"; buyer={buyerTelegramUserId}"
                : string.Empty;

            return new GozargahSiteOrderPayload
            {
                PlanId = map.PlanId,
                Inbound = map.Inbound,
                ArrangedPlanId = map.ArrangedPlanId,
                Name = name,
                NewName = newName,
                Uuid = uuid,
                Comment = $"Synced from Telegram bot; owner={siteOwnerTelegramUserId}{buyerText}; {comment}",
                Price = priceToman.ToString(CultureInfo.InvariantCulture),
                Volume = volumeGb,
                Date = Math.Max(0, durationDays),
                Username = siteUser.Data.Username,
                TrackingCode = string.IsNullOrWhiteSpace(trackingCode) ? $"bot-sync-{name}" : trackingCode,
                Sub = subLink,
                Trial = metadata?.IsTrial == true ? 1 : 0,
                Bot = 1
            };
        }

        /// <summary>
        /// Maps bot service metadata to the plan and inbound identifiers expected by the website database.
        /// </summary>
        /// <param name="metadata">Parsed XUI JSON comment metadata, or null for older accounts.</param>
        /// <param name="fallbackTrafficGb">Traffic in GB used to map older unlimited accounts without metadata.</param>
        /// <returns>Website plan id, inbound string, and arranged unlimited-plan id.</returns>
        /// <remarks>
        /// The mapping follows the site contract: national is plan 1/inbound 156, normal is plan 2/inbounds
        /// 166,188,189, and unlimited is plan 5 with arranged ids based on fair-usage GB.
        /// </remarks>
        private static GozargahPlanMap MapPlan(XuiV3ClientMetadata metadata, int fallbackTrafficGb)
        {
            if (string.Equals(metadata?.ServiceKey, "national", StringComparison.OrdinalIgnoreCase))
                return new GozargahPlanMap(NationalPlanId, "156", 0);

            if (string.Equals(metadata?.ServiceKey, "unlimited", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(metadata?.ServiceKind, XuiV3ServiceKinds.Unlimited, StringComparison.OrdinalIgnoreCase))
            {
                return new GozargahPlanMap(UnlimitedPlanId, "166, 188, 189", MapUnlimitedArrangedPlan(metadata?.TrafficGb ?? fallbackTrafficGb));
            }

            return new GozargahPlanMap(NormalPlanId, "166, 188, 189", 0);
        }

        /// <summary>
        /// Maps unlimited fair-usage volume to the website arranged plan id.
        /// </summary>
        /// <param name="fairUsageGb">Fair-usage allowance in GB from the bot plan metadata.</param>
        /// <returns>Website arranged plan id: 6 for 100GB, 7 for 170GB, 8 for 230GB, and 9 for larger plans.</returns>
        private static int MapUnlimitedArrangedPlan(int fairUsageGb)
        {
            return fairUsageGb switch
            {
                <= 100 => 6,
                <= 170 => 7,
                <= 230 => 8,
                _ => 9
            };
        }

        /// <summary>
        /// Resolves the website volume field in GB from metadata, bytes, or fallback plan data.
        /// </summary>
        /// <param name="metadata">Parsed XUI metadata, preferred when it contains <c>TrafficGb</c>.</param>
        /// <param name="trafficBytes">Traffic allowance in bytes from the panel or creation result.</param>
        /// <param name="fallbackTrafficGb">Fallback traffic in GB when metadata and bytes are missing.</param>
        /// <returns>Traffic volume in GB, rounded to two decimals when calculated from bytes.</returns>
        private static decimal ResolveVolumeGb(XuiV3ClientMetadata metadata, long trafficBytes, int fallbackTrafficGb)
        {
            if (metadata?.TrafficGb > 0)
                return metadata.TrafficGb;
            if (trafficBytes > 0)
                return Math.Round(trafficBytes / 1024m / 1024m / 1024m, 2);
            return Math.Max(0, fallbackTrafficGb);
        }

        /// <summary>
        /// Reads total traffic bytes from a potentially sparse XUI v3 client object.
        /// </summary>
        /// <param name="client">XUI client returned by the panel; <c>Traffic</c> may be null.</param>
        /// <returns>Total bytes from top-level fields, nested traffic, or raw <c>Extra</c>; otherwise zero.</returns>
        private static long ReadTotalBytes(XuiV3Client client)
        {
            if (client == null)
                return 0;
            if (client.TotalGB > 0)
                return client.TotalGB;
            if (client.Traffic?.TotalGB > 0)
                return client.Traffic.TotalGB;
            if (client.Traffic?.Total > 0)
                return client.Traffic.Total;
            return ReadLongExtra(client, "totalGB");
        }

        /// <summary>
        /// Calculates the remaining or first-use duration in days for website sync.
        /// </summary>
        /// <param name="client">XUI client whose expiry can be positive, zero, or negative first-use milliseconds.</param>
        /// <returns>Duration in whole days rounded up, or zero for expired/unlimited/unknown values.</returns>
        private static int CalculateDurationDays(XuiV3Client client)
        {
            var expiryTime = client?.ExpiryTime ?? client?.Traffic?.ExpiryTime ?? ReadLongExtra(client, "expiryTime");
            if (expiryTime < 0)
                return (int)Math.Ceiling(Math.Abs(expiryTime) / (double)TimeSpan.FromDays(1).TotalMilliseconds);
            if (expiryTime <= 0)
                return 0;
            var remaining = expiryTime - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            return remaining <= 0 ? 0 : (int)Math.Ceiling(remaining / (double)TimeSpan.FromDays(1).TotalMilliseconds);
        }

        /// <summary>
        /// Reads a numeric value from the raw XUI <c>Extra</c> bag.
        /// </summary>
        /// <param name="client">XUI client that may contain raw JSON properties in <c>Extra</c>.</param>
        /// <param name="key">Case-sensitive JSON key to read.</param>
        /// <returns>Parsed long value, or zero when the key is missing or not numeric.</returns>
        private static long ReadLongExtra(XuiV3Client client, string key)
        {
            if (client?.Extra == null || !client.Extra.TryGetValue(key, out var token) || token == null)
                return 0;
            if (token.Type == JTokenType.Integer)
                return token.Value<long>();
            return long.TryParse(token.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : 0;
        }

        /// <summary>
        /// Parses the bot JSON metadata stored in the XUI comment field.
        /// </summary>
        /// <param name="comment">Raw XUI comment text.</param>
        /// <returns>Parsed metadata, or null when the comment is empty or not valid bot JSON.</returns>
        private static XuiV3ClientMetadata TryReadMetadata(string comment)
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

        /// <summary>
        /// Determines whether a failed website update likely means the order does not exist yet.
        /// </summary>
        /// <param name="message">Website API message returned from <c>update_order</c>.</param>
        /// <returns><c>true</c> when create-order fallback should be attempted.</returns>
        /// <remarks>
        /// The site API currently has no separate get-order endpoint, so historical sync relies on this check to
        /// upsert old records by trying update first and create second.
        /// </remarks>
        private static bool LooksLikeMissingOrder(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
                return false;
            return message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("no order", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("پیدا", StringComparison.OrdinalIgnoreCase) ||
                   message.Contains("وجود", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Marks a sync event as skipped with a durable reason.
        /// </summary>
        /// <param name="syncEvent">Tracked outbox event to update.</param>
        /// <param name="reason">Reason the event should no longer be retried.</param>
        private static void MarkSkipped(GozargahSiteSyncEvent syncEvent, string reason)
        {
            syncEvent.Status = GozargahSiteSyncStatuses.Skipped;
            syncEvent.LastError = reason;
            syncEvent.UpdatedAtUtc = DateTime.UtcNow;
        }

        /// <summary>
        /// Internal value object containing website plan mapping fields for one bot service.
        /// </summary>
        /// <param name="PlanId">Website plan id.</param>
        /// <param name="Inbound">Comma-separated inbound ids expected by the website API.</param>
        /// <param name="ArrangedPlanId">Website arranged plan id for unlimited plans; zero for regular plans.</param>
        private readonly record struct GozargahPlanMap(int PlanId, string Inbound, int ArrangedPlanId);
    }

    /// <summary>
    /// Eligibility result for showing and using the Gozargah website wallet payment method.
    /// </summary>
    public class GozargahSiteWalletEligibility
    {
        /// <summary>
        /// Whether the wallet can be used for the requested amount.
        /// </summary>
        public bool CanUse { get; set; }

        /// <summary>
        /// User-facing reason when <see cref="CanUse"/> is false.
        /// </summary>
        public string Message { get; set; }

        /// <summary>
        /// Current website wallet balance in toman when available.
        /// </summary>
        public long WalletToman { get; set; }

        /// <summary>
        /// Website user data returned by <c>get_user</c>.
        /// </summary>
        public GozargahSiteUserData User { get; set; }

        /// <summary>
        /// Creates an available eligibility result.
        /// </summary>
        /// <param name="user">Website user data with enough wallet balance.</param>
        /// <returns>Eligibility result that allows site-wallet payment.</returns>
        public static GozargahSiteWalletEligibility Available(GozargahSiteUserData user)
            => new() { CanUse = true, User = user, WalletToman = user?.Wallet ?? 0 };

        /// <summary>
        /// Creates a disabled eligibility result.
        /// </summary>
        /// <returns>Eligibility result for disabled config or missing API setup.</returns>
        public static GozargahSiteWalletEligibility Disabled()
            => new() { Message = "پرداخت با کیف پول سایت فعال نیست." };

        /// <summary>
        /// Creates a blocked eligibility result.
        /// </summary>
        /// <param name="message">Reason returned to the caller.</param>
        /// <returns>Eligibility result for bot-side or site-side ban.</returns>
        public static GozargahSiteWalletEligibility Blocked(string message)
            => new() { Message = message };

        /// <summary>
        /// Creates an unavailable eligibility result.
        /// </summary>
        /// <param name="message">Reason returned to the caller.</param>
        /// <returns>Eligibility result for missing site user or API failure.</returns>
        public static GozargahSiteWalletEligibility Unavailable(string message)
            => new() { Message = message };

        /// <summary>
        /// Creates an insufficient-balance eligibility result.
        /// </summary>
        /// <param name="walletToman">Current site wallet balance in toman.</param>
        /// <returns>Eligibility result containing the current wallet balance.</returns>
        public static GozargahSiteWalletEligibility Insufficient(long walletToman)
            => new() { WalletToman = walletToman, Message = "موجودی کیف پول سایت کافی نیست." };
    }

    /// <summary>
    /// Result of a Gozargah website wallet debit.
    /// </summary>
    public class GozargahSiteWalletDebitResult
    {
        /// <summary>
        /// Whether the website wallet was debited successfully.
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Website wallet balance before debit.
        /// </summary>
        public long BeforeWallet { get; set; }

        /// <summary>
        /// Website wallet balance after debit.
        /// </summary>
        public long AfterWallet { get; set; }

        /// <summary>
        /// Error message when <see cref="Success"/> is false.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Creates a successful debit result.
        /// </summary>
        /// <param name="beforeWallet">Website wallet before debit.</param>
        /// <param name="afterWallet">Website wallet after debit.</param>
        /// <returns>Successful debit result.</returns>
        public static GozargahSiteWalletDebitResult Applied(long beforeWallet, long afterWallet)
            => new() { Success = true, BeforeWallet = beforeWallet, AfterWallet = afterWallet };

        /// <summary>
        /// Creates a failed debit result.
        /// </summary>
        /// <param name="errorMessage">Reason the debit was not applied.</param>
        /// <returns>Failed debit result.</returns>
        public static GozargahSiteWalletDebitResult Failed(string errorMessage)
            => new() { ErrorMessage = errorMessage };
    }

    /// <summary>
    /// Background worker that retries failed or pending Gozargah site sync events.
    /// </summary>
    public class GozargahSiteSyncRetryService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<GozargahSiteSyncRetryService> _logger;

        /// <summary>
        /// Creates the retry worker.
        /// </summary>
        /// <param name="serviceProvider">Root service provider used to resolve the singleton sync service.</param>
        /// <param name="logger">Logger for retry diagnostics.</param>
        public GozargahSiteSyncRetryService(IServiceProvider serviceProvider, ILogger<GozargahSiteSyncRetryService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        /// <summary>
        /// Periodically retries pending and failed Gozargah sync events.
        /// </summary>
        /// <param name="stoppingToken">Cancellation token triggered when the host is shutting down.</param>
        /// <returns>A task that completes when the background worker stops.</returns>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var db = _serviceProvider.GetRequiredService<UserDbContext>();
                    var syncService = _serviceProvider.GetRequiredService<GozargahSiteSyncService>();
                    var events = await db.GozargahSiteSyncEvents
                        .Where(x => x.Status == GozargahSiteSyncStatuses.Pending || x.Status == GozargahSiteSyncStatuses.Failed)
                        .OrderBy(x => x.CreatedAtUtc)
                        .Take(25)
                        .ToListAsync(stoppingToken);

                    foreach (var syncEvent in events)
                        await syncService.TrySendEventAsync(syncEvent, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Gozargah site sync retry loop failed.");
                }
            }
        }
    }
}
