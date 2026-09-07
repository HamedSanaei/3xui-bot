using Adminbot.Domain;
using Adminbot.Utils;
using Microsoft.EntityFrameworkCore;

/// <summary>Owner-scoped admission and durable receipts for non-idempotent website debits.</summary>
/// <remarks>One polling process is required. The owner gate covers fresh eligibility and debit; no SQLite write transaction spans network I/O.
/// XUI provisioning happens before admission.</remarks>
public sealed class SiteWalletDebitStore
{
    private readonly UserDbContextFactory _factory;
    private static readonly AsyncKeyedGate Owners = new();

    /// <summary>Creates an operation store without keeping an EF context alive.</summary>
    /// <param name="factory">Shared users.db context factory.</param>
    public SiteWalletDebitStore(UserDbContextFactory factory) => _factory = factory;

    /// <summary>Debits at most once for a business event, using the same owner's website account across all bots.</summary>
    /// <param name="key">Stable business event key, including reference type/id; never a token.</param>
    /// <param name="ownerId">Positive Telegram id of the shared website wallet owner.</param>
    /// <param name="amount">Positive debit amount in toman.</param>
    /// <param name="eligible">Fresh owner balance/eligibility read, executed within owner admission and outside transactions.</param>
    /// <param name="debit">Single non-retried remote mutation, executed after the durable sending marker.</param>
    /// <param name="token">Cancellation for admission, database work and external calls.</param>
    /// <returns>Committed website receipt, or safe failure when eligibility prevents any debit attempt.</returns>
    /// <exception cref="SiteWalletDebitUncertainException">A debit was attempted without a committed successful receipt; no fallback is permitted.</exception>
    /// <exception cref="InvalidOperationException">A key was reused with conflicting owner or amount.</exception>
    /// <remarks>A remote failure is conservatively uncertain. Restart reads sending without issuing another POST. No transaction spans the website and SQLite.</remarks>
    /// <example><code>await store.ExecuteAsync(key, ownerId, amount, ct =&gt; CheckAsync(ct), ct =&gt; DebitAsync(ct), token);</code></example>
    public async Task<GozargahSiteWalletDebitResult> ExecuteAsync(string key, long ownerId, long amount,
        Func<CancellationToken, Task<GozargahSiteWalletEligibility>> eligible,
        Func<CancellationToken, Task<GozargahSiteWalletDebitResult>> debit, CancellationToken token)
    {
        if (ownerId <= 0 || amount <= 0 || string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Invalid website debit identity or amount.");
        using var gate = await Owners.EnterAsync(ownerId.ToString(System.Globalization.CultureInfo.InvariantCulture), token);
        await using (var db = _factory.CreateDbContext())
        {
            var receipt = await db.Set<SiteWalletDebitOperation>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == key, token);
            if (receipt != null)
            {
                if (receipt.OwnerTelegramUserId != ownerId || receipt.AmountToman != amount) throw new InvalidOperationException("Conflicting website debit key.");
                if (receipt.Status != "applied") throw new SiteWalletDebitUncertainException(key);
                return GozargahSiteWalletDebitResult.Applied(receipt.BeforeBalance, receipt.AfterBalance);
            }
        }
        var eligibility = await eligible(token);
        if (!eligibility.CanUse) return GozargahSiteWalletDebitResult.Failed(eligibility.Message);
        await SqliteOperation.RunAsync(async ct =>
        {
            await using var db = _factory.CreateDbContext();
            db.Add(new SiteWalletDebitOperation { Id = key, OwnerTelegramUserId = ownerId, AmountToman = amount });
            return await db.SaveChangesAsync(ct);
        }, token);
        try
        {
            var result = await debit(token);
            if (!result.Success || (decimal)result.BeforeWallet - result.AfterWallet != amount)
                throw new SiteWalletDebitUncertainException(key);
            await SqliteOperation.RunAsync(async ct =>
            {
                await using var db = _factory.CreateDbContext();
                var row = await db.Set<SiteWalletDebitOperation>().SingleAsync(x => x.Id == key, ct);
                row.Status = "applied"; row.BeforeBalance = result.BeforeWallet; row.AfterBalance = result.AfterWallet;
                return await db.SaveChangesAsync(ct);
            }, token);
            return result;
        }
        catch (Exception ex) when (ex is not SiteWalletDebitUncertainException)
        {
            // Even cancellation may follow an applied remote debit. Preserve sending, and prohibit compensation.
            throw new SiteWalletDebitUncertainException(key);
        }
    }
}
