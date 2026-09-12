using System;
using System.Threading;
using System.Threading.Tasks;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Adminbot.Services
{
    /// <summary>
    /// Closed-vocabulary outcome of one provisional tenant card-to-card provisioning attempt.
    /// </summary>
    /// <remarks>
    /// Only <see cref="Created" /> and <see cref="AlreadyDelivered" /> mean the customer's provisional client exists and
    /// its identity is durably persisted. No value in this enumeration authorizes a wallet debit, a ledger entry, order
    /// fulfillment, or a final-sale notification.
    /// </remarks>
    public enum TenantCardProvisionalProvisioningStatus
    {
        /// <summary>The global provisional-delivery switch is off, so nothing was attempted.</summary>
        Disabled,

        /// <summary>The order is not a tenant card-to-card purchase, or is already financially fulfilled.</summary>
        NotApplicable,

        /// <summary>The provisional client already exists with persisted identity, so no panel mutation was issued.</summary>
        AlreadyDelivered,

        /// <summary>One provisional client was created and its identity was durably persisted.</summary>
        Created,

        /// <summary>A transient or ambiguous panel outcome left the attempt resumable under the same operation key.</summary>
        Retryable,

        /// <summary>The attempt cannot proceed automatically and needs human reconciliation before any further mutation.</summary>
        ManualReview
    }

    /// <summary>
    /// Result of one provisional provisioning attempt, including the persisted provisional identity when it exists.
    /// </summary>
    /// <param name="Status">Closed-vocabulary outcome of this attempt.</param>
    /// <param name="OrderId">Internal <c>users.db</c> id of the tenant order the attempt belongs to.</param>
    /// <param name="PublicOrderId">Public tenant order id, used for the durable operation key and for customer text.</param>
    /// <param name="Email">Provisional panel email once known; <c>null</c> when no client is proven to exist.</param>
    /// <param name="Uuid">Provisional panel UUID once known; <c>null</c> when no client is proven to exist.</param>
    /// <param name="SubId">Provisional subscription id once known, falling back to the email when the panel omits it.</param>
    /// <param name="SubLink">Provisional subscription link built from the configured sub-link base; never a credential.</param>
    /// <param name="TrafficGb">Provisional courtesy allowance in whole GiB, always <c>1</c> for a created client.</param>
    /// <param name="DurationDays">Provisional courtesy lifetime in whole days, always <c>1</c> for a created client.</param>
    /// <param name="ReasonCode">Stable secret-free reason code for a non-created outcome; <c>null</c> on success.</param>
    /// <param name="CreatedNow">
    /// <c>true</c> when this call performed the panel create; <c>false</c> when it observed an already-proven client.
    /// </param>
    public sealed record TenantCardProvisionalProvisioningResult(
        TenantCardProvisionalProvisioningStatus Status,
        int OrderId,
        string PublicOrderId,
        string Email,
        string Uuid,
        string SubId,
        string SubLink,
        int TrafficGb,
        int DurationDays,
        string ReasonCode,
        bool CreatedNow);

    /// <summary>
    /// Creates the tenant card-to-card courtesy client exactly once and persists enough identity to upgrade it in place.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Purpose.</b> A tenant storefront customer who pays by personal card-to-card normally waits for the store owner
    /// to review the receipt. While the receipt is under review the customer receives one temporary panel client so they
    /// can start using the service immediately. The account is a courtesy, not a sale: it is 1 GiB and 1 day, the order
    /// stays <see cref="TenantBotOrder.IsFulfilled" /> <c>false</c>, and no owner wallet debit, tenant ledger row, profit
    /// credit, or final-sale notification is produced here.
    /// </para>
    /// <para>
    /// <b>Exactly-once.</b> Creation goes through
    /// <see cref="XuiV3PurchaseService.CreateAccountWithExplicitLimitsAsync" /> with the durable operation key
    /// <c>tenant-card-provisional-create:{publicOrderId}</c>, which reuses the existing
    /// <see cref="XuiV3CreationOperationStore" /> Reserve / single-POST / Applied-or-Ambiguous boundary. A repeated call
    /// — duplicate Telegram update, customer retry, receipt replacement, process restart — can only recover the original
    /// client by panel read-back; it can never authorize a second POST. The key is deliberately distinct from the normal
    /// purchase key <c>tenant-create:{publicOrderId}</c>.
    /// </para>
    /// <para>
    /// <b>Tenant authorization.</b> The ORIGINAL purchased selection is reconstructed from the order and revalidated
    /// through <see cref="XuiV3PurchaseService.ResolveTenantProvisionalPlacement" />, so an order whose plan was hidden
    /// or disabled for this tenant receives no provisional access. Only the 1 GiB / 1 day policy limits bypass catalog
    /// minimum-traffic and duration-key validation.
    /// </para>
    /// <para>
    /// <b>Scope.</b> Applies only when <see cref="AppConfig.TenantCardProvisionalDeliveryEnabled" /> is <c>true</c>, the
    /// order's provider is <c>tenant_card</c>, and its kind is a purchase. Renewals and every automatic gateway are
    /// excluded, because upgrading a renewal in place would destructively downgrade an existing client.
    /// </para>
    /// </remarks>
    public sealed class TenantCardProvisionalProvisioningService
    {
        /// <summary>Provisional courtesy allowance in whole GiB.</summary>
        /// <remarks>One GiB, expressed in GB so the panel quota is exactly <c>ApiService.ConvertGBToBytes(1)</c>.</remarks>
        public const int ProvisionalTrafficGb = 1;

        /// <summary>Provisional courtesy lifetime in whole days.</summary>
        public const int ProvisionalDurationDays = 1;

        /// <summary>Plan label recorded in the panel comment so the client is recognizable as a courtesy account.</summary>
        public const string ProvisionalPlanKey = "provisional-1gb-1d";

        private readonly UserDbContextFactory _factory;
        private readonly XuiV3PurchaseService _purchaseService;
        private readonly XuiV3CreationOperationStore _creationOperations;
        private readonly IConfiguration _configuration;
        private readonly ILogger<TenantCardProvisionalProvisioningService> _logger;

        /// <summary>Creates the provisioning service over users.db and the shared XUI purchase machinery.</summary>
        /// <param name="factory">Required users.db factory; the service never retains a live context.</param>
        /// <param name="purchaseService">Shared purchase service supplying catalog placement and the exactly-once create.</param>
        /// <param name="creationOperations">
        /// Durable creation-operation store used read-only to decide whether a failed attempt left the panel untouched.
        /// </param>
        /// <param name="configuration">Runtime configuration; read for the global switch only.</param>
        /// <param name="logger">Operational logger. Entries never carry tokens, credentials, or raw panel payloads.</param>
        /// <exception cref="ArgumentNullException">Any required dependency is <c>null</c>.</exception>
        public TenantCardProvisionalProvisioningService(
            UserDbContextFactory factory,
            XuiV3PurchaseService purchaseService,
            XuiV3CreationOperationStore creationOperations,
            IConfiguration configuration,
            ILogger<TenantCardProvisionalProvisioningService> logger)
        {
            _factory = factory ?? throw new ArgumentNullException(nameof(factory));
            _purchaseService = purchaseService ?? throw new ArgumentNullException(nameof(purchaseService));
            _creationOperations = creationOperations ?? throw new ArgumentNullException(nameof(creationOperations));
            _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>Builds the durable provisional creation key for one tenant order.</summary>
        /// <param name="publicOrderId">Public tenant order id; never a database id.</param>
        /// <returns>The stable key in <c>tenant-card-provisional-create:{orderId}</c> form.</returns>
        public static string BuildCreateOperationKey(string publicOrderId)
            => $"tenant-card-provisional-create:{publicOrderId}";

        /// <summary>Determines whether a panel comment's plan key identifies a provisional courtesy account.</summary>
        /// <param name="planKey">Plan key read from a panel client comment, possibly <c>null</c>.</param>
        /// <returns><c>true</c> when the key is the provisional courtesy plan key; otherwise <c>false</c>.</returns>
        /// <remarks>
        /// Used by the expiry and volume reminder scans to skip courtesy accounts. The plan key is written by the
        /// provisional create through <see cref="XuiV3AccountMetadataOptions.PlanKeyOverride" />, and finalization replaces
        /// it with the purchased plan through the same update path, so a finalized account becomes reminder-eligible again
        /// while a provisional one stays excluded.
        /// </remarks>
        public static bool IsProvisionalPlanKey(string planKey)
            => string.Equals(planKey, ProvisionalPlanKey, StringComparison.Ordinal);

        /// <summary>Determines whether a raw panel client comment identifies a provisional courtesy account.</summary>
        /// <param name="comment">Raw comment JSON read from a panel client, possibly <c>null</c> or non-JSON.</param>
        /// <returns><c>true</c> only when the comment parses and carries the provisional plan key.</returns>
        /// <remarks>
        /// Unparseable and empty comments return <c>false</c> so reminders keep working for every ordinary client. This is
        /// a best-effort read of whatever the panel list endpoint reports; see the reminder services for the documented
        /// residual limitation.
        /// </remarks>
        public static bool IsProvisionalClientComment(string comment)
        {
            if (string.IsNullOrWhiteSpace(comment))
                return false;

            try
            {
                var metadata = Newtonsoft.Json.JsonConvert.DeserializeObject<XuiV3ClientMetadata>(comment);
                return IsProvisionalPlanKey(metadata?.PlanKey);
            }
            catch (Newtonsoft.Json.JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// Creates the provisional courtesy client for one tenant card-to-card purchase, or returns the existing one.
        /// </summary>
        /// <param name="customer">
        /// Detached global credentials profile of the tenant customer receiving the courtesy client. Its Telegram user id
        /// becomes the panel owner. <see cref="XuiV3AccountMetadataOptions.SaveUserStatus" /> is disabled for this call so
        /// the courtesy account never overwrites the customer's own conversation state.
        /// </param>
        /// <param name="serverInfo">
        /// Authenticated panel endpoint for the tenant's configured XUI server. Never logged and never taken from a
        /// Telegram payload.
        /// </param>
        /// <param name="tenantBotOrderId">Internal <c>users.db</c> id of the tenant order to provision for.</param>
        /// <param name="cancellationToken">Token cancelling the reservation, the single panel POST, and read-back recovery.</param>
        /// <returns>
        /// A result whose <see cref="TenantCardProvisionalProvisioningResult.Email" /> is populated only when the
        /// provisional client's identity is durably persisted. A caller may send the customer the account details exactly
        /// when <see cref="TenantCardProvisionalProvisioningResult.Status" /> is <c>Created</c> or
        /// <c>AlreadyDelivered</c>.
        /// </returns>
        /// <exception cref="ArgumentNullException"><paramref name="customer" /> or <paramref name="serverInfo" /> is <c>null</c>.</exception>
        /// <exception cref="OperationCanceledException"><paramref name="cancellationToken" /> was cancelled.</exception>
        /// <remarks>
        /// Side effects: at most one panel client is created per order for the lifetime of the database, and the order's
        /// provisional identity columns and state are updated. This method never debits a wallet, writes a ledger row,
        /// marks the order paid or fulfilled, credits profit, or enqueues a final-sale notification.
        /// </remarks>
        /// <example>
        /// <code>
        /// var provisional = await provisioning.ProvisionAsync(customer, panel, order.Id, cancellationToken);
        /// if (provisional.Status is TenantCardProvisionalProvisioningStatus.Created)
        ///     await SendProvisionalDetailsAsync(botClient, chatId, provisional, cancellationToken);
        /// </code>
        /// </example>
        public async Task<TenantCardProvisionalProvisioningResult> ProvisionAsync(
            CredUser customer,
            ServerInfo serverInfo,
            int tenantBotOrderId,
            CancellationToken cancellationToken)
        {
            if (customer == null)
                throw new ArgumentNullException(nameof(customer));
            if (serverInfo == null)
                throw new ArgumentNullException(nameof(serverInfo));

            var order = await LoadOrderAsync(tenantBotOrderId, cancellationToken).ConfigureAwait(false);
            if (order == null)
                return Failure(TenantCardProvisionalProvisioningStatus.NotApplicable, tenantBotOrderId, null, "provisional_order_not_found");

            // The global switch is read live so an operator change takes effect without restarting the process.
            if (!(_configuration.Get<AppConfig>()?.TenantCardProvisionalDeliveryEnabled ?? false))
                return Failure(TenantCardProvisionalProvisioningStatus.Disabled, order.Id, order.OrderId, "provisional_delivery_disabled");

            // Scope guard: purchases funded by the tenant's personal card only. Renewals would downgrade an existing
            // customer client, and every automatic gateway settles through its own verified callback path.
            if (!string.Equals(order.PaymentProvider, "tenant_card", StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(order.OrderKind, TenantBotOrderKinds.Purchase, StringComparison.OrdinalIgnoreCase) ||
                order.IsFulfilled)
            {
                return Failure(TenantCardProvisionalProvisioningStatus.NotApplicable, order.Id, order.OrderId, "provisional_not_applicable");
            }

            switch (order.ProvisionalDeliveryState)
            {
                case TenantCardProvisionalStates.ManualReview:
                    return Failure(TenantCardProvisionalProvisioningStatus.ManualReview, order.Id, order.OrderId, order.ProvisionalErrorCode ?? "provisional_manual_review");
                case TenantCardProvisionalStates.Delivered:
                case TenantCardProvisionalStates.Finalizing:
                case TenantCardProvisionalStates.Finalized:
                case TenantCardProvisionalStates.Revoking:
                case TenantCardProvisionalStates.Revoked:
                    return Existing(order, createdNow: false);
            }

            // A proven identity means the client exists even if the customer notification did not complete. Resuming must
            // never reach the panel create again.
            if (HasProvenIdentity(order))
                return Existing(order, createdNow: false);

            // Durable claim before the first mutation, so a crash between the claim and the POST is resumable rather than
            // invisible. A concurrent caller that loses the claim simply resumes the same operation key.
            await ClaimProvisioningAsync(order.Id, cancellationToken).ConfigureAwait(false);

            XuiV3ProvisionalPlacement placement;
            try
            {
                // Revalidate the ORIGINAL purchased selection against the CURRENT tenant catalog. This is an eligibility
                // check only: the courtesy limits below are policy constants, not catalog plans, so the commercial
                // minimum-traffic and duration-key rules are intentionally not applied to them.
                placement = _purchaseService.ResolveTenantProvisionalPlacement(BuildSelection(order));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
            {
                // Nothing was created, so the order is not permanently blocked: recording the code keeps the attempt
                // visible while allowing a later receipt to retry once the plan is visible again.
                await RecordFailureAsync(order.Id, "provisional_plan_unavailable", cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "Provisional tenant card delivery skipped because the original plan is not currently tenant-visible. tenantBotId={TenantBotId} orderId={OrderId} serviceKey={ServiceKey}",
                    order.TenantBotId, order.OrderId, order.ServiceKey);
                return Failure(TenantCardProvisionalProvisioningStatus.Retryable, order.Id, order.OrderId, "provisional_plan_unavailable");
            }

            var created = await _purchaseService.CreateAccountWithExplicitLimitsAsync(
                customer,
                serverInfo,
                placement,
                selectedCountry: "tenant-card-provisional",
                trafficGb: ProvisionalTrafficGb,
                durationDays: ProvisionalDurationDays,
                cancellationToken: cancellationToken,
                metadataOptions: new XuiV3AccountMetadataOptions
                {
                    OperationKey = BuildCreateOperationKey(order.OrderId),
                    UserComment = $"provisional tenant card courtesy VIA @{order.TenantBotUsername}; Buyer={order.CustomerTelegramUserId}; tenant={order.TenantBotId}",
                    PlanKeyOverride = ProvisionalPlanKey,
                    PlanNameOverride = "Provisional 1GB / 1 day",
                    PriceTomanOverride = 0,
                    CreatedByBotId = order.TenantBotId,
                    LastUpdatedByBotId = order.TenantBotId,
                    CreatedByTelegramUserId = order.CustomerTelegramUserId,
                    LastUpdatedByTelegramUserId = order.OwnerTelegramUserId,
                    LastAction = "tenant-card-provisional",
                    // A courtesy account must never overwrite the customer's permanent conversation state.
                    SaveUserStatus = false
                }).ConfigureAwait(false);

            if (!created.Success || string.IsNullOrWhiteSpace(created.Email))
            {
                // The operation key keeps the attempt resumable by read-back only; a later call can never issue a second
                // POST for this key after a definitive rejection or an ambiguous outcome.
                await RecordFailureAsync(order.Id, "provisional_create_failed", cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "Provisional tenant card create did not produce a verified client. tenantBotId={TenantBotId} orderId={OrderId} serviceKey={ServiceKey}",
                    order.TenantBotId, order.OrderId, order.ServiceKey);
                return Failure(TenantCardProvisionalProvisioningStatus.Retryable, order.Id, order.OrderId, "provisional_create_failed");
            }

            var subId = string.IsNullOrWhiteSpace(created.SubId) ? created.Email : created.SubId;
            await PersistIdentityAsync(order.Id, created.Email, created.Uuid, subId, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Provisional tenant card account created. tenantBotId={TenantBotId} orderId={OrderId} trafficGb={TrafficGb} days={Days}",
                order.TenantBotId, order.OrderId, ProvisionalTrafficGb, ProvisionalDurationDays);

            return new TenantCardProvisionalProvisioningResult(
                TenantCardProvisionalProvisioningStatus.Created,
                order.Id,
                order.OrderId,
                created.Email,
                created.Uuid,
                subId,
                created.SubLink,
                ProvisionalTrafficGb,
                ProvisionalDurationDays,
                null,
                CreatedNow: true);
        }

        /// <summary>
        /// Records that the customer has actually received the provisional account details.
        /// </summary>
        /// <param name="tenantBotOrderId">Internal <c>users.db</c> id of the tenant order.</param>
        /// <param name="cancellationToken">Cancellation token for the short update.</param>
        /// <returns>A task completing after the delivery marker commits.</returns>
        /// <remarks>
        /// The guarded transition only moves an order that is still <c>provisioning</c> to <c>delivered</c>, so a duplicate
        /// notification cannot move a finalized, revoked, or reviewed order backwards. Any other state is left untouched.
        /// </remarks>
        public async Task MarkDeliveredAsync(int tenantBotOrderId, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            await using var db = _factory.CreateDbContext();
            await db.TenantBotOrders
                .Where(x => x.Id == tenantBotOrderId &&
                            x.ProvisionalDeliveryState == TenantCardProvisionalStates.Provisioning)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.ProvisionalDeliveryState, TenantCardProvisionalStates.Delivered)
                    .SetProperty(x => x.ProvisionalDeliveredAtUtc, now)
                    .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Escalates one order's provisional delivery to manual review so no further automatic panel work happens.
        /// </summary>
        /// <param name="tenantBotOrderId">Internal <c>users.db</c> id of the tenant order.</param>
        /// <param name="reasonCode">Stable secret-free reason code shown to operators.</param>
        /// <param name="cancellationToken">Cancellation token for the short update.</param>
        /// <returns>A task completing after the terminal state commits.</returns>
        /// <remarks>
        /// A terminal state is never rewound by this method, so a replayed callback cannot move a reviewed order back into
        /// an automatic path that might touch the panel again.
        /// </remarks>
        public async Task MarkManualReviewAsync(int tenantBotOrderId, string reasonCode, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            await using var db = _factory.CreateDbContext();
            await db.TenantBotOrders
                .Where(x => x.Id == tenantBotOrderId &&
                            x.ProvisionalDeliveryState != TenantCardProvisionalStates.ManualReview)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.ProvisionalDeliveryState, TenantCardProvisionalStates.ManualReview)
                    .SetProperty(x => x.ProvisionalErrorCode, reasonCode)
                    .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Determines whether a failed provisional create left the panel untouched, so the normal fulfillment path is safe.
        /// </summary>
        /// <param name="publicOrderId">Public tenant order id whose provisional creation key is inspected.</param>
        /// <param name="cancellationToken">Cancellation token for the short read.</param>
        /// <returns>
        /// <c>true</c> when the creation key was never reserved, is still <c>Reserved</c> (no POST was ever authorized), or
        /// is <c>DefinitiveRejected</c> (the panel refused it), so no provisional client can exist. <c>false</c> when the
        /// outcome is <c>PostStarted</c> or <c>Ambiguous</c>, which must be reconciled before any second account is made.
        /// </returns>
        /// <remarks>
        /// Used by the receipt-approval boundary. An unreconciled <c>PostStarted</c>/<c>Ambiguous</c> outcome routes the
        /// order to manual review instead of creating a full account that could duplicate a client the panel already has.
        /// </remarks>
        /// <exception cref="ArgumentException"><paramref name="publicOrderId" /> is empty.</exception>
        public async Task<bool> IsPanelProvenUntouchedAsync(string publicOrderId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(publicOrderId))
                throw new ArgumentException("A public order id is required.", nameof(publicOrderId));

            var operation = await _creationOperations.FindAsync(BuildCreateOperationKey(publicOrderId), cancellationToken).ConfigureAwait(false);
            if (operation == null)
                return true;

            return operation.Outcome is XuiV3CreationOutcome.Reserved or XuiV3CreationOutcome.DefinitiveRejected;
        }

        /// <summary>Reconstructs the original commercial selection from the persisted order.</summary>
        /// <param name="order">Tenant order carrying the customer's purchased service, traffic, and duration keys.</param>
        /// <returns>The selection to revalidate against the current tenant catalog.</returns>
        /// <remarks>
        /// Rebuilt from authoritative order columns only, never from a Telegram callback or conversation state, so a
        /// forged payload cannot select a different service than the one that was purchased.
        /// </remarks>
        private static XuiV3PurchaseSelection BuildSelection(TenantBotOrder order)
            => new()
            {
                ServiceKey = order.ServiceKey,
                TrafficGb = order.TrafficGb,
                DurationKey = order.DurationKey,
                UnlimitedPlanKey = order.UnlimitedPlanKey,
                AccountCount = 1
            };

        /// <summary>Reports whether the order already carries a proven provisional client identity.</summary>
        /// <param name="order">Tenant order being inspected.</param>
        /// <returns><c>true</c> when both the provisional email and UUID are persisted; otherwise <c>false</c>.</returns>
        /// <remarks>
        /// Both values are required. A partially written identity is treated as unproven so the durable creation operation,
        /// not this flag, decides whether another panel create is permissible.
        /// </remarks>
        private static bool HasProvenIdentity(TenantBotOrder order)
            => !string.IsNullOrWhiteSpace(order.ProvisionalAccountEmail)
               && !string.IsNullOrWhiteSpace(order.ProvisionalAccountUuid);

        /// <summary>Builds a result for a non-created attempt, carrying no panel identity.</summary>
        /// <param name="status">Closed-vocabulary outcome.</param>
        /// <param name="orderId">Internal order id, or the requested id when the order was not found.</param>
        /// <param name="publicOrderId">Public order id when known.</param>
        /// <param name="reasonCode">Stable secret-free reason code.</param>
        /// <returns>A result with an empty identity.</returns>
        private static TenantCardProvisionalProvisioningResult Failure(
            TenantCardProvisionalProvisioningStatus status, int orderId, string publicOrderId, string reasonCode)
            => new(status, orderId, publicOrderId, null, null, null, null, 0, 0, reasonCode, CreatedNow: false);

        /// <summary>Builds a result for an order whose provisional client identity is already persisted.</summary>
        /// <param name="order">Order carrying the proven identity.</param>
        /// <param name="createdNow">Always <c>false</c>; an existing client is never re-created.</param>
        /// <returns>A delivered/already-delivered result.</returns>
        private static TenantCardProvisionalProvisioningResult Existing(TenantBotOrder order, bool createdNow)
            => new(
                TenantCardProvisionalProvisioningStatus.AlreadyDelivered,
                order.Id,
                order.OrderId,
                order.ProvisionalAccountEmail,
                order.ProvisionalAccountUuid,
                order.ProvisionalSubId,
                null,
                ProvisionalTrafficGb,
                ProvisionalDurationDays,
                null,
                createdNow);

        /// <summary>Loads one tenant order without tracking it.</summary>
        /// <param name="tenantBotOrderId">Internal order id.</param>
        /// <param name="cancellationToken">Cancellation token for the short read.</param>
        /// <returns>The detached order, or <c>null</c> when it does not exist.</returns>
        private async Task<TenantBotOrder> LoadOrderAsync(int tenantBotOrderId, CancellationToken cancellationToken)
        {
            await using var db = _factory.CreateDbContext();
            return await db.TenantBotOrders.AsNoTracking()
                .SingleOrDefaultAsync(x => x.Id == tenantBotOrderId, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Atomically moves the order from "no attempt" to <c>provisioning</c> before any panel mutation.
        /// </summary>
        /// <param name="orderId">Internal order id being claimed.</param>
        /// <param name="cancellationToken">Cancellation token for the short update.</param>
        /// <returns>A task completing after the guarded update. Losing the race is not an error.</returns>
        /// <remarks>
        /// The guard on <c>none</c> means a concurrent caller can never re-open an order that another attempt already
        /// claimed, which is the durable half of the at-most-once courtesy grant.
        /// </remarks>
        private async Task ClaimProvisioningAsync(int orderId, CancellationToken cancellationToken)
        {
            await using var db = _factory.CreateDbContext();
            await db.TenantBotOrders
                .Where(x => x.Id == orderId && x.ProvisionalDeliveryState == TenantCardProvisionalStates.None)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.ProvisionalDeliveryState, TenantCardProvisionalStates.Provisioning)
                    .SetProperty(x => x.ProvisionalErrorCode, (string)null)
                    .SetProperty(x => x.UpdatedAtUtc, DateTime.UtcNow), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Durably persists the proven provisional client identity on the order.</summary>
        /// <param name="orderId">Internal order id.</param>
        /// <param name="email">Exact panel email of the provisional client; required for later same-client finalization.</param>
        /// <param name="uuid">Exact panel UUID of the provisional client; the strongest identity available.</param>
        /// <param name="subId">Subscription id, falling back to the email when the panel omits it.</param>
        /// <param name="cancellationToken">Cancellation token for the short update.</param>
        /// <returns>A task completing after the identity commits.</returns>
        /// <remarks>
        /// Written before any customer notification, so a failed Telegram send cannot cause a second panel create. The
        /// state stays <c>provisioning</c> until the customer actually receives the details, which keeps "account exists"
        /// and "customer was told" independently observable.
        /// </remarks>
        private async Task PersistIdentityAsync(int orderId, string email, string uuid, string subId, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            await using var db = _factory.CreateDbContext();
            await db.TenantBotOrders
                .Where(x => x.Id == orderId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.ProvisionalAccountEmail, email)
                    .SetProperty(x => x.ProvisionalAccountUuid, uuid)
                    .SetProperty(x => x.ProvisionalSubId, subId)
                    .SetProperty(x => x.ProvisionalCreatedAtUtc, x => x.ProvisionalCreatedAtUtc ?? now)
                    .SetProperty(x => x.ProvisionalErrorCode, (string)null)
                    .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Records a stable secret-free failure code without changing the provisional state machine step.</summary>
        /// <param name="orderId">Internal order id.</param>
        /// <param name="reasonCode">Closed-vocabulary reason code safe to show an operator.</param>
        /// <param name="cancellationToken">Cancellation token for the short update.</param>
        /// <returns>A task completing after the code commits.</returns>
        /// <remarks>
        /// Deliberately does not rewind <c>provisioning</c> to <c>none</c>: the durable creation operation, not this code
        /// string, decides whether a later attempt may reach the panel again.
        /// </remarks>
        private async Task RecordFailureAsync(int orderId, string reasonCode, CancellationToken cancellationToken)
        {
            var now = DateTime.UtcNow;
            await using var db = _factory.CreateDbContext();
            await db.TenantBotOrders
                .Where(x => x.Id == orderId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.ProvisionalErrorCode, reasonCode)
                    .SetProperty(x => x.UpdatedAtUtc, now), cancellationToken).ConfigureAwait(false);
        }
    }
}
