using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>Production migrations retain wallet evidence and refuse a downgrade that would discard an admitted sale.</summary>
    /// <returns>A task completing after migrations and a rejected downgrade against disposable real SQLite databases.</returns>
    [Fact]
    public async Task TenantCustomerWallet_Migration_refuses_to_erase_financial_history()
    {
        using var databases = new Databases(initialize: false);
        await using (var users = databases.Users.CreateDbContext()) await users.Database.MigrateAsync();
        await using (var credentials = databases.Credentials.CreateDbContext()) await credentials.Database.MigrateAsync();
        var (wallet, funding, order) = await SeedCustomerWalletOrderAsync(databases);
        Assert.True(await funding.DebitAsync(order));
        await using (var users = databases.Users.CreateDbContext())
        {
            // Step back past the additive BotFather-delivery binding migration so the guarded wallet migration
            // itself is asked to downgrade. Its financial-history guard must still refuse the destructive rollback.
            var previous = users.Database.GetMigrations().SkipLast(2).Last();
            await Assert.ThrowsAsync<SqliteException>(() => users.GetService<IMigrator>().MigrateAsync(previous));
        }
        await using var verify = databases.Users.CreateDbContext();
        Assert.Contains("20260923083845_TenantCustomerWallet", await verify.Database.GetAppliedMigrationsAsync());
        Assert.Equal("paid", (await verify.TenantBotOrders.SingleAsync()).CustomerWalletState);
        Assert.NotNull(await wallet.GetWalletOperationAsync(TenantCustomerWalletFunding.DebitKey(order.Id)));
        Assert.Equal(400000, await wallet.GetAccountBalance(123));
    }
}
