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
            // Target the migration immediately before TenantCustomerWallet itself instead of counting from
            // the end. Later additive migrations (for example AtlasPay audit/webhook columns) must not weaken or
            // accidentally bypass this destructive-downgrade guard.
            var migrations = users.Database.GetMigrations().ToList();
            var walletMigrationIndex = migrations.IndexOf("20260923083845_TenantCustomerWallet");
            Assert.True(walletMigrationIndex > 0);
            var previous = migrations[walletMigrationIndex - 1];
            await Assert.ThrowsAsync<SqliteException>(() => users.GetService<IMigrator>().MigrateAsync(previous));
        }
        await using var verify = databases.Users.CreateDbContext();
        Assert.Contains("20260923083845_TenantCustomerWallet", await verify.Database.GetAppliedMigrationsAsync());
        Assert.Equal("paid", (await verify.TenantBotOrders.SingleAsync()).CustomerWalletState);
        Assert.NotNull(await wallet.GetWalletOperationAsync(TenantCustomerWalletFunding.DebitKey(order.Id)));
        Assert.Equal(400000, await wallet.GetAccountBalance(123));
    }
}
