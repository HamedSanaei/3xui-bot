using Adminbot.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Telegram.Bot.Types.Enums;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>
    /// Proves that storefront mandatory join is an admission policy for new customer-wallet work and never a
    /// settlement or recovery policy: background repair of an already-committed debit completes while the
    /// customer's membership is definitively lost.
    /// </summary>
    /// <returns>A task completing after one recovery scan plus its idempotency replay.</returns>
    /// <remarks>
    /// Regression guard for routing customer-wallet actions ahead of the shared message/callback forced-join gates.
    /// The credentials.db debit receipt is the financial source of truth and is simulated as already committed while
    /// the users.db ledger row and paid marker are still missing, exactly as a crash between the two databases leaves
    /// it. Recovery must reconstruct that state without ever calling <c>GetChatMember</c>, because a customer who
    /// already paid must not lose settlement when the storefront's join policy changes afterwards. The second scan
    /// proves the repair is idempotent, so no second debit or duplicate ledger entry can appear.
    /// </remarks>
    [Fact]
    public async Task TenantCustomerWallet_Background_debit_recovery_is_unaffected_by_join_state()
    {
        using var databases = new Databases();
        var (wallet, _, order) = await SeedCustomerWalletOrderAsync(databases);
        await using (var db = databases.Users.CreateDbContext())
        {
            var store = await db.BotInstances.SingleAsync();
            store.TenantMandatoryJoinEnabled = true;
            store.TenantChannelIdsJson = JsonConvert.SerializeObject(new[] { "@required" });
            await db.SaveChangesAsync();
        }

        // Commit only the credentials.db debit. The users.db ledger row and paid marker are deliberately absent so the
        // recovery worker has to finish the second half of the two-database saga from the immutable receipt alone.
        var debitKey = TenantCustomerWalletFunding.DebitKey(order.Id);
        Assert.NotNull(await wallet.TryDebitWalletIfSufficientAsync(
            order.CustomerTelegramUserId, order.SalePriceToman, debitKey, order.TenantBotId));
        Assert.Equal(400_000, await wallet.GetAccountBalance(123));

        // The normal caller finishes well inside a minute; recovery only adopts receipts an interrupted caller left.
        // Only the order debit is aged, so the fixture's own wallet funding stays with its active caller.
        await using (var credentials = databases.Credentials.CreateDbContext())
            await credentials.WalletOperations
                .Where(x => x.OperationKey == debitKey)
                .ExecuteUpdateAsync(
                    set => set.SetProperty(x => x.CreatedAtUtc, DateTime.UtcNow.AddMinutes(-2)));

        // Membership is lost for the whole recovery scan. Any membership probe would be visible on this client, so a
        // zero count proves the join policy was never consulted for settlement or recovery work.
        var lostMembership = new MembershipProbeClient(ChatMemberStatus.Left);
        var botContext = new BotContextAccessor();
        using (botContext.Push(new BotRuntimeContext
               {
                   Config = new BotInstanceConfig { Id = "tenant-a", Type = BotInstanceTypes.Tenant },
                   Client = lostMembership
               }))
        {
            var recovery = new WalletOperationReconciliationService(
                databases.Credentials,
                databases.Users,
                new WalletLedgerService(databases.Users, wallet),
                NullLogger<WalletOperationReconciliationService>.Instance);

            Assert.Equal(1, await recovery.ReconcileAsync());
            Assert.Equal(0, await recovery.ReconcileAsync());
        }

        Assert.Equal(0, lostMembership.GetChatMemberCalls);
        Assert.Equal(400_000, await wallet.GetAccountBalance(123));

        await using var verify = databases.Users.CreateDbContext();
        Assert.Equal("paid", (await verify.TenantBotOrders.SingleAsync(x => x.Id == order.Id)).CustomerWalletState);
        Assert.Single(await verify.WalletLedgerEntries
            .Where(x => x.IdempotencyKey == debitKey && x.Direction == WalletLedgerDirections.Debit).ToListAsync());
    }
}
