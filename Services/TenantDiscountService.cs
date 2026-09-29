using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>Business failures returned by tenant discount reads and short users.db writes.</summary>
public enum TenantDiscountFailure
{
    /// <summary>Successful validation or transaction.</summary>
    None,
    /// <summary>Customer-entered ASCII code is malformed or unknown in this store.</summary>
    InvalidCode,
    /// <summary>Owner draft has contradictory type, amount, scope or capacity fields.</summary>
    InvalidDefinition,
    /// <summary>Authenticated owner does not own this selected tenant bot.</summary>
    Unauthorized,
    /// <summary>Requested live definition no longer exists in this store.</summary>
    NotFound,
    /// <summary>A nondeleted definition already uses the normalized code in this store.</summary>
    DuplicateCode,
    /// <summary>Displayed selection, tariff or revision no longer matches admission.</summary>
    ChangedQuote,
    /// <summary>Selected definition is inactive or soft-deleted.</summary>
    Disabled,
    /// <summary>Definition does not cover this purchase or renewal kind.</summary>
    Scope,
    /// <summary>Undiscounted order does not meet the code minimum.</summary>
    MinimumOrder,
    /// <summary>All paid or reserved uses occupy the code's maximum.</summary>
    Exhausted,
    /// <summary>No positive reduction fits between the sale and colleague cost.</summary>
    NoAvailableMargin,
    /// <summary>A concurrent or incompatible order/payment method already owns the action.</summary>
    Conflict
}

/// <summary>A successful value or an explicit, non-fallback discount failure.</summary>
public readonly record struct TenantDiscountResult<T>(T Value, TenantDiscountFailure Failure)
{
    /// <summary>Whether the operation succeeded; an unsuccessful result must never be charged at gross instead.</summary>
    public bool Success => Failure == TenantDiscountFailure.None;
    /// <summary>Constructs a successful result.</summary>
    /// <param name="value">Validated tenant-scoped value; may be null only when success explicitly represents no selection.</param>
    /// <returns>A successful result containing the value without a fallback price.</returns>
    public static TenantDiscountResult<T> Ok(T value) => new(value, TenantDiscountFailure.None);
    /// <summary>Constructs a business failure without a value.</summary>
    /// <param name="reason">Non-success validation or concurrency failure; never pass None.</param>
    /// <returns>An unsuccessful result with no usable value.</returns>
    public static TenantDiscountResult<T> Fail(TenantDiscountFailure reason) => new(default, reason);
}

/// <summary>Owner-entered definition; Code is normalized before persistence, never in callback data.</summary>
/// <param name="Code">Owner-entered ASCII code, normalized to uppercase before insertion.</param>
/// <param name="Kind">Fixed whole-toman or integer percentage reduction.</param>
/// <param name="Scope">Purchase, renewal or both eligible order kinds.</param>
/// <param name="FixedAmountToman">Positive whole-toman reduction for fixed codes, otherwise null.</param>
/// <param name="Percent">Integer from 1 to 100 for percentage codes, otherwise null.</param>
/// <param name="MaxDiscountToman">Optional positive maximum whole-toman reduction.</param>
/// <param name="MinimumOrderToman">Nonnegative undiscounted minimum sale amount in whole toman.</param>
/// <param name="MaxUses">Maximum paid and pending claims across this storefront, from 1 to 1,000,000.</param>
/// <param name="IsActive">Whether customers can select the code for new checkouts.</param>
public sealed record TenantDiscountCodeInput(string Code, string Kind, string Scope, long? FixedAmountToman,
    int? Percent, long? MaxDiscountToman, long MinimumOrderToman, int MaxUses, bool IsActive);

/// <summary>Frozen, actual discount and final payable amount, in whole toman.</summary>
/// <param name="GrossToman">Tenant's undiscounted sale price in whole Iranian toman.</param>
/// <param name="BaseCostToman">Colleague cost in whole toman, not exposed to customers.</param>
/// <param name="DiscountAmountToman">Realized positive reduction after maximum and margin caps.</param>
/// <param name="NetToman">Final payable whole-toman amount; never below base cost.</param>
public readonly record struct TenantDiscountPrice(long GrossToman, long BaseCostToman, long DiscountAmountToman, long NetToman);

/// <summary>Renewal confirmation values displayed to this customer, used to detect tariff or code changes at admission.</summary>
/// <param name="CodeId">Original users.db code id, scoped by tenant at admission.</param>
/// <param name="CodeUpdatedAtUtc">Exact owner configuration revision displayed to the customer.</param>
/// <param name="Displayed">Gross, base, realized reduction and net snapshot shown at preview.</param>
public sealed record TenantDiscountSelection(int CodeId, DateTime CodeUpdatedAtUtc, TenantDiscountPrice Displayed);

/// <summary>Tenant code capacity split into paid uses and claims awaiting definitive payment.</summary>
/// <param name="Consumed">Count of durably verified paid claims.</param>
/// <param name="Reserved">Count of admitted orders with unresolved payment.</param>
/// <param name="Remaining">Maximum uses minus consumed and reserved claims.</param>
public readonly record struct TenantDiscountUsage(int Consumed, int Reserved, int Remaining);

/// <summary>
/// Stores tenant-owned definitions, displayed quotes and exactly-once reservations exclusively in users.db.
/// Gateway availability, Telegram delivery and payment effects belong to the caller, outside these transactions.
/// </summary>
public sealed class TenantDiscountService
{
    private readonly UserDbContextFactory _factory;
    private static readonly TimeSpan QuoteLifetime = TimeSpan.FromHours(2);

    /// <summary>Creates a service with independently owned users.db contexts.</summary>
    /// <param name="factory">Users.db factory; each short operation owns its own EF context.</param>
    public TenantDiscountService(UserDbContextFactory factory) => _factory = factory;

    /// <summary>Trims and canonicalizes only ASCII letters, digits, underscore and hyphen, length 3–32; null means invalid.</summary>
    /// <param name="input">Optional user-entered ASCII code, with surrounding whitespace allowed.</param>
    /// <returns>Uppercase 3–32-character code, or null when the input is invalid.</returns>
    /// <example><code>var code = TenantDiscountService.NormalizeCode(" sale-25 "); // SALE-25</code></example>
    public static string NormalizeCode(string input)
    {
        var code = input?.Trim();
        if (code is not { Length: >= 3 and <= 32 }) return null;
        foreach (var c in code)
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')) return null;
        return code.ToUpperInvariant();
    }

    /// <summary>Checks a complete draft before any owner mutation; no irrelevant amount fields may be retained.</summary>
    /// <param name="input">Complete owner draft with exactly one reduction kind, valid scope and capacity.</param>
    /// <returns>None for a valid definition; otherwise a precise input failure without persistence.</returns>
    public static TenantDiscountFailure ValidateOwnerInput(TenantDiscountCodeInput input)
    {
        if (input == null || NormalizeCode(input.Code) == null) return TenantDiscountFailure.InvalidCode;
        if (input.MaxUses is < 1 or > 1_000_000 || input.MinimumOrderToman < 0
            || !ValidScope(input.Scope)) return TenantDiscountFailure.InvalidDefinition;
        if (input.Kind == TenantDiscountKinds.Fixed)
            return input.FixedAmountToman > 0 && input.Percent == null
                && (input.MaxDiscountToman == null || input.MaxDiscountToman > 0)
                ? TenantDiscountFailure.None : TenantDiscountFailure.InvalidDefinition;
        if (input.Kind == TenantDiscountKinds.Percent)
            return input.Percent is >= 1 and <= 100 && input.FixedAmountToman == null
                && (input.MaxDiscountToman == null || input.MaxDiscountToman > 0)
                ? TenantDiscountFailure.None : TenantDiscountFailure.InvalidDefinition;
        return TenantDiscountFailure.InvalidDefinition;
    }

    /// <summary>Computes the realized reduction against the current tenant tariff, never below colleague cost.</summary>
    /// <param name="code">Selected tenant code from this storefront, not a global promotion.</param>
    /// <param name="grossToman">Positive undiscounted storefront sale in whole toman.</param>
    /// <param name="baseCostToman">Nonnegative colleague cost in whole toman; customer net must not cross it.</param>
    /// <param name="scope">Explicit purchase or renewal order kind.</param>
    /// <returns>Actual gross, base, capped discount and net; or a named eligibility failure.</returns>
    /// <remarks>Percentage uses decimal arithmetic before flooring, so multiplying large long amounts cannot overflow.</remarks>
    public static TenantDiscountResult<TenantDiscountPrice> Quote(TenantDiscountCode code, long grossToman,
        long baseCostToman, string scope)
    {
        if (code == null || code.IsDeleted || !code.IsActive) return TenantDiscountResult<TenantDiscountPrice>.Fail(TenantDiscountFailure.Disabled);
        if (!MatchesScope(code.Scope, scope)) return TenantDiscountResult<TenantDiscountPrice>.Fail(TenantDiscountFailure.Scope);
        if (grossToman <= 0 || baseCostToman < 0 || grossToman < code.MinimumOrderToman)
            return TenantDiscountResult<TenantDiscountPrice>.Fail(TenantDiscountFailure.MinimumOrder);
        if (grossToman <= baseCostToman) return TenantDiscountResult<TenantDiscountPrice>.Fail(TenantDiscountFailure.NoAvailableMargin);
        var margin = grossToman - baseCostToman;
        decimal candidate;
        if (code.Kind == TenantDiscountKinds.Fixed && code.FixedAmountToman > 0 && code.Percent == null
            && (code.MaxDiscountToman == null || code.MaxDiscountToman > 0))
            candidate = code.FixedAmountToman.Value;
        else if (code.Kind == TenantDiscountKinds.Percent && code.Percent is >= 1 and <= 100
            && code.FixedAmountToman == null && (code.MaxDiscountToman == null || code.MaxDiscountToman > 0))
            candidate = decimal.Floor((decimal)grossToman * code.Percent.Value / 100M);
        else return TenantDiscountResult<TenantDiscountPrice>.Fail(TenantDiscountFailure.InvalidDefinition);
        if (code.MaxDiscountToman.HasValue) candidate = decimal.Min(candidate, code.MaxDiscountToman.Value);
        candidate = decimal.Min(candidate, margin);
        if (candidate < 1) return TenantDiscountResult<TenantDiscountPrice>.Fail(TenantDiscountFailure.NoAvailableMargin);
        var reduction = checked((long)candidate);
        return TenantDiscountResult<TenantDiscountPrice>.Ok(new(grossToman, baseCostToman, reduction, grossToman - reduction));
    }

    /// <summary>Checks whether the tenant offers at least one active code for the specified purchase/renew scope.</summary>
    /// <param name="tenantBotId">Internal id of the tenant storefront, not its numeric Telegram bot id.</param>
    /// <param name="scope">Purchase or renewal order kind, never both for a checkout.</param>
    /// <param name="token">Cancellation of the read-only users.db query.</param>
    /// <returns>True when this store has any live active code eligible for the requested kind.</returns>
    public async Task<bool> HasActiveScopeAsync(string tenantBotId, string scope, CancellationToken token = default)
    {
        if (!ValidCheckoutScope(scope)) return false;
        await using var db = _factory.CreateDbContext();
        return await db.TenantDiscountCodes.AsNoTracking().AnyAsync(x => x.TenantBotId == tenantBotId && x.IsActive && !x.IsDeleted
            && (x.Scope == scope || x.Scope == TenantDiscountScopes.Both), token);
    }

    /// <summary>Resolves a code for this storefront and returns its currently available displayed price (not a reservation).</summary>
    /// <param name="tenantBotId">Internal id of the customer's currently authenticated tenant bot.</param>
    /// <param name="enteredCode">Customer-entered ASCII code, normalized only for lookup.</param>
    /// <param name="grossToman">Current undiscounted checkout amount in whole toman.</param>
    /// <param name="baseCostToman">Current colleague cost in whole toman, used for the net floor.</param>
    /// <param name="scope">Single purchase or renewal order kind.</param>
    /// <param name="token">Cancellation of the users.db preview query.</param>
    /// <returns>Untracked code and preview price or a named error; no capacity is reserved.</returns>
    public async Task<TenantDiscountResult<(TenantDiscountCode Code, TenantDiscountPrice Price)>> QuoteAsync(
        string tenantBotId, string enteredCode, long grossToman, long baseCostToman, string scope, CancellationToken token = default)
    {
        var normalized = NormalizeCode(enteredCode);
        if (normalized == null) return TenantDiscountResult<(TenantDiscountCode, TenantDiscountPrice)>.Fail(TenantDiscountFailure.InvalidCode);
        await using var db = _factory.CreateDbContext();
        var code = await db.TenantDiscountCodes.AsNoTracking().SingleOrDefaultAsync(x => x.TenantBotId == tenantBotId
            && x.Code == normalized && !x.IsDeleted, token);
        if (code == null) return TenantDiscountResult<(TenantDiscountCode, TenantDiscountPrice)>.Fail(TenantDiscountFailure.InvalidCode);
        var price = Quote(code, grossToman, baseCostToman, scope);
        if (!price.Success) return TenantDiscountResult<(TenantDiscountCode, TenantDiscountPrice)>.Fail(price.Failure);
        if (await db.TenantDiscountRedemptions.CountAsync(x => x.CodeId == code.Id
            && (x.State == TenantDiscountRedemptionStates.Reserved || x.State == TenantDiscountRedemptionStates.Consumed), token) >= code.MaxUses)
            return TenantDiscountResult<(TenantDiscountCode, TenantDiscountPrice)>.Fail(TenantDiscountFailure.Exhausted);
        return TenantDiscountResult<(TenantDiscountCode, TenantDiscountPrice)>.Ok((code, price.Value));
    }

    /// <summary>Lists definitions belonging to the authenticated owner and selected storefront, including disabled codes.</summary>
    /// <param name="tenantBotId">Internal id of the currently selected tenant storefront.</param>
    /// <param name="ownerTelegramUserId">Authenticated numeric Telegram owner id.</param>
    /// <param name="page">Zero-based page, ten nondeleted definitions per page.</param>
    /// <param name="token">Cancellation of users.db read.</param>
    /// <returns>Detached definitions, possibly empty, or an authorization failure.</returns>
    public async Task<TenantDiscountResult<IReadOnlyList<TenantDiscountCode>>> ListCodesAsync(
        string tenantBotId, long ownerTelegramUserId, int page = 0, CancellationToken token = default)
    {
        await using var db = _factory.CreateDbContext();
        if (!await OwnsStoreAsync(db, tenantBotId, ownerTelegramUserId, token))
            return TenantDiscountResult<IReadOnlyList<TenantDiscountCode>>.Fail(TenantDiscountFailure.Unauthorized);
        var codes = await db.TenantDiscountCodes.AsNoTracking().Where(x => x.TenantBotId == tenantBotId && !x.IsDeleted)
            .OrderByDescending(x => x.Id).Skip(Math.Clamp(page, 0, int.MaxValue / 10) * 10).Take(10).ToListAsync(token);
        return TenantDiscountResult<IReadOnlyList<TenantDiscountCode>>.Ok(codes);
    }

    /// <summary>Gets a live definition only if it belongs to this exact authenticated storefront owner.</summary>
    /// <param name="tenantBotId">Internal selected storefront bot id.</param>
    /// <param name="ownerTelegramUserId">Authenticated numeric Telegram owner id.</param>
    /// <param name="codeId">Internal users.db code id, not a Telegram callback grant.</param>
    /// <param name="token">Cancellation of users.db read.</param>
    /// <returns>Detached nondeleted code owned by this store, or a named failure.</returns>
    public async Task<TenantDiscountResult<TenantDiscountCode>> GetCodeAsync(string tenantBotId, long ownerTelegramUserId,
        int codeId, CancellationToken token = default)
    {
        await using var db = _factory.CreateDbContext();
        if (!await OwnsStoreAsync(db, tenantBotId, ownerTelegramUserId, token))
            return TenantDiscountResult<TenantDiscountCode>.Fail(TenantDiscountFailure.Unauthorized);
        var code = await db.TenantDiscountCodes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == codeId
            && x.TenantBotId == tenantBotId && !x.IsDeleted, token);
        return code == null ? TenantDiscountResult<TenantDiscountCode>.Fail(TenantDiscountFailure.NotFound)
            : TenantDiscountResult<TenantDiscountCode>.Ok(code);
    }

    /// <summary>Reads used, reserved and remaining capacity for a code owned by this exact storefront owner.</summary>
    /// <param name="tenantBotId">Internal selected storefront bot id.</param>
    /// <param name="ownerTelegramUserId">Authenticated Telegram owner id.</param>
    /// <param name="codeId">Internal users.db id of a live code in this store.</param>
    /// <param name="token">Cancellation of the capacity reads.</param>
    /// <returns>Consumed, reserved and still-free uses, or an ownership/not-found failure.</returns>
    public async Task<TenantDiscountResult<TenantDiscountUsage>> GetUsageAsync(string tenantBotId,
        long ownerTelegramUserId, int codeId, CancellationToken token = default)
    {
        await using var db = _factory.CreateDbContext();
        if (!await OwnsStoreAsync(db, tenantBotId, ownerTelegramUserId, token))
            return TenantDiscountResult<TenantDiscountUsage>.Fail(TenantDiscountFailure.Unauthorized);
        var code = await db.TenantDiscountCodes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == codeId
            && x.TenantBotId == tenantBotId && !x.IsDeleted, token);
        if (code == null) return TenantDiscountResult<TenantDiscountUsage>.Fail(TenantDiscountFailure.NotFound);
        var reserved = await db.TenantDiscountRedemptions.CountAsync(x => x.CodeId == codeId
            && x.State == TenantDiscountRedemptionStates.Reserved, token);
        var consumed = await db.TenantDiscountRedemptions.CountAsync(x => x.CodeId == codeId
            && x.State == TenantDiscountRedemptionStates.Consumed, token);
        return TenantDiscountResult<TenantDiscountUsage>.Ok(new(consumed, reserved,
            Math.Max(0, code.MaxUses - consumed - reserved)));
    }

    /// <summary>Creates or edits one code, comparing the original revision and refusing a limit below occupied claims.</summary>
    /// <param name="tenantBotId">Internal selected tenant bot id whose owner and uniqueness are checked.</param>
    /// <param name="ownerTelegramUserId">Authenticated owner Telegram user id, not the store bot id.</param>
    /// <param name="input">Fully entered discount definition; money amounts use whole toman.</param>
    /// <param name="codeId">Internal existing code id for edits, null for creation.</param>
    /// <param name="expectedUpdatedAtUtc">Original code revision for edits, null for creation.</param>
    /// <param name="token">Cancellation of the short users.db transaction.</param>
    /// <returns>Persisted tracked definition or a named validation, ownership, capacity or revision failure.</returns>
    /// <remarks>A bot-row writer lock serializes same-store creates; a code-row writer lock serializes edits and reservations.</remarks>
    public Task<TenantDiscountResult<TenantDiscountCode>> SaveCodeAsync(string tenantBotId, long ownerTelegramUserId,
        TenantDiscountCodeInput input, int? codeId = null, DateTime? expectedUpdatedAtUtc = null, CancellationToken token = default)
    {
        var validation = ValidateOwnerInput(input);
        if (validation != TenantDiscountFailure.None) return Task.FromResult(TenantDiscountResult<TenantDiscountCode>.Fail(validation));
        if (codeId.HasValue != expectedUpdatedAtUtc.HasValue)
            return Task.FromResult(TenantDiscountResult<TenantDiscountCode>.Fail(TenantDiscountFailure.Conflict));
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var store = await LockOwnerStoreAsync(db, tenantBotId, ownerTelegramUserId, ct);
            if (store == null) return TenantDiscountResult<TenantDiscountCode>.Fail(TenantDiscountFailure.Unauthorized);
            TenantDiscountCode code;
            if (codeId.HasValue)
            {
                code = await LockCodeAsync(db, tenantBotId, codeId.Value, ct);
                if (code == null || code.IsDeleted) return TenantDiscountResult<TenantDiscountCode>.Fail(TenantDiscountFailure.NotFound);
                if (code.UpdatedAtUtc != expectedUpdatedAtUtc)
                    return TenantDiscountResult<TenantDiscountCode>.Fail(TenantDiscountFailure.Conflict);
                if (input.MaxUses < await OccupiedAsync(db, code.Id, ct))
                    return TenantDiscountResult<TenantDiscountCode>.Fail(TenantDiscountFailure.Exhausted);
            }
            else code = new TenantDiscountCode { TenantBotId = tenantBotId, CreatedAtUtc = DateTime.UtcNow };
            var normalized = NormalizeCode(input.Code);
            if (await db.TenantDiscountCodes.AnyAsync(x => x.TenantBotId == tenantBotId && x.Code == normalized
                && !x.IsDeleted && x.Id != code.Id, ct))
                return TenantDiscountResult<TenantDiscountCode>.Fail(TenantDiscountFailure.DuplicateCode);
            code.Code = normalized;
            code.Kind = input.Kind;
            code.Scope = input.Scope;
            code.FixedAmountToman = input.FixedAmountToman;
            code.Percent = input.Percent;
            code.MaxDiscountToman = input.MaxDiscountToman;
            code.MinimumOrderToman = input.MinimumOrderToman;
            code.MaxUses = input.MaxUses;
            code.IsActive = input.IsActive;
            code.UpdatedAtUtc = NextRevision(code.UpdatedAtUtc);
            if (!codeId.HasValue) db.TenantDiscountCodes.Add(code);
            store.UpdatedAtUtc = NextRevision(store.UpdatedAtUtc);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return TenantDiscountResult<TenantDiscountCode>.Ok(code);
        }, token);
    }

    /// <summary>Applies an explicit target activation state; repeat requests do not toggle or bump revisions.</summary>
    /// <param name="tenantBotId">Internal id of the selected tenant storefront.</param>
    /// <param name="ownerTelegramUserId">Authenticated Telegram owner of that store.</param>
    /// <param name="codeId">Internal live users.db definition id.</param>
    /// <param name="active">Explicit desired state, not a toggle; false blocks new previews.</param>
    /// <param name="token">Cancellation of the serialized users.db write.</param>
    /// <returns>Updated definition, or a named authorization/not-found failure.</returns>
    public Task<TenantDiscountResult<TenantDiscountCode>> SetActiveAsync(string tenantBotId, long ownerTelegramUserId,
        int codeId, bool active, CancellationToken token = default) => MutateStateAsync(tenantBotId, ownerTelegramUserId, codeId, active, false, token);

    /// <summary>Soft-deletes and disables a code without removing its paid and reserved order history.</summary>
    /// <param name="tenantBotId">Internal id of the selected tenant storefront.</param>
    /// <param name="ownerTelegramUserId">Authenticated Telegram owner of that store.</param>
    /// <param name="codeId">Internal users.db id to soft-delete, not an order id.</param>
    /// <param name="token">Cancellation of the serialized users.db write.</param>
    /// <returns>Disabled soft-deleted definition or a named ownership/not-found failure.</returns>
    public Task<TenantDiscountResult<TenantDiscountCode>> DeleteAsync(string tenantBotId, long ownerTelegramUserId,
        int codeId, CancellationToken token = default) => MutateStateAsync(tenantBotId, ownerTelegramUserId, codeId, false, true, token);

    /// <summary>Serializes an explicit enable/disable or soft-delete with owner authorization and revision bump.</summary>
    /// <param name="botId">Internal selected tenant bot id.</param>
    /// <param name="ownerId">Authenticated Telegram owner id.</param>
    /// <param name="codeId">Internal users.db definition id.</param>
    /// <param name="active">Target activation state.</param>
    /// <param name="delete">Whether the definition is permanently hidden from new checkouts.</param>
    /// <param name="token">Cancellation of users.db transaction.</param>
    /// <returns>Tracked updated definition or a named ownership/not-found failure.</returns>
    private Task<TenantDiscountResult<TenantDiscountCode>> MutateStateAsync(string botId, long ownerId, int codeId,
        bool active, bool delete, CancellationToken token) => SqliteOperation.RunAsync(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var store = await LockOwnerStoreAsync(db, botId, ownerId, ct);
        if (store == null) return TenantDiscountResult<TenantDiscountCode>.Fail(TenantDiscountFailure.Unauthorized);
        var code = await LockCodeAsync(db, botId, codeId, ct);
        if (code == null) return TenantDiscountResult<TenantDiscountCode>.Fail(TenantDiscountFailure.NotFound);
        if (code.IsDeleted && !delete) return TenantDiscountResult<TenantDiscountCode>.Fail(TenantDiscountFailure.NotFound);
        if (code.IsDeleted != delete || code.IsActive != active)
        {
            code.IsDeleted = delete;
            code.IsActive = active;
            code.UpdatedAtUtc = NextRevision(code.UpdatedAtUtc);
            store.UpdatedAtUtc = NextRevision(store.UpdatedAtUtc);
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return TenantDiscountResult<TenantDiscountCode>.Ok(code);
    }, token);

    /// <summary>Creates a short-lived unbound purchase confirmation at the current, undiscounted tariff.</summary>
    /// <param name="tenantBotId">Internal current tenant storefront id.</param>
    /// <param name="customerId">Numeric Telegram sender id whose checkout owns this quote.</param>
    /// <param name="chatId">Numeric Telegram chat to bind when a message is delivered.</param>
    /// <param name="selectionKey">Server-side validated purchase plan identity.</param>
    /// <param name="grossToman">Undiscounted tenant sale amount in whole toman.</param>
    /// <param name="baseCostToman">Colleague cost in whole toman; no reduction may cross it.</param>
    /// <param name="token">Cancellation of the users.db insert.</param>
    /// <returns>Unbound, unadmitted quote expiring two hours after creation.</returns>
    /// <exception cref="ArgumentException">Checkout identity or tariff is malformed.</exception>
    /// <remarks>Only the Telegram-supplied message id from a successful render may subsequently bind this quote.</remarks>
    public Task<TenantDiscountQuote> CreateQuoteAsync(string tenantBotId, long customerId, long chatId,
        string selectionKey, long grossToman, long baseCostToman, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(tenantBotId) || customerId <= 0 || chatId == 0 || string.IsNullOrWhiteSpace(selectionKey)
            || grossToman <= 0 || baseCostToman < 0 || grossToman < baseCostToman) throw new ArgumentException("Invalid purchase quote identity or tariff.");
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            var now = DateTime.UtcNow;
            var quote = new TenantDiscountQuote
            {
                TenantBotId = tenantBotId, CustomerTelegramUserId = customerId, ChatId = chatId,
                SelectionKey = selectionKey, GrossToman = grossToman, BaseCostToman = baseCostToman,
                NetToman = grossToman, State = TenantDiscountQuoteStates.Open,
                CreatedAtUtc = now, UpdatedAtUtc = now, ExpiresAtUtc = now.Add(QuoteLifetime)
            };
            db.TenantDiscountQuotes.Add(quote);
            await db.SaveChangesAsync(ct);
            return quote;
        }, token);
    }

    /// <summary>Binds the successful Telegram render once; rebinding a displayed or admitted quote is forbidden.</summary>
    /// <param name="quoteId">Internal users.db id of the unbound open quote.</param>
    /// <param name="tenantBotId">Authenticated internal tenant bot id.</param>
    /// <param name="customerId">Original Telegram sender id.</param>
    /// <param name="chatId">Original numeric Telegram chat id.</param>
    /// <param name="messageId">Positive message id returned by a successful Telegram send/edit.</param>
    /// <param name="token">Cancellation of the users.db binding transaction.</param>
    /// <returns>Message-bound quote, or ChangedQuote if it expired or was already bound.</returns>
    public Task<TenantDiscountResult<TenantDiscountQuote>> BindQuoteMessageAsync(int quoteId, string tenantBotId,
        long customerId, long chatId, int messageId, CancellationToken token = default) => SqliteOperation.RunAsync(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var quote = await db.TenantDiscountQuotes.SingleOrDefaultAsync(x => x.Id == quoteId && x.TenantBotId == tenantBotId
            && x.CustomerTelegramUserId == customerId && x.ChatId == chatId, ct);
        if (messageId <= 0 || quote == null || quote.State != TenantDiscountQuoteStates.Open || quote.ExpiresAtUtc <= DateTime.UtcNow
            || quote.MessageId.HasValue) return TenantDiscountResult<TenantDiscountQuote>.Fail(TenantDiscountFailure.ChangedQuote);
        quote.MessageId = messageId;
        quote.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return TenantDiscountResult<TenantDiscountQuote>.Ok(quote);
    }, token);

    /// <summary>Moves an open quote to a newly sent message after a failed Telegram edit; old buttons become stale.</summary>
    /// <param name="quoteId">Internal id of the currently bound open quote.</param>
    /// <param name="tenantBotId">Authenticated internal tenant bot id.</param>
    /// <param name="customerId">Original numeric Telegram sender id.</param>
    /// <param name="chatId">Original numeric Telegram chat id.</param>
    /// <param name="previousMessageId">Old bound Telegram message id to tombstone.</param>
    /// <param name="newMessageId">New positive id from a successful Telegram send.</param>
    /// <param name="token">Cancellation of the users.db rebind transaction.</param>
    /// <returns>Newly bound quote or ChangedQuote without changing an admitted order.</returns>
    /// <remarks>Caller must pass the previous bound id and the real id from a successful Telegram send.</remarks>
    public Task<TenantDiscountResult<TenantDiscountQuote>> RebindQuoteMessageAsync(int quoteId, string tenantBotId,
        long customerId, long chatId, int previousMessageId, int newMessageId, CancellationToken token = default) =>
        SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var quote = await db.TenantDiscountQuotes.SingleOrDefaultAsync(x => x.Id == quoteId
                && x.TenantBotId == tenantBotId && x.CustomerTelegramUserId == customerId
                && x.ChatId == chatId && x.MessageId == previousMessageId, ct);
            if (previousMessageId <= 0 || newMessageId <= 0 || newMessageId == previousMessageId
                || quote == null || quote.State != TenantDiscountQuoteStates.Open || quote.ExpiresAtUtc <= DateTime.UtcNow)
                return TenantDiscountResult<TenantDiscountQuote>.Fail(TenantDiscountFailure.ChangedQuote);
            var now = DateTime.UtcNow;
            // Keep an expired marker on the old message so legacy full-price buttons cannot bypass its quote.
            var oldMessage = new TenantDiscountQuote
            {
                TenantBotId = tenantBotId, CustomerTelegramUserId = customerId, ChatId = chatId,
                MessageId = previousMessageId, SelectionKey = quote.SelectionKey, CodeId = quote.CodeId,
                CodeUpdatedAtUtc = quote.CodeUpdatedAtUtc, GrossToman = quote.GrossToman,
                BaseCostToman = quote.BaseCostToman, DiscountAmountToman = quote.DiscountAmountToman,
                NetToman = quote.NetToman, State = TenantDiscountQuoteStates.Expired,
                CreatedAtUtc = quote.CreatedAtUtc, UpdatedAtUtc = now, ExpiresAtUtc = now
            };
            quote.MessageId = newMessageId;
            quote.UpdatedAtUtc = now;
            await db.SaveChangesAsync(ct);
            db.TenantDiscountQuotes.Add(oldMessage);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return TenantDiscountResult<TenantDiscountQuote>.Ok(quote);
        }, token);

    /// <summary>Detects all quote-bearing confirmation messages, including admitted and expired tombstones.</summary>
    /// <param name="tenantBotId">Internal bot id of the current tenant checkout.</param>
    /// <param name="customerId">Telegram sender who saw the original quote.</param>
    /// <param name="chatId">Numeric chat that contains the original quote.</param>
    /// <param name="messageId">Positive Telegram message id being checked.</param>
    /// <param name="token">Cancellation of read-only users.db lookup.</param>
    /// <returns>True for any bound quote on this exact message, including admitted and expired tombstones.</returns>
    /// <remarks>Use this to reject legacy full-price buttons sent on a message that has a discount quote.</remarks>
    public async Task<bool> HasQuoteForMessageAsync(string tenantBotId, long customerId, long chatId,
        int messageId, CancellationToken token = default)
    {
        if (messageId <= 0) return false;
        await using var db = _factory.CreateDbContext();
        return await db.TenantDiscountQuotes.AsNoTracking().AnyAsync(x => x.TenantBotId == tenantBotId
            && x.CustomerTelegramUserId == customerId && x.ChatId == chatId && x.MessageId == messageId, token);
    }

    /// <summary>Expires an unadmitted quote without changing any order or redemption.</summary>
    /// <param name="quoteId">Internal users.db id of the quote to retire.</param>
    /// <param name="tenantBotId">Authenticated tenant storefront bot id.</param>
    /// <param name="customerId">Original numeric Telegram sender id.</param>
    /// <param name="chatId">Original numeric Telegram chat id.</param>
    /// <param name="token">Cancellation of the users.db state transition.</param>
    /// <returns>The expired quote, or ChangedQuote if no matching quote exists; admitted orders remain intact.</returns>
    public Task<TenantDiscountResult<TenantDiscountQuote>> ExpireQuoteAsync(int quoteId, string tenantBotId,
        long customerId, long chatId, CancellationToken token = default) => SqliteOperation.RunAsync(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var quote = await db.TenantDiscountQuotes.SingleOrDefaultAsync(x => x.Id == quoteId
            && x.TenantBotId == tenantBotId && x.CustomerTelegramUserId == customerId && x.ChatId == chatId, ct);
        if (quote == null) return TenantDiscountResult<TenantDiscountQuote>.Fail(TenantDiscountFailure.ChangedQuote);
        if (quote.State == TenantDiscountQuoteStates.Open)
        {
            quote.State = TenantDiscountQuoteStates.Expired;
            quote.UpdatedAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return TenantDiscountResult<TenantDiscountQuote>.Ok(quote);
    }, token);

    /// <summary>Looks up a quote only through its original bot, sender, chat, message and selection identity.</summary>
    /// <param name="quoteId">Internal users.db quote id.</param>
    /// <param name="tenantBotId">Authenticated internal storefront id.</param>
    /// <param name="customerId">Original numeric Telegram sender id.</param>
    /// <param name="chatId">Original numeric Telegram chat id.</param>
    /// <param name="messageId">Bound Telegram message id.</param>
    /// <param name="selectionKey">Original server-stored purchase selection key.</param>
    /// <param name="token">Cancellation of the read-only users.db lookup.</param>
    /// <returns>Detached exact-identity quote or ChangedQuote, never a cross-store quote.</returns>
    public async Task<TenantDiscountResult<TenantDiscountQuote>> GetQuoteAsync(int quoteId, string tenantBotId,
        long customerId, long chatId, int messageId, string selectionKey, CancellationToken token = default)
    {
        await using var db = _factory.CreateDbContext();
        var quote = await db.TenantDiscountQuotes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == quoteId
            && x.TenantBotId == tenantBotId && x.CustomerTelegramUserId == customerId && x.ChatId == chatId
            && x.MessageId == messageId && x.SelectionKey == selectionKey, token);
        return quote == null ? TenantDiscountResult<TenantDiscountQuote>.Fail(TenantDiscountFailure.ChangedQuote)
            : TenantDiscountResult<TenantDiscountQuote>.Ok(quote);
    }

    /// <summary>Records the discount displayed on an existing bound purchase quote, or removes it with a null selection.</summary>
    /// <param name="quoteId">Internal id of an open message-bound purchase quote.</param>
    /// <param name="tenantBotId">Authenticated internal tenant bot id.</param>
    /// <param name="customerId">Original numeric Telegram sender id.</param>
    /// <param name="chatId">Original numeric Telegram chat id.</param>
    /// <param name="messageId">Bound Telegram message id being edited.</param>
    /// <param name="selectionKey">Exact server-side purchase plan key.</param>
    /// <param name="selection">Displayed selected code and price, or null to remove the selected code.</param>
    /// <param name="freshGrossToman">Current undiscounted sale price in whole toman.</param>
    /// <param name="freshBaseCostToman">Current colleague cost in whole toman.</param>
    /// <param name="token">Cancellation of the short users.db transaction.</param>
    /// <returns>Updated unadmitted quote, or a named capacity, revision or tariff failure.</returns>
    /// <remarks>The caller must successfully show this exact quote on Telegram; a failed render must not lead to admission.</remarks>
    public Task<TenantDiscountResult<TenantDiscountQuote>> SelectQuoteCodeAsync(int quoteId, string tenantBotId,
        long customerId, long chatId, int messageId, string selectionKey, TenantDiscountSelection selection,
        long freshGrossToman, long freshBaseCostToman, CancellationToken token = default) => SqliteOperation.RunAsync(async ct =>
    {
        await using var db = _factory.CreateDbContext();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var quote = await db.TenantDiscountQuotes.SingleOrDefaultAsync(x => x.Id == quoteId && x.TenantBotId == tenantBotId
            && x.CustomerTelegramUserId == customerId && x.ChatId == chatId && x.MessageId == messageId
            && x.SelectionKey == selectionKey, ct);
        if (quote == null || quote.State != TenantDiscountQuoteStates.Open || quote.ExpiresAtUtc <= DateTime.UtcNow
            || freshGrossToman != quote.GrossToman || freshBaseCostToman != quote.BaseCostToman)
            return TenantDiscountResult<TenantDiscountQuote>.Fail(TenantDiscountFailure.ChangedQuote);
        if (selection != null)
        {
            var validated = await CheckSelectedAsync(db, tenantBotId, selection, freshGrossToman, freshBaseCostToman,
                TenantDiscountScopes.Purchase, ct);
            if (!validated.Success) return TenantDiscountResult<TenantDiscountQuote>.Fail(validated.Failure);
            quote.CodeId = selection.CodeId;
            quote.CodeUpdatedAtUtc = selection.CodeUpdatedAtUtc;
            quote.DiscountAmountToman = selection.Displayed.DiscountAmountToman;
            quote.NetToman = selection.Displayed.NetToman;
        }
        else
        {
            quote.CodeId = null;
            quote.CodeUpdatedAtUtc = null;
            quote.DiscountAmountToman = 0;
            quote.NetToman = freshGrossToman;
        }
        quote.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return TenantDiscountResult<TenantDiscountQuote>.Ok(quote);
    }, token);

    /// <summary>Atomically admits the one message-bound purchase quote, order and capacity claim; same-method replay returns the original order.</summary>
    /// <param name="quoteId">Stored quote identity selected by the short callback.</param>
    /// <param name="tenantBotId">Authenticated storefront bot id.</param>
    /// <param name="customerId">Authenticated Telegram sender.</param>
    /// <param name="chatId">Actual Telegram checkout chat.</param>
    /// <param name="messageId">Telegram message that displayed this quote.</param>
    /// <param name="selectionKey">Server-stored checkout selection, never a callback-supplied price.</param>
    /// <param name="provider">First chosen compact payment method: HP, TM, UP, AP, NP, CARD or W.</param>
    /// <param name="freshGrossToman">Fresh undiscounted tariff for admission.</param>
    /// <param name="freshBaseCostToman">Fresh colleague base cost for admission.</param>
    /// <param name="newOrder">New unsaved tenant purchase order from the current selection; null only for same-method replay of an already admitted quote.</param>
    /// <param name="token">Cancellation of local users.db work.</param>
    /// <returns>The admitted immutable order or a named, fail-closed quote conflict.</returns>
    /// <remarks>A different provider or released claim cannot reuse this quote. No external I/O runs within the transaction.</remarks>
    /// <example><code>var admitted = await discounts.AdmitQuotedOrderAsync(id, botId, actor, chat, message, key, "HP", gross, cost, newOrder, token);</code></example>
    public Task<TenantDiscountResult<TenantBotOrder>> AdmitQuotedOrderAsync(int quoteId, string tenantBotId,
        long customerId, long chatId, int messageId, string selectionKey, string provider, long freshGrossToman,
        long freshBaseCostToman, TenantBotOrder newOrder, CancellationToken token = default)
    {
        if (newOrder != null && newOrder.Id != 0) return Task.FromResult(TenantDiscountResult<TenantBotOrder>.Fail(TenantDiscountFailure.ChangedQuote));
        return SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var admitted = await AdmitQuotedOrderInTransactionAsync(db, quoteId, tenantBotId, customerId,
                chatId, messageId, selectionKey, provider, freshGrossToman, freshBaseCostToman, newOrder, ct);
            if (admitted.Success) await tx.CommitAsync(ct);
            return admitted;
        }, token);
    }

    /// <summary>Stages the exact quote, order and capacity claim in a caller-owned users.db transaction.</summary>
    /// <param name="db">Shared users.db context with the active admission-key transaction.</param>
    /// <param name="quoteId">Bound quote identity.</param>
    /// <param name="tenantBotId">Authenticated storefront id.</param>
    /// <param name="customerId">Authenticated Telegram sender.</param>
    /// <param name="chatId">Bound checkout chat.</param>
    /// <param name="messageId">Bound Telegram message id.</param>
    /// <param name="selectionKey">Server-stored selection key.</param>
    /// <param name="provider">Compact requested payment method.</param>
    /// <param name="freshGrossToman">Current undiscounted sale tariff.</param>
    /// <param name="freshBaseCostToman">Current colleague base cost.</param>
    /// <param name="newOrder">Unsaved purchase snapshot, or null only when replaying an admitted quote.</param>
    /// <param name="ct">Transaction cancellation token.</param>
    /// <returns>Saved order or a named admission failure; caller commits only on success.</returns>
    /// <remarks>The wallet admission-key row and approval gate must share this transaction; no external wallet reads occur here.</remarks>
    internal async Task<TenantDiscountResult<TenantBotOrder>> AdmitQuotedOrderInTransactionAsync(UserDbContext db,
        int quoteId, string tenantBotId, long customerId, long chatId, int messageId, string selectionKey,
        string provider, long freshGrossToman, long freshBaseCostToman, TenantBotOrder newOrder, CancellationToken ct)
    {
        if (newOrder is { Id: not 0 }) newOrder.Id = 0; // A rolled-back SQLite retry can retain a generated key.
        var persistedProvider = provider switch
        {
            "HP" => "HooshPay", "TM" => "Tetraminator", "UP" => "UniquePay",
            "AP" => "atlaspay", "NP" => "NowPayments", "CARD" => "tenant_card", "W" => "wallet",
            _ => null
        };
        var quote = await db.TenantDiscountQuotes.SingleOrDefaultAsync(x => x.Id == quoteId && x.TenantBotId == tenantBotId
            && x.CustomerTelegramUserId == customerId && x.ChatId == chatId && x.MessageId == messageId
            && x.SelectionKey == selectionKey, ct);
        if (quote == null || persistedProvider == null || messageId <= 0)
            return TenantDiscountResult<TenantBotOrder>.Fail(TenantDiscountFailure.ChangedQuote);
        if (quote.State == TenantDiscountQuoteStates.Admitted && quote.OrderId.HasValue)
        {
            if (quote.SelectedProvider != provider) return TenantDiscountResult<TenantBotOrder>.Fail(TenantDiscountFailure.Conflict);
            // Compact callback codes never become accounting/payment-provider names on persisted orders.
            var existing = await db.TenantBotOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == quote.OrderId.Value
                && x.TenantBotId == tenantBotId && x.CustomerTelegramUserId == customerId
                && x.CustomerChatId == chatId && x.PaymentProvider == persistedProvider, ct);
            if (existing?.DiscountInvoiceAttemptState == "definitive_failed"
                || existing?.PaymentStatus == TenantBotOrderStatuses.DiscountExpired
                || existing?.CustomerWalletState == "definitive_failed")
                return TenantDiscountResult<TenantBotOrder>.Fail(TenantDiscountFailure.Conflict);
            if (existing?.TenantDiscountCodeId != null && (!await db.TenantDiscountRedemptions.AsNoTracking()
                .AnyAsync(x => x.TenantBotOrderId == existing.Id && x.CodeId == existing.TenantDiscountCodeId
                    && x.State != TenantDiscountRedemptionStates.Released, ct)))
                return TenantDiscountResult<TenantBotOrder>.Fail(TenantDiscountFailure.Conflict);
            return existing == null ? TenantDiscountResult<TenantBotOrder>.Fail(TenantDiscountFailure.Conflict)
                : TenantDiscountResult<TenantBotOrder>.Ok(existing);
        }
        if (!ValidOrderIdentity(newOrder, tenantBotId, customerId, chatId, TenantBotOrderKinds.Purchase))
            return TenantDiscountResult<TenantBotOrder>.Fail(TenantDiscountFailure.ChangedQuote);
        if (quote.State != TenantDiscountQuoteStates.Open || quote.ExpiresAtUtc <= DateTime.UtcNow
            || freshGrossToman != quote.GrossToman || freshBaseCostToman != quote.BaseCostToman)
            return TenantDiscountResult<TenantBotOrder>.Fail(TenantDiscountFailure.ChangedQuote);
        TenantDiscountCode code = null;
        TenantDiscountPrice price = new(freshGrossToman, freshBaseCostToman, 0, freshGrossToman);
        if (quote.CodeId.HasValue)
        {
            var selection = new TenantDiscountSelection(quote.CodeId.Value, quote.CodeUpdatedAtUtc ?? DateTime.MinValue,
                new(quote.GrossToman, quote.BaseCostToman, quote.DiscountAmountToman, quote.NetToman));
            var validated = await CheckSelectedAsync(db, tenantBotId, selection, freshGrossToman, freshBaseCostToman,
                TenantDiscountScopes.Purchase, ct, true);
            if (!validated.Success) return TenantDiscountResult<TenantBotOrder>.Fail(validated.Failure);
            code = validated.Value.Code;
            price = validated.Value.Price;
        }
        else if (quote.DiscountAmountToman != 0 || quote.NetToman != freshGrossToman)
            return TenantDiscountResult<TenantBotOrder>.Fail(TenantDiscountFailure.ChangedQuote);
        var result = await InsertOrderAsync(db, newOrder, code, price, persistedProvider, ct);
        if (!result.Success) return result;
        quote.OrderId = result.Value.Id;
        quote.SelectedProvider = provider;
        quote.State = TenantDiscountQuoteStates.Admitted;
        quote.AdmittedAtUtc = DateTime.UtcNow;
        quote.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        return result;
    }

    /// <summary>Atomically inserts a renewal order and its reserved claim after comparing its displayed code and fresh tariff.</summary>
    /// <param name="newOrder">Unsaved, tenant/customer/renewal-target-bound order with a unique confirmation identity.</param>
    /// <param name="selection">Selected code and exact displayed price; null only for an explicitly confirmed gross order.</param>
    /// <param name="freshGrossToman">Fresh undiscounted storefront tariff in whole toman.</param>
    /// <param name="freshBaseCostToman">Fresh colleague cost in whole toman.</param>
    /// <param name="token">Cancellation of the users.db order and reservation transaction.</param>
    /// <returns>Original pending renewal order and claim or a named fail-closed validation failure.</returns>
    /// <remarks>The owner-selected payment method may still be pending; caller chooses an allowed provider at checkout.</remarks>
    public Task<TenantDiscountResult<TenantBotOrder>> AdmitRenewalOrderAsync(TenantBotOrder newOrder,
        TenantDiscountSelection selection, long freshGrossToman, long freshBaseCostToman, CancellationToken token = default)
    {
        var unsavedOrder = newOrder?.Id == 0;
        return SqliteOperation.RunAsync(async ct =>
        {
            if (unsavedOrder) newOrder.Id = 0; // A rolled-back SQLite retry can leave EF's generated key on the input object.
            await using var db = _factory.CreateDbContext();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            if (!unsavedOrder || !ValidOrderIdentity(newOrder, newOrder?.TenantBotId, newOrder?.CustomerTelegramUserId ?? 0,
                    newOrder?.CustomerChatId ?? 0, TenantBotOrderKinds.Renew)
                || freshGrossToman <= 0 || freshBaseCostToman < 0 || freshGrossToman < freshBaseCostToman)
                return TenantDiscountResult<TenantBotOrder>.Fail(TenantDiscountFailure.ChangedQuote);
            // OrderId is generated by the existing tenant order factory and is the renewal confirmation identity.
            var existing = await db.TenantBotOrders.AsNoTracking().SingleOrDefaultAsync(x => x.OrderId == newOrder.OrderId, ct);
            if (existing != null)
                return existing.TenantBotId == newOrder.TenantBotId
                    && existing.CustomerTelegramUserId == newOrder.CustomerTelegramUserId
                    && existing.CustomerChatId == newOrder.CustomerChatId
                    && existing.OrderKind == TenantBotOrderKinds.Renew
                    && existing.ServiceKey == newOrder.ServiceKey
                    && existing.TrafficGb == newOrder.TrafficGb
                    && existing.DurationKey == newOrder.DurationKey
                    && existing.UnlimitedPlanKey == newOrder.UnlimitedPlanKey
                    && existing.TargetAccountEmail == newOrder.TargetAccountEmail
                    && existing.TargetAccountUuid == newOrder.TargetAccountUuid
                    && existing.TenantDiscountCodeId == selection?.CodeId
                    ? TenantDiscountResult<TenantBotOrder>.Ok(existing)
                    : TenantDiscountResult<TenantBotOrder>.Fail(TenantDiscountFailure.Conflict);
            TenantDiscountCode code = null;
            TenantDiscountPrice price = new(freshGrossToman, freshBaseCostToman, 0, freshGrossToman);
            if (selection != null)
            {
                var validated = await CheckSelectedAsync(db, newOrder.TenantBotId, selection, freshGrossToman,
                    freshBaseCostToman, TenantDiscountScopes.Renew, ct, true);
                if (!validated.Success) return TenantDiscountResult<TenantBotOrder>.Fail(validated.Failure);
                code = validated.Value.Code;
                price = validated.Value.Price;
            }
            var admitted = await InsertOrderAsync(db, newOrder, code, price, newOrder.PaymentProvider, ct);
            if (!admitted.Success) return admitted;
            await tx.CommitAsync(ct);
            return admitted;
        }, token);
    }

    /// <summary>Reprices a displayed selection; admission first takes the code writer lock before capacity is counted.</summary>
    /// <param name="db">Users.db context participating in the caller's admission transaction.</param>
    /// <param name="botId">Authenticated internal tenant storefront id.</param>
    /// <param name="selection">Original code revision and displayed price bound to this customer checkout.</param>
    /// <param name="gross">Fresh undiscounted sale in whole toman.</param>
    /// <param name="baseCost">Fresh colleague cost in whole toman.</param>
    /// <param name="scope">Single purchase or renewal order kind.</param>
    /// <param name="token">Cancellation of capacity and code checks.</param>
    /// <param name="lockCode">True at admission to serialize owner edits and competing claims.</param>
    /// <returns>Tracked code and recomputed exact displayed price, or a named eligibility failure.</returns>
    private static async Task<TenantDiscountResult<(TenantDiscountCode Code, TenantDiscountPrice Price)>> CheckSelectedAsync(
        UserDbContext db, string botId, TenantDiscountSelection selection, long gross, long baseCost, string scope,
        CancellationToken token, bool lockCode = false)
    {
        var code = lockCode ? await LockCodeAsync(db, botId, selection.CodeId, token)
            : await db.TenantDiscountCodes.AsNoTracking().SingleOrDefaultAsync(x => x.Id == selection.CodeId && x.TenantBotId == botId, token);
        if (code == null || code.IsDeleted || !code.IsActive)
            return TenantDiscountResult<(TenantDiscountCode, TenantDiscountPrice)>.Fail(TenantDiscountFailure.Disabled);
        if (code.UpdatedAtUtc != selection.CodeUpdatedAtUtc)
            return TenantDiscountResult<(TenantDiscountCode, TenantDiscountPrice)>.Fail(TenantDiscountFailure.ChangedQuote);
        var price = Quote(code, gross, baseCost, scope);
        if (!price.Success) return TenantDiscountResult<(TenantDiscountCode, TenantDiscountPrice)>.Fail(price.Failure);
        if (selection.Displayed != price.Value)
            return TenantDiscountResult<(TenantDiscountCode, TenantDiscountPrice)>.Fail(TenantDiscountFailure.ChangedQuote);
        if (await OccupiedAsync(db, code.Id, token) >= code.MaxUses)
            return TenantDiscountResult<(TenantDiscountCode, TenantDiscountPrice)>.Fail(TenantDiscountFailure.Exhausted);
        return TenantDiscountResult<(TenantDiscountCode, TenantDiscountPrice)>.Ok((code, price.Value));
    }

    /// <summary>Stages the frozen payable order and one reservation within the caller's users.db transaction.</summary>
    /// <param name="db">Shared users.db context inside the admission transaction.</param>
    /// <param name="order">Unsaved authenticated customer order, including tenant owner and account selection.</param>
    /// <param name="code">Locked selected code, or null for an explicitly undiscounted quote.</param>
    /// <param name="price">Fresh gross, base, realized discount and immutable net in whole toman.</param>
    /// <param name="provider">Persisted first payment provider or pending renewal choice.</param>
    /// <param name="token">Cancellation of the users.db insert and claim.</param>
    /// <returns>Tracked inserted order, or a named missing-store/invalid-identity failure.</returns>
    private static async Task<TenantDiscountResult<TenantBotOrder>> InsertOrderAsync(UserDbContext db,
        TenantBotOrder order, TenantDiscountCode code, TenantDiscountPrice price, string provider, CancellationToken token)
    {
        var store = await db.BotInstances.AsNoTracking().SingleOrDefaultAsync(x => x.Id == order.TenantBotId
            && x.Type == BotInstanceTypes.Tenant && x.OwnerTelegramUserId == order.OwnerTelegramUserId, token);
        if (store == null || order.Id != 0 || string.IsNullOrWhiteSpace(order.OrderId) || string.IsNullOrWhiteSpace(provider))
            return TenantDiscountResult<TenantBotOrder>.Fail(TenantDiscountFailure.Conflict);
        order.BaseCostToman = price.BaseCostToman;
        order.SalePriceToman = price.NetToman;
        order.ProfitToman = price.NetToman - price.BaseCostToman;
        order.PaymentProvider = provider;
        if (order.OrderKind == TenantBotOrderKinds.Purchase || code != null)
            order.DiscountInvoiceAttemptState = "none";
        if (code != null)
        {
            order.OriginalSalePriceToman = price.GrossToman;
            order.DiscountAmountToman = price.DiscountAmountToman;
            order.TenantDiscountCodeId = code.Id;
            order.AppliedDiscountCode = code.Code;
        }
        db.TenantBotOrders.Add(order);
        await db.SaveChangesAsync(token);
        if (code != null)
        {
            db.TenantDiscountRedemptions.Add(new TenantDiscountRedemption
            {
                CodeId = code.Id, TenantBotOrderId = order.Id, State = TenantDiscountRedemptionStates.Reserved,
                ReservedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync(token);
        }
        return TenantDiscountResult<TenantBotOrder>.Ok(order);
    }

    /// <summary>Checks unsaved order identity against the authenticated tenant customer and checkout kind.</summary>
    /// <param name="order">Unsaved order built for the authenticated tenant checkout.</param>
    /// <param name="botId">Current internal tenant storefront id.</param>
    /// <param name="customerId">Authenticated numeric Telegram sender id.</param>
    /// <param name="chatId">Original numeric Telegram chat id.</param>
    /// <param name="kind">Purchase or renewal order kind expected by admission.</param>
    /// <returns>True when all durable identity fields match.</returns>
    private static bool ValidOrderIdentity(TenantBotOrder order, string botId, long customerId, long chatId, string kind) =>
        order != null && order.Id == 0 && !string.IsNullOrWhiteSpace(botId) && customerId > 0 && chatId != 0
        && order.TenantBotId == botId && order.CustomerTelegramUserId == customerId && order.CustomerChatId == chatId
        && order.OrderKind == kind && !string.IsNullOrWhiteSpace(order.OrderId);

    /// <summary>Checks exact owner/store authorization, never just matching an owner's Telegram user id.</summary>
    /// <param name="db">Users.db context carrying the exact-store query.</param>
    /// <param name="botId">Selected internal storefront bot id.</param>
    /// <param name="ownerId">Authenticated numeric Telegram owner id.</param>
    /// <param name="token">Cancellation of the users.db query.</param>
    /// <returns>True only when that owner owns this tenant bot.</returns>
    private static async Task<bool> OwnsStoreAsync(UserDbContext db, string botId, long ownerId, CancellationToken token) =>
        ownerId > 0 && await db.BotInstances.AsNoTracking().AnyAsync(x => x.Id == botId
            && x.Type == BotInstanceTypes.Tenant && x.OwnerTelegramUserId == ownerId, token);

    /// <summary>Serializes same-store owner mutations, including creation, without changing the bot revision.</summary>
    /// <param name="db">Users.db context inside the owner mutation transaction.</param>
    /// <param name="botId">Internal selected tenant bot id to lock.</param>
    /// <param name="ownerId">Authenticated Telegram owner id required for the lock.</param>
    /// <param name="token">Cancellation of the SQLite writer lock.</param>
    /// <returns>Tracked owned bot row, or null if ownership changed.</returns>
    private static async Task<BotInstance> LockOwnerStoreAsync(UserDbContext db, string botId, long ownerId, CancellationToken token)
    {
        if (ownerId <= 0 || string.IsNullOrWhiteSpace(botId)) return null;
        var locked = await db.BotInstances.Where(x => x.Id == botId && x.Type == BotInstanceTypes.Tenant
            && x.OwnerTelegramUserId == ownerId).ExecuteUpdateAsync(s => s.SetProperty(x => x.UpdatedAtUtc, x => x.UpdatedAtUtc), token);
        return locked == 0 ? null : await db.BotInstances.SingleAsync(x => x.Id == botId, token);
    }

    /// <summary>Acquires the SQLite writer lock on a code row without altering its configuration revision.</summary>
    /// <param name="db">Users.db context inside the owner or admission transaction.</param>
    /// <param name="botId">Internal tenant storefront id of the code.</param>
    /// <param name="id">Internal users.db code id to serialize.</param>
    /// <param name="token">Cancellation of the SQLite writer lock.</param>
    /// <returns>Tracked exact-store code row, or null if it is missing.</returns>
    private static async Task<TenantDiscountCode> LockCodeAsync(UserDbContext db, string botId, int id, CancellationToken token)
    {
        var locked = await db.TenantDiscountCodes.Where(x => x.Id == id && x.TenantBotId == botId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.MaxUses, x => x.MaxUses), token);
        return locked == 0 ? null : await db.TenantDiscountCodes.SingleAsync(x => x.Id == id, token);
    }

    /// <summary>Counts uses that still occupy capacity; released claims remain stored but do not count.</summary>
    /// <param name="db">Users.db context reading claimed uses.</param>
    /// <param name="codeId">Internal tenant code id whose capacity is checked.</param>
    /// <param name="token">Cancellation of count query.</param>
    /// <returns>Reserved plus consumed uses; released audit records do not count.</returns>
    private static Task<int> OccupiedAsync(UserDbContext db, int codeId, CancellationToken token) =>
        db.TenantDiscountRedemptions.CountAsync(x => x.CodeId == codeId
            && (x.State == TenantDiscountRedemptionStates.Reserved || x.State == TenantDiscountRedemptionStates.Consumed), token);

    /// <summary>Generates a strictly increasing configuration timestamp for repeated owner edits.</summary>
    /// <param name="previous">Previous UTC code or owner revision; null on creation.</param>
    /// <returns>Current UTC timestamp or the next tick if the clock has not advanced.</returns>
    private static DateTime NextRevision(DateTime? previous)
    {
        var now = DateTime.UtcNow;
        return previous.HasValue && now <= previous.Value ? previous.Value.AddTicks(1) : now;
    }

    /// <summary>Accepts only supported persisted owner eligibility scopes.</summary>
    /// <param name="scope">Owner-entered eligibility scope.</param>
    /// <returns>True for purchase, renewal or both.</returns>
    private static bool ValidScope(string scope) => scope == TenantDiscountScopes.Purchase
        || scope == TenantDiscountScopes.Renew || scope == TenantDiscountScopes.Both;
    /// <summary>Accepts only one checkout kind at a time.</summary>
    /// <param name="scope">Requested single checkout kind.</param>
    /// <returns>True for purchase or renewal; both is not a checkout.</returns>
    private static bool ValidCheckoutScope(string scope) => scope == TenantDiscountScopes.Purchase || scope == TenantDiscountScopes.Renew;
    /// <summary>Compares owner scope to an explicit purchase or renewal checkout kind.</summary>
    /// <param name="configured">Owner-selected purchase, renewal or both code scope.</param>
    /// <param name="requested">Current single purchase or renewal checkout kind.</param>
    /// <returns>True only when this code permits this checkout kind.</returns>
    private static bool MatchesScope(string configured, string requested) => ValidCheckoutScope(requested)
        && (configured == requested || configured == TenantDiscountScopes.Both);
}
