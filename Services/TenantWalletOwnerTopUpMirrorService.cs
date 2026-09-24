using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Mirrors a confirmed central tenant-customer wallet top-up into the immutable tenant owner's global bot wallet.
/// </summary>
/// <remarks>
/// Personal tenant card payments never call this service. A deterministic credentials receipt key makes retries,
/// duplicate IPNs, webhook recovery, and process restarts idempotent.
/// </remarks>
public sealed class TenantWalletOwnerTopUpMirrorService
{
    private readonly CredentialsStore _credentials;
    private readonly WalletLedgerService _ledger;
    private readonly UserDbContextFactory _users;
    private readonly ILogger<TenantWalletOwnerTopUpMirrorService> _logger;

    public TenantWalletOwnerTopUpMirrorService(
        CredentialsStore credentials,
        WalletLedgerService ledger,
        UserDbContextFactory users,
        ILogger<TenantWalletOwnerTopUpMirrorService> logger)
    {
        _credentials = credentials;
        _ledger = ledger;
        _users = users;
        _logger = logger;
    }

    /// <summary>
    /// Ensures the tenant owner receives exactly one credit equal to the customer's confirmed central top-up.
    /// </summary>
    public async Task<WalletOperation> EnsureAsync(
        string provider,
        int paymentId,
        string botId,
        string botUsername,
        string botType,
        long? tenantOwnerTelegramUserId,
        long customerTelegramUserId,
        long amountToman,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(botType, BotInstanceTypes.Tenant, StringComparison.OrdinalIgnoreCase))
            return null;
        if (string.IsNullOrWhiteSpace(botId) || amountToman <= 0)
            throw new InvalidOperationException("Tenant wallet top-up is missing storefront attribution.");
        if (tenantOwnerTelegramUserId is not > 0)
        {
            await using var db = _users.CreateDbContext();
            tenantOwnerTelegramUserId = await db.BotInstances.AsNoTracking()
                .Where(x => x.Id == botId && x.Type == BotInstanceTypes.Tenant)
                .Select(x => x.OwnerTelegramUserId)
                .SingleOrDefaultAsync(cancellationToken);
            if (tenantOwnerTelegramUserId is > 0)
                _logger.LogWarning(
                    "Recovered legacy tenant wallet top-up owner attribution from storefront. Provider={Provider} PaymentId={PaymentId} BotId={BotId}",
                    provider, paymentId, botId);
        }
        if (tenantOwnerTelegramUserId is not > 0)
            throw new InvalidOperationException("Tenant wallet top-up is missing owner attribution.");
        if (string.IsNullOrWhiteSpace(provider) || paymentId <= 0 || customerTelegramUserId <= 0)
            throw new InvalidOperationException("Tenant wallet top-up mirror identity is invalid.");

        // Customer and owner use the same canonical credentials wallet store. If they are the same Telegram identity,
        // the customer's original payment credit already is the owner's credit and must never be duplicated.
        if (tenantOwnerTelegramUserId.Value == customerTelegramUserId)
            return null;

        var key = $"tenant-wallet-topup:{provider.ToLowerInvariant()}:{paymentId}:owner-credit";
        // The persisted storefront owner is authoritative even for migrated tenants whose global wallet row
        // has not been materialized yet. Create the zero-balance identity idempotently before applying the mirror.
        await _credentials.AddEmptyUser(tenantOwnerTelegramUserId.Value);
        var receipt = await _credentials.MutateWalletAsync(
            tenantOwnerTelegramUserId.Value,
            amountToman,
            key,
            cancellationToken,
            botId);
        if (receipt == null)
            throw new InvalidOperationException("Tenant owner mirror credit receipt was not created.");

        await _ledger.RecordAsync(
            tenantOwnerTelegramUserId.Value,
            WalletLedgerDirections.Credit,
            amountToman,
            receipt.BeforeBalance,
            receipt.AfterBalance,
            WalletLedgerReasons.TenantWalletTopUpMirror,
            provider: provider,
            referenceType: "tenant-wallet-topup",
            referenceId: paymentId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            description: "Tenant customer central wallet top-up mirrored to storefront owner",
            ownerTelegramUserId: tenantOwnerTelegramUserId,
            counterpartyTelegramUserId: customerTelegramUserId,
            botId: botId,
            botUsername: botUsername,
            botType: BotInstanceTypes.Tenant,
            idempotencyKey: key,
            cancellationToken: cancellationToken);

        _logger.LogInformation(
            "Tenant wallet top-up owner mirror ensured. Provider={Provider} PaymentId={PaymentId} BotId={BotId} Owner={OwnerTelegramUserId} Customer={CustomerTelegramUserId} AmountToman={AmountToman}",
            provider, paymentId, botId, tenantOwnerTelegramUserId.Value, customerTelegramUserId, amountToman);
        return receipt;
    }
}
