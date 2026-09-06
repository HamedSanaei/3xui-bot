using System.Globalization;
using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Classifies who may authorize a tenant provisioning retry generation beyond a terminal rejection.
/// </summary>
/// <remarks>
/// Only <see cref="OwnerExplicit"/>, <see cref="SuperAdminExplicit"/>, and <see cref="ReviewedRecovery"/>
/// may advance a <see cref="XuiV3CreationOutcome.DefinitiveRejected"/> attempt to the next generation.
/// Every automatic or replayed payment path passes <see cref="None"/> and can never allocate a new attempt.
/// </remarks>
public enum TenantProvisioningRetryAuthorizationKind
{
    /// <summary>Automatic fulfillment, provider callback, reconciliation, startup, or ordinary re-entry; never authorizes a new generation.</summary>
    None,
    /// <summary>The tenant owner explicitly re-confirms an already-paid unfulfilled manual order.</summary>
    OwnerExplicit,
    /// <summary>A global super-admin explicitly confirms or retries the paid tenant order.</summary>
    SuperAdminExplicit,
    /// <summary>The terminal-recovery <c>/inbox_retry_tenant_order</c> command authorized by a durable reviewed-absence reference.</summary>
    ReviewedRecovery
}

/// <summary>
/// Durable, typed authorization for one tenant purchase provisioning retry generation.
/// </summary>
/// <remarks>
/// A tenant retry is not inferred from a settlement <c>Source</c> string; only explicit product and operator
/// actions construct this object. The <see cref="DurableKey"/> is a restricted non-secret internal identity that
/// survives duplicate Telegram delivery, repeated command handling, process crash, and restart so one explicit
/// action can grant at most one generation.
/// </remarks>
public sealed class TenantProvisioningRetryAuthorization
{
    /// <summary>Creates a retry authorization from an already validated kind and durable key.</summary>
    /// <param name="kind">Explicit authorization kind; <see cref="TenantProvisioningRetryAuthorizationKind.None"/> never advances a generation.</param>
    /// <param name="durableKey">Restricted internal key such as <c>tenant-retry:{orderId}:tg:{inboxSequence}</c>; never customer text.</param>
    /// <param name="actorTelegramUserId">Authenticated positive Telegram id of the owner, super-admin, or reviewer.</param>
    /// <exception cref="ArgumentException">The key is null, empty, or not in the restricted internal format.</exception>
    public TenantProvisioningRetryAuthorization(TenantProvisioningRetryAuthorizationKind kind, string durableKey, long actorTelegramUserId)
    {
        if (kind != TenantProvisioningRetryAuthorizationKind.None &&
            (string.IsNullOrWhiteSpace(durableKey) || !IsRestrictedDurableKey(durableKey)))
            throw new ArgumentException("A non-empty restricted durable authorization key is required.", nameof(durableKey));
        if (actorTelegramUserId <= 0)
            throw new ArgumentOutOfRangeException(nameof(actorTelegramUserId));
        Kind = kind;
        DurableKey = durableKey;
        ActorTelegramUserId = actorTelegramUserId;
    }

    /// <summary>Gets the explicit action that owns this authorization; <see cref="TenantProvisioningRetryAuthorizationKind.None"/> for automatic paths.</summary>
    public TenantProvisioningRetryAuthorizationKind Kind { get; }

    /// <summary>Gets the restricted durable identity of the explicit action that must grant at most one generation.</summary>
    public string DurableKey { get; }

    /// <summary>Gets the authenticated Telegram id of the owner, super-admin, or reviewer who acted.</summary>
    public long ActorTelegramUserId { get; }

    /// <summary>Gets whether this authorization may advance a rejected attempt to a new generation.</summary>
    public bool IsExplicit => Kind != TenantProvisioningRetryAuthorizationKind.None && !string.IsNullOrEmpty(DurableKey);

    /// <summary>Restricts durable keys to the internal <c>tenant-retry:</c> prefix formats; no secret or free text is accepted.</summary>
    private static bool IsRestrictedDurableKey(string durableKey) =>
        durableKey.StartsWith(TenantProvisioningAttemptCoordinator.DurableAuthorizationPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Builds the durable authorization of a Telegram-driven explicit confirmation for one tenant order.
    /// </summary>
    /// <param name="kind">Owner or super-admin explicit kind; reviewed recovery uses <see cref="ReviewedRecovery"/>.</param>
    /// <param name="actorTelegramUserId">Authenticated positive Telegram id of the acting owner or super-admin.</param>
    /// <param name="orderId">Positive users.db tenant order id; never derived from customer text.</param>
    /// <returns>
    /// A durable authorization keyed by the current inbox execution, or null when no durable inbox sequence is
    /// present. Callers must treat a null result as unable to prove an explicit action and fail closed.
    /// </returns>
    /// <remarks>
    /// The durable inbox sequence uniquely identifies one explicit confirmation and is stable when the same inbox
    /// row is resumed after a restart, so duplicate delivery can never allocate a second generation.
    /// </remarks>
    public static TenantProvisioningRetryAuthorization TryTelegramConfirmation(
        TenantProvisioningRetryAuthorizationKind kind,
        long actorTelegramUserId,
        int orderId)
    {
        if (kind is not (TenantProvisioningRetryAuthorizationKind.OwnerExplicit or TenantProvisioningRetryAuthorizationKind.SuperAdminExplicit))
            throw new ArgumentOutOfRangeException(nameof(kind));
        var sequence = TelegramUpdateExecutionScope.CurrentSequence;
        return sequence is long seq && seq > 0
            ? new TenantProvisioningRetryAuthorization(kind,
                TenantProvisioningAttemptCoordinator.DurableAuthorizationPrefix + orderId.ToString(CultureInfo.InvariantCulture) + ":tg:" + seq.ToString(CultureInfo.InvariantCulture),
                actorTelegramUserId)
            : null;
    }

    /// <summary>
    /// Builds the durable authorization of an operator-reviewed recovery (<c>/inbox_retry_tenant_order</c>).
    /// </summary>
    /// <param name="actorTelegramUserId">Authenticated positive global super-admin Telegram id stored on the review receipt.</param>
    /// <param name="orderId">Positive users.db tenant order id resolved from the exact persisted operation link.</param>
    /// <param name="reviewReference">Existing <c>review-N</c> reference stored by the authoritative-absence review.</param>
    /// <returns>A reviewed-recovery authorization whose durable key is the restricted review reference.</returns>
    /// <exception cref="ArgumentException">The review reference is missing or not in the restricted <c>review-N</c> format.</exception>
    public static TenantProvisioningRetryAuthorization ReviewedRecovery(long actorTelegramUserId, int orderId, string reviewReference)
    {
        if (orderId <= 0 || reviewReference == null ||
            !System.Text.RegularExpressions.Regex.IsMatch(reviewReference, @"\Areview-[0-9]{1,12}\z"))
            throw new ArgumentException("A positive order id and review-N reference are required.");
        return new TenantProvisioningRetryAuthorization(TenantProvisioningRetryAuthorizationKind.ReviewedRecovery,
            TenantProvisioningAttemptCoordinator.DurableAuthorizationPrefix + orderId.ToString(CultureInfo.InvariantCulture) + ":" + reviewReference,
            actorTelegramUserId);
    }
}

/// <summary>Outcome of resolving which immutable provisioning attempt one tenant purchase order must use next.</summary>
/// <remarks>A resolution is either an allowed attempt key (possibly a newly authorized retry generation) or a
/// fixed safe refusal. The refusal is shown to operators and stored on the order; it contains no attempt key.</remarks>
public sealed class TenantPurchaseAttemptResolution
{
    private TenantPurchaseAttemptResolution() { }

    /// <summary>Gets whether a durable attempt key is available; false means no provisioning should start.</summary>
    public bool Allowed { get; private set; }

    /// <summary>Gets the exact operation key to reserve or reuse: base or <c>:retry:N</c> for this order.</summary>
    public string OperationKey { get; private set; }

    /// <summary>
    /// Gets the durable explicit-authorization key to persist on the reservation, or null when no new generation is
    /// allocated (first attempts and reused attempts keep their existing immutable rows).
    /// </summary>
    public string AuthorizedByKey { get; private set; }

    /// <summary>Gets the fixed safe reason when <see cref="Allowed"/> is false; empty otherwise.</summary>
    public string Refusal { get; private set; }

    /// <summary>Gets the chosen generation for diagnostics: 0 is the base attempt, N is retry:N.</summary>
    public int Generation { get; private set; }

    /// <summary>Creates an allowed resolution for an existing or newly allocated attempt key.</summary>
    internal static TenantPurchaseAttemptResolution CreateAllowed(string operationKey, string authorizedByKey, int generation) => new()
    {
        Allowed = true, OperationKey = operationKey, AuthorizedByKey = authorizedByKey,
        Refusal = string.Empty, Generation = generation
    };

    /// <summary>Creates a safe refusal that must never expose attempt keys, payloads, or tokens.</summary>
    internal static TenantPurchaseAttemptResolution CreateRefused(string reason) => new()
    {
        Allowed = false, OperationKey = null, AuthorizedByKey = null, Refusal = reason, Generation = -1
    };
}

/// <summary>
/// Selects the single immutable XUI provisioning attempt for one paid tenant purchase order.
/// </summary>
/// <remarks>
/// A <see cref="TenantBotOrder"/> is the commercial transaction; each <c>XuiV3CreationOperation</c> row is one
/// immutable provisioning attempt. Attempt keys are <c>tenant-create:{orderId}</c> and
/// <c>tenant-create:{orderId}:retry:N</c>. Reserved, PostStarted, Ambiguous, and Applied attempts are never
/// duplicated merely because fulfillment was invoked again. Only a terminal <see cref="XuiV3CreationOutcome.DefinitiveRejected"/>
/// attempt plus a NEW explicit durable retry authorization may allocate the next generation, and one authorization
/// event grants at most one generation. The coordinator performs no writes itself: allocation is durably recorded by
/// the normal reservation insert, and the per-order fulfillment gate plus the unique operation key remain the
/// concurrency boundary.
/// </remarks>
public sealed class TenantProvisioningAttemptCoordinator
{
    /// <summary>Exact prefix of every tenant purchase creation attempt key.</summary>
    public const string TenantCreationPrefix = "tenant-create:";

    /// <summary>Restricted durable-key prefix shared by every tenant retry authorization format.</summary>
    public const string DurableAuthorizationPrefix = "tenant-retry:";

    private readonly UserDbContextFactory _factory;

    /// <summary>Creates the attempt coordinator over short-lived users.db contexts.</summary>
    /// <param name="factory">Required users.db factory; no context is retained by the coordinator.</param>
    /// <remarks>Read-only decision logic: attempt rows are created and classified only by the existing operation store.</remarks>
    public TenantProvisioningAttemptCoordinator(UserDbContextFactory factory) => _factory = factory;

    /// <summary>Builds the exact base attempt key for one tenant purchase order.</summary>
    /// <param name="orderId">Positive users.db tenant order id.</param>
    /// <returns>The immutable <c>tenant-create:{orderId}</c> key used by the first provisioning attempt.</returns>
    public static string BuildBaseOperationKey(int orderId) =>
        TenantCreationPrefix + orderId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Builds the exact retry attempt key for one tenant purchase order and generation.</summary>
    /// <param name="orderId">Positive users.db tenant order id.</param>
    /// <param name="generation">Positive retry generation N, producing <c>tenant-create:{orderId}:retry:N</c>.</param>
    /// <returns>The immutable retry attempt key; generations are never rewritten or reused with changed parameters.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The generation is not positive.</exception>
    public static string BuildRetryOperationKey(int orderId, int generation)
    {
        if (generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        return BuildBaseOperationKey(orderId) + ":retry:" + generation.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Strictly parses the exact tenant order id from an attempt key of this flow only.</summary>
    /// <param name="operationKey">Persisted operation key; command text is never accepted.</param>
    /// <returns>The positive users.db order id, or null for malformed keys or keys belonging to another flow.</returns>
    /// <remarks>The numeric delimiter prevents <c>tenant-create:2050</c> from matching order 205.</remarks>
    public static int? TryParseTenantCreationOrderId(string operationKey)
    {
        if (string.IsNullOrWhiteSpace(operationKey) || !operationKey.StartsWith(TenantCreationPrefix, StringComparison.Ordinal))
            return null;
        var remainder = operationKey[TenantCreationPrefix.Length..];
        if (remainder.Length == 0)
            return null;
        var orderPart = remainder;
        var generationPart = (string)null;
        var retryMarker = remainder.IndexOf(":retry:", StringComparison.Ordinal);
        if (retryMarker >= 0)
        {
            orderPart = remainder[..retryMarker];
            generationPart = remainder[(retryMarker + ":retry:".Length)..];
        }
        if (!int.TryParse(orderPart, NumberStyles.None, CultureInfo.InvariantCulture, out var orderId) || orderId <= 0)
            return null;
        if (generationPart != null &&
            (!int.TryParse(generationPart, NumberStyles.None, CultureInfo.InvariantCulture, out var generation) || generation <= 0))
            return null;
        // Reject unexpected extra segments such as tenant-create:{id}:other so foreign flows never correlate.
        return generationPart == null || remainder.EndsWith(":retry:" + generationPart, StringComparison.Ordinal)
            ? orderId
            : null;
    }

    /// <summary>
    /// Resolves which durable attempt one tenant purchase order must use for the current fulfillment call.
    /// </summary>
    /// <param name="orderId">Positive users.db tenant purchase order id; the coordinator never matches other orders.</param>
    /// <param name="authorization">
    /// Typed explicit retry authorization supplied only by owner, super-admin, or reviewed-recovery callers. Null
    /// (or <see cref="TenantProvisioningRetryAuthorizationKind.None"/>) marks an automatic or replayed path that can
    /// reuse non-terminal attempts but can never allocate a new generation after a definitive rejection.
    /// </param>
    /// <param name="cancellationToken">Cancellation of the short detached users.db read.</param>
    /// <returns>
    /// An allowed resolution carrying the exact attempt key (and the durable authorization key when a new retry
    /// generation is allocated), or a fixed safe refusal when the latest attempt is terminal and no unused explicit
    /// authorization exists.
    /// </returns>
    /// <remarks>
    /// State rules: no attempt allocates the base key; Reserved may compete for its single POST; PostStarted and
    /// Ambiguous reuse only for read-back/reconciliation; Applied resumes idempotent settlement; only
    /// DefinitiveRejected plus a NEW explicit authorization advances to <c>retry:(highestGeneration + 1)</c>.
    /// Replaying the same authorization that already granted an attempt never advances again.
    /// </remarks>
    public async Task<TenantPurchaseAttemptResolution> ResolvePurchaseAttemptAsync(
        int orderId,
        TenantProvisioningRetryAuthorization authorization,
        CancellationToken cancellationToken)
    {
        if (orderId <= 0)
            throw new ArgumentOutOfRangeException(nameof(orderId));

        var baseKey = BuildBaseOperationKey(orderId);
        await using var db = _factory.CreateDbContext();
        var rows = await db.XuiV3CreationOperations.AsNoTracking()
            .Where(x => x.OperationKey == baseKey || x.OperationKey.StartsWith(baseKey + ":retry:"))
            .Select(x => new { x.OperationKey, x.Outcome, x.AuthorizedByKey })
            .ToListAsync(cancellationToken);

        List<(int Generation, string Key, XuiV3CreationOutcome Outcome, string AuthorizedByKey)> attempts = null;
        foreach (var row in rows)
        {
            int generation;
            if (row.OperationKey == baseKey)
            {
                generation = 0;
            }
            else if (!TryParseRetryGeneration(row.OperationKey, baseKey, out generation))
            {
                // A foreign key sharing the prefix never influences this order's decision.
                continue;
            }
            (attempts ??= new List<(int, string, XuiV3CreationOutcome, string)>()).Add((generation, row.OperationKey, row.Outcome, row.AuthorizedByKey));
        }

        if (attempts == null || attempts.Count == 0)
            return TenantPurchaseAttemptResolution.CreateAllowed(baseKey, null, 0);

        var highest = attempts[0];
        for (var i = 1; i < attempts.Count; i++)
            if (attempts[i].Generation > highest.Generation)
                highest = attempts[i];

        if (highest.Outcome is not (XuiV3CreationOutcome.Reserved or XuiV3CreationOutcome.PostStarted
            or XuiV3CreationOutcome.Ambiguous or XuiV3CreationOutcome.Applied))
        {
            // The latest attempt is terminally rejected. Only an explicit, previously unused authorization may open
            // the next generation; automatic callbacks, reconciliation, startup, and ordinary re-entry cannot.
            if (authorization == null || !authorization.IsExplicit)
                return TenantPurchaseAttemptResolution.CreateRefused(
                    "Tenant provisioning requires an explicit owner or super-admin confirmation before the next attempt.");
            foreach (var attempt in attempts)
            {
                if (attempt.Generation > 0 && string.Equals(attempt.AuthorizedByKey, authorization.DurableKey, StringComparison.Ordinal))
                    return TenantPurchaseAttemptResolution.CreateRefused(
                        "Tenant retry authorization was already used by a previous attempt; a new explicit confirmation is required.");
            }

            var nextGeneration = highest.Generation + 1;
            return TenantPurchaseAttemptResolution.CreateAllowed(
                BuildRetryOperationKey(orderId, nextGeneration), authorization.DurableKey, nextGeneration);
        }

        return TenantPurchaseAttemptResolution.CreateAllowed(highest.Key, null, highest.Generation);
    }

    /// <summary>Parses the positive retry generation from an exact <c>tenant-create:{orderId}:retry:N</c> suffix.</summary>
    /// <param name="operationKey">Full persisted attempt key.</param>
    /// <param name="baseKey">Exact base key <c>tenant-create:{orderId}</c> of this order.</param>
    /// <param name="generation">Positive generation N when the key parses; otherwise zero.</param>
    /// <returns>True only when the remainder is exactly <c>:retry:{positive int}</c>.</returns>
    private static bool TryParseRetryGeneration(string operationKey, string baseKey, out int generation)
    {
        generation = 0;
        if (!operationKey.StartsWith(baseKey, StringComparison.Ordinal))
            return false;
        var suffix = operationKey[baseKey.Length..];
        const string marker = ":retry:";
        if (!suffix.StartsWith(marker, StringComparison.Ordinal))
            return false;
        var value = suffix[marker.Length..];
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out generation) && generation > 0;
    }
}
