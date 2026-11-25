using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Adminbot.Domain;
using Xunit;

public sealed partial class ConcurrencyTests
{
    /// <summary>Production migrations retain wallet evidence and refuse a downgrade that would discard an admitted sale.</summary>
    /// <returns>A task completing after migrations and a rejected downgrade against disposable real SQLite databases.</returns>
    /// <remarks>The financial downgrade guard is exercised before the unrelated irreversible logger cutover.
    /// The fixture seeds historical paid evidence without running current storefront admission, which requires tables
    /// introduced after that boundary. The final upgrade must preserve the paid order, wallet debit receipt and balance.</remarks>
    [Fact]
    public async Task TenantCustomerWallet_Migration_refuses_to_erase_financial_history()
    {
        using var databases = new Databases(initialize: false);
        // Isolate the existing financial downgrade guard before the unrelated logger cutover guard.
        // Apply the complete current upgrade after refusal and verify the same financial evidence.
        await using (var users = databases.Users.CreateDbContext())
            await users.GetService<IMigrator>().MigrateAsync("20261009120000_AddTelegramEndpointRouting");
        await using (var credentials = databases.Credentials.CreateDbContext()) await credentials.Database.MigrateAsync();
        var wallet = new CredentialsStore(databases.Credentials);
        await wallet.AddEmptyUser(123);
        await wallet.MutateWalletAsync(123, 500_000, "fixture:fund");
        var order = new TenantBotOrder
        {
            OrderId = "historical-wallet-order",
            TenantBotId = "tenant-a",
            OwnerTelegramUserId = 456,
            CustomerTelegramUserId = 123,
            CustomerChatId = 123,
            ServiceKey = "normal",
            SalePriceToman = 100_000,
            BaseCostToman = 80_000,
            ProfitToman = 20_000,
            PaymentProvider = "wallet",
            PaymentStatus = TenantBotOrderStatuses.Paid,
            CustomerWalletAdmissionKey = "historical-wallet-admission",
            CustomerWalletState = "paid",
            PaidAtUtc = DateTime.UtcNow
        };
        // Model already-committed historical facts, not a new sale against a pre-blocklist schema.
        await using (var users = databases.Users.CreateDbContext())
        {
            users.TenantBotOrders.Add(order);
            await users.SaveChangesAsync();
        }
        var debitKey = TenantCustomerWalletFunding.DebitKey(order.Id);
        Assert.NotNull(await wallet.TryDebitWalletIfSufficientAsync(123, order.SalePriceToman, debitKey, order.TenantBotId));
        await using (var users = databases.Users.CreateDbContext())
        {
            // Target the migration immediately before TenantCustomerWallet itself instead of counting from
            // the end. Later additive migrations (for example AtlasPay audit/webhook columns) must not weaken or
            // accidentally bypass this destructive-downgrade guard.
            var migrations = users.Database.GetMigrations().ToList();
            var walletMigrationIndex = migrations.IndexOf("20260923083845_TenantCustomerWallet");
            Assert.True(walletMigrationIndex > 0);
            var previous = migrations[walletMigrationIndex - 1];
            var failure = await Assert.ThrowsAsync<SqliteException>(() => users.GetService<IMigrator>().MigrateAsync(previous));
            Assert.Equal(275, failure.SqliteExtendedErrorCode); // SQLITE_CONSTRAINT_CHECK from the wallet downgrade guard.
            Assert.Contains("Safe = 1", failure.Message);
        }
        await using var verify = databases.Users.CreateDbContext();
        Assert.Contains("20260923083845_TenantCustomerWallet", await verify.Database.GetAppliedMigrationsAsync());
        // The attempted downgrade can remove later audit-only columns before the older financial guard rejects it.
        // Read the protected historical value without materializing today's full TenantBotOrder model.
        Assert.Equal("paid", await verify.Database.SqlQueryRaw<string>(
            "SELECT \"CustomerWalletState\" AS \"Value\" FROM \"TenantBotOrders\"").SingleAsync());
        Assert.NotNull(await wallet.GetWalletOperationAsync(debitKey));
        Assert.Equal(400000, await wallet.GetAccountBalance(123));
        await verify.Database.MigrateAsync();
        Assert.Equal("paid", await verify.Database.SqlQueryRaw<string>(
            "SELECT \"CustomerWalletState\" AS \"Value\" FROM \"TenantBotOrders\"").SingleAsync());
        Assert.NotNull(await wallet.GetWalletOperationAsync(debitKey));
        Assert.Equal(400000, await wallet.GetAccountBalance(123));
    }
}
