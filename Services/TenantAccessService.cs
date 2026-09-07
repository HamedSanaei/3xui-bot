using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;

/// <summary>Evaluates all storefronts against their shared owner's suspension and funding, with durable debt repayment.</summary>
/// <remarks>One polling process. Website calls never run in SQLite transactions. Receiver configuration is not changed.</remarks>
public sealed class TenantAccessService
{
    /// <summary>Customer-facing suspension without a support identifier.</summary>
    public const string BlockedMessage = "ربات به علت تخلف مسدود است. به پشتیبانی پیام دهید.";
    /// <summary>Customer-facing insufficient owner funding without a support identifier.</summary>
    public const string DebtMessage = "ربات به علت بدهی غیرفعال است. به پشتیبانی پیام دهید.";
    private static readonly AsyncKeyedGate Owners = new();
    private readonly UserDbContextFactory _factory;
    private readonly CredentialsStore _credentials;
    private readonly GozargahSiteSyncService _site;
    private readonly AppConfig _appConfig;
    private readonly ILogger<TenantAccessService> _logger;

    /// <summary>Creates a stateless evaluator using short-lived database contexts.</summary>
    /// <param name="factory">Shared users.db factory for transfer intent and website receipts.</param>
    /// <param name="credentials">Shared owner profile and atomic bot wallet receipt store.</param>
    /// <param name="site">Website integration using owner-wide debit admission.</param>
    /// <param name="configuration">
    /// Runtime configuration bound from <c>Data/configuration.json</c>; supplies the configurable minimum site-wallet
    /// threshold (<c>tenantMinimumSiteWalletToman</c>) used by the storefront access gate.
    /// </param>
    /// <param name="logger">Operational diagnostics containing operation ids, never credentials or payloads.</param>
    public TenantAccessService(UserDbContextFactory factory, CredentialsStore credentials, GozargahSiteSyncService site, IConfiguration configuration, ILogger<TenantAccessService> logger)
    {
        _factory = factory; _credentials = credentials; _site = site;
        _appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
        _logger = logger;
    }

    /// <summary>Repays available debt and returns the current customer access restriction.</summary>
    /// <param name="ownerId">Required storefront owner's Telegram id, never the customer's id.</param>
    /// <param name="token">Cancellation for owner admission, database operations and website calls.</param>
    /// <returns>Null when allowed, otherwise the fixed violation or debt message.</returns>
    /// <exception cref="OperationCanceledException">The execution is cancelled; any reserved transfer remains recoverable.</exception>
    /// <remarks>Violation takes priority. Uncertain debits are not replayed. A positive local balance permits access;
    /// otherwise a readable usable website balance at or above the configured minimum site-wallet threshold
    /// (<see cref="AppConfig.TenantMinimumSiteWalletToman"/>, key <c>tenantMinimumSiteWalletToman</c>) is required.</remarks>
    /// <example><code>var restriction = await access.EvaluateAsync(tenant.OwnerTelegramUserId.Value, token);</code></example>
    public async Task<string> EvaluateAsync(long ownerId, CancellationToken token)
    {
        using var gate = await Owners.EnterAsync(ownerId.ToString(System.Globalization.CultureInfo.InvariantCulture), token);
        var owner = await _credentials.GetUserStatusWithId(ownerId);
        if (owner?.IsBlocked == true) return BlockedMessage;
        if (owner == null) return DebtMessage;

        TenantDebtTransfer pending;
        await using (var db = _factory.CreateDbContext())
            pending = await db.Set<TenantDebtTransfer>().AsNoTracking()
                .SingleOrDefaultAsync(x => x.OwnerTelegramUserId == ownerId && x.Status == "pending", token);
        if (pending != null)
        {
            try { await RecoverAsync(_factory, _credentials, pending, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning("Owner debt credit requires recovery. TransferId={TransferId} ErrorType={ErrorType}", pending.Id, ex.GetType().Name);
            }
        }

        owner = await _credentials.GetUserStatusWithId(ownerId);
        if (owner.IsBlocked) return BlockedMessage;
        if (owner.AccountBalance > 0) return null;
        var site = await ReadSiteAsync(ownerId, token);
        if (pending == null && owner.AccountBalance < 0 && site.CanUse && site.WalletToman > 0)
        {
            var amount = (long)Math.Min(-(decimal)owner.AccountBalance, site.WalletToman);
            var transfer = new TenantDebtTransfer { Id = Guid.NewGuid().ToString("N"), OwnerTelegramUserId = ownerId, AmountToman = amount };
            await SqliteOperation.RunAsync(async ct =>
            {
                await using var db = _factory.CreateDbContext();
                db.Add(transfer);
                return await db.SaveChangesAsync(ct);
            }, token);
            try
            {
                // Existing website admission serializes this debit with purchases across every bot.
                var result = await _site.DeductSiteWalletAfterPanelSuccessAsync(ownerId, amount,
                    "tenant-debt", transfer.Id, "Owner debt repayment", token, async ct =>
                    {
                        var fresh = await _credentials.GetUserStatusWithId(ownerId);
                        return fresh != null && !fresh.IsBlocked && fresh.AccountBalance < 0
                            && -(decimal)fresh.AccountBalance >= amount;
                    });
                if (result.Success) await RecoverAsync(_factory, _credentials, transfer, token);
                else await SetStatusAsync(_factory, transfer.Id, "cancelled", token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Keep pending even when the local credit commit failed. Recovery requires the saved website receipt.
                _logger.LogWarning("Owner debt transfer remains pending. TransferId={TransferId} ErrorType={ErrorType}", transfer.Id, ex.GetType().Name);
            }
            site = await ReadSiteAsync(ownerId, token);
        }
        owner = await _credentials.GetUserStatusWithId(ownerId);
        if (owner.IsBlocked) return BlockedMessage;
        // The website threshold is configurable: tenantMinimumSiteWalletToman decides the minimum usable wallet
        // that keeps a storefront active when the owner's local bot wallet is empty or negative.
        return owner.AccountBalance > 0 || (site.CanUse && site.WalletToman >= _appConfig.TenantMinimumSiteWalletToman) ? null : DebtMessage;
    }

    /// <summary>Reads usable website funds without interpreting connectivity failures as credit.</summary>
    /// <param name="ownerId">Global owner Telegram id.</param>
    /// <param name="token">Execution cancellation, which is not swallowed.</param>
    /// <returns>Fresh eligibility or unavailable; only CanUse authorizes website funding.</returns>
    /// <remarks>When the website is unavailable, access depends on the local wallet only.</remarks>
    private async Task<GozargahSiteWalletEligibility> ReadSiteAsync(long ownerId, CancellationToken token)
    {
        try { return await _site.CheckSiteWalletEligibilityAsync(ownerId, 0, token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return GozargahSiteWalletEligibility.Unavailable("Website unavailable"); }
    }

    /// <summary>Completes only the local credit backed by a persisted, matching successful website debit.</summary>
    /// <param name="factory">Users.db factory holding transfer and website receipt.</param>
    /// <param name="credentials">Atomic local wallet writer.</param>
    /// <param name="transfer">Detached immutable transfer reservation.</param>
    /// <param name="token">Database cancellation.</param>
    /// <returns>Completion task; missing or uncertain website receipts remain pending.</returns>
    /// <remarks>Safe after restart or concurrent recovery: the same credit key cannot apply twice. No network calls.
    /// The existing wallet reconciliation worker derives the ledger from the committed local receipt.</remarks>
    internal static async Task RecoverAsync(UserDbContextFactory factory, CredentialsStore credentials, TenantDebtTransfer transfer, CancellationToken token)
    {
        await using var db = factory.CreateDbContext();
        var receipt = await db.Set<SiteWalletDebitOperation>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == $"site:{transfer.OwnerTelegramUserId}:tenant-debt:{transfer.Id}", token);
        if (receipt?.Status != "applied") return;
        if (receipt.OwnerTelegramUserId != transfer.OwnerTelegramUserId || receipt.AmountToman != transfer.AmountToman
            || (decimal)receipt.BeforeBalance - receipt.AfterBalance != transfer.AmountToman)
            throw new InvalidOperationException("Debt transfer receipt conflict.");
        await credentials.MutateWalletAsync(transfer.OwnerTelegramUserId, transfer.AmountToman,
            $"tenant-debt:{transfer.Id}:credit", token, botId: "owner-wallet");
        await SetStatusAsync(factory, transfer.Id, "completed", token);
    }

    /// <summary>Persists terminal transfer status using a fresh local transaction.</summary>
    /// <param name="factory">Users.db factory.</param>
    /// <param name="id">Immutable transfer id.</param>
    /// <param name="status">Completed after credit, or cancelled only before a remote attempt.</param>
    /// <param name="token">Database cancellation.</param>
    /// <returns>Task completing the status update.</returns>
    private static Task SetStatusAsync(UserDbContextFactory factory, string id, string status, CancellationToken token) =>
        SqliteOperation.RunAsync(async ct =>
        {
            await using var db = factory.CreateDbContext();
            return await db.Set<TenantDebtTransfer>().Where(x => x.Id == id && x.Status == "pending")
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, status), ct);
        }, token);
}
