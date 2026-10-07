using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;

/// <summary>Evaluates the exact shared owner's restrictions and payment funding mode, preserving durable debt repayment.</summary>
/// <remarks>
/// All stores owned by the same Telegram user share funding; another owner's wallet is never consulted.
/// Insufficient funding permits customer access through live central gateways but excludes new personal-card payments.
/// Website calls never run in SQLite transactions. Automatic website-to-bot debt repayment and receiver configuration
/// are unchanged; this service does not enforce the tenant's separate manual Enabled switch.
/// </remarks>
public sealed class TenantAccessService
{
    /// <summary>Customer-facing suspension without a support identifier.</summary>
    public const string BlockedMessage = "ربات به علت تخلف مسدود است. به پشتیبانی پیام دهید.";
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
    /// threshold (<c>tenantMinimumSiteWalletToman</c>) used to classify the storefront payment mode.
    /// </param>
    /// <param name="logger">Operational diagnostics containing operation ids, never credentials or payloads.</param>
    public TenantAccessService(UserDbContextFactory factory, CredentialsStore credentials, GozargahSiteSyncService site, IConfiguration configuration, ILogger<TenantAccessService> logger)
    {
        _factory = factory; _credentials = credentials; _site = site;
        _appConfig = configuration.Get<AppConfig>() ?? new AppConfig();
        _logger = logger;
    }

    /// <summary>Preserves automatic website debt repayment and returns only a blocking owner-status restriction.</summary>
    /// <param name="ownerId">Required exact stored storefront owner's global Telegram user id, never the customer's id.</param>
    /// <param name="token">Cancellation for owner admission, database operations and website calls.</param>
    /// <returns>Null for Allowed or InsufficientFunding; otherwise a fixed blocked-owner or unavailable message.</returns>
    /// <exception cref="OperationCanceledException">The execution is cancelled; any reserved transfer remains recoverable.</exception>
    /// <remarks>
    /// Owner blocking takes priority. Funding eligibility remains bot balance &gt; 0 OR usable website balance at
    /// least <see cref="AppConfig.TenantMinimumSiteWalletToman"/>; neither side requires the other to be positive.
    /// Falling below both conditions permits access but requires central payments. Negative bot debt is still repaid
    /// from usable positive website funds up to the smaller of debt and website balance, with durable receipt-backed
    /// local credit and no replay of uncertain debits. Check the tenant's manual Enabled switch separately.
    /// </remarks>
    /// <example><code>var restriction = await access.EvaluateAsync(tenant.OwnerTelegramUserId.Value, token);</code></example>
    public async Task<string> EvaluateAsync(long ownerId, CancellationToken token)
        => (await EvaluateDecisionAsync(ownerId, token)).RestrictionMessage;

    /// <summary>Preserves owner debt repayment and returns owner restrictions or an accessible financial payment mode.</summary>
    /// <param name="ownerId">Required exact stored tenant owner's global Telegram user id; never use a customer or another owner.</param>
    /// <param name="token">Cancellation for owner serialization, database access, and website lookup or repayment.</param>
    /// <returns>A non-null immutable owner-scoped evaluation; both funding modes allow access, but only Allowed retains saved payment preferences.</returns>
    /// <remarks>
    /// Serializes repayment per shared owner, recovers pending receipt-backed credits idempotently, and preserves the
    /// existing website debit up to the smaller of negative bot debt and usable positive site funds. Uncertain debits
    /// are never replayed. Funding remains bot balance &gt; 0 OR usable site balance at least the configured minimum,
    /// including after repayment; insufficient funds classify central-payment fallback rather than suspension.
    /// Missing or blocked owners fail closed before financial work. Manual tenant enablement remains a caller check.
    /// </remarks>
    /// <exception cref="OperationCanceledException">Owner admission, database access, or website work is cancelled; durable transfer recovery remains possible.</exception>
    /// <example><code>var evaluation = await access.EvaluateDecisionAsync(tenant.OwnerTelegramUserId.Value, token);</code></example>
    public async Task<TenantAccessEvaluation> EvaluateDecisionAsync(long ownerId, CancellationToken token)
    {
        using var gate = await Owners.EnterAsync(ownerId.ToString(System.Globalization.CultureInfo.InvariantCulture), token);
        var owner = await _credentials.GetUserStatusWithId(ownerId);
        if (owner?.IsBlocked == true)
            return new(TenantAccessDecision.OwnerBlocked, owner.AccountBalance, null, false, _appConfig.TenantMinimumSiteWalletToman);
        if (owner == null)
            return new(TenantAccessDecision.OwnerMissing, null, null, false, _appConfig.TenantMinimumSiteWalletToman);

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
        if (owner.IsBlocked)
            return new(TenantAccessDecision.OwnerBlocked, owner.AccountBalance, null, false, _appConfig.TenantMinimumSiteWalletToman);
        if (owner.AccountBalance > 0)
            return ClassifyFundingSnapshot(owner.AccountBalance, false, null);

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
                _logger.LogWarning("Owner debt transfer remains pending. TransferId={TransferId} ErrorType={ErrorType}", transfer.Id, ex.GetType().Name);
            }
            site = await ReadSiteAsync(ownerId, token);
        }

        owner = await _credentials.GetUserStatusWithId(ownerId);
        if (owner.IsBlocked)
            return new(TenantAccessDecision.OwnerBlocked, owner.AccountBalance, site.WalletToman, site.CanUse, _appConfig.TenantMinimumSiteWalletToman);
        return ClassifyFundingSnapshot(owner.AccountBalance, site.CanUse, site.CanUse ? (long?)site.WalletToman : null);
    }

    /// <summary>
    /// Evaluates owner restrictions and the existing OR funding rule without financial side effects, for fresh
    /// payment admission and read-only background monitoring of externally funded Gozargah wallets.
    /// </summary>
    /// <param name="ownerId">Required exact stored tenant owner's global Telegram user id, never the customer's or another owner's id.</param>
    /// <param name="token">Cancellation for the credential read and the optional website wallet lookup.</param>
    /// <returns>
    /// A non-null owner-scoped snapshot; InsufficientFunding still permits access but requires central payments.
    /// </returns>
    /// <remarks>
    /// Read-only by construction: it never creates a <see cref="TenantDebtTransfer"/>, never debits the Gozargah
    /// site wallet, never repays debt, and never mutates wallets, orders, payments, or XUI state. A positive local
    /// balance short-circuits before any website lookup. Otherwise only a usable site balance at least the configured
    /// minimum restores saved payment preferences. An unavailable website provides no funding credit; neither mode
    /// overrides missing or blocked owners or the caller's manual tenant Enabled check.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The credential or website lookup is cancelled.</exception>
    /// <example><code>var evaluation = await access.EvaluateFundingSnapshotAsync(tenant.OwnerTelegramUserId.Value, token);</code></example>
    public async Task<TenantAccessEvaluation> EvaluateFundingSnapshotAsync(long ownerId, CancellationToken token)
    {
        var owner = await _credentials.GetUserStatusWithId(ownerId);
        if (owner?.IsBlocked == true)
            return new(TenantAccessDecision.OwnerBlocked, owner.AccountBalance, null, false, _appConfig.TenantMinimumSiteWalletToman);
        if (owner == null)
            return new(TenantAccessDecision.OwnerMissing, null, null, false, _appConfig.TenantMinimumSiteWalletToman);
        if (owner.AccountBalance > 0)
            return ClassifyFundingSnapshot(owner.AccountBalance, false, null);
        var site = await ReadSiteAsync(ownerId, token);
        return ClassifyFundingSnapshot(owner.AccountBalance, site.CanUse, site.CanUse ? (long?)site.WalletToman : null);
    }

    /// <summary>Classifies observed funding using the unchanged OR rule, independently of customer access permission.</summary>
    /// <param name="botBalanceToman">The exact tenant owner's shared bot balance in toman; zero and negative values do not satisfy local funding.</param>
    /// <param name="siteWalletUsable">Whether this owner's observed website wallet is eligible for funding.</param>
    /// <param name="siteWalletToman">Observed website balance in toman, or null when unavailable; ignored when unusable.</param>
    /// <returns>Allowed for bot balance &gt; 0 OR usable site balance at least the configured minimum; otherwise accessible InsufficientFunding.</returns>
    /// <remarks>
    /// Pure classification with no website requests or wallet mutation. The caller must first exclude missing and
    /// blocked owners and must not mix balances from different owners. Manual tenant enablement is checked elsewhere.
    /// </remarks>
    /// <example><code>var evaluation = access.ClassifyFundingSnapshot(owner.AccountBalance, site.CanUse, site.WalletToman);</code></example>
    internal TenantAccessEvaluation ClassifyFundingSnapshot(long botBalanceToman, bool siteWalletUsable, long? siteWalletToman)
    {
        var allowed = botBalanceToman > 0 ||
                      (siteWalletUsable && siteWalletToman.HasValue && siteWalletToman.Value >= _appConfig.TenantMinimumSiteWalletToman);
        return new TenantAccessEvaluation(
            allowed ? TenantAccessDecision.Allowed : TenantAccessDecision.InsufficientFunding,
            botBalanceToman,
            siteWalletUsable ? siteWalletToman : null,
            siteWalletUsable,
            _appConfig.TenantMinimumSiteWalletToman);
    }
    /// <summary>Reads usable website funds without interpreting connectivity failures as credit.</summary>
    /// <param name="ownerId">Global owner Telegram id.</param>
    /// <param name="token">Execution cancellation, which is not swallowed.</param>
    /// <returns>Fresh eligibility or unavailable; only CanUse authorizes website funding.</returns>
    /// <remarks>Website unavailability leaves only the local wallet to satisfy financial eligibility; otherwise an unblocked owner uses central-payment fallback.</remarks>
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
