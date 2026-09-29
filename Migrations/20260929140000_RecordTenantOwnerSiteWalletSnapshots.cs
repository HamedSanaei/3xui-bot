using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds durable, display-only Gozargah website-wallet observations to tenant fulfillment receipts.
    /// </summary>
    /// <remarks>
    /// The columns belong to users.db tenant orders, not to the credentials wallet. Existing orders retain false
    /// and null values, so historical payment-audit recovery labels website balances as unrecorded. The migration
    /// does not contact Gozargah, alter any wallet or payment, enqueue an audit, or backfill guessed balances.
    /// </remarks>
    [DbContext(typeof(UserDbContext))]
    [Migration("20260929140000_RecordTenantOwnerSiteWalletSnapshots")]
    public partial class RecordTenantOwnerSiteWalletSnapshots : Migration
    {
        /// <summary>Adds the recorded marker and nullable before/after amounts without modifying existing orders.</summary>
        /// <param name="migrationBuilder">Schema builder for users.db; the amounts are whole Iranian toman.</param>
        /// <remarks>False means no historical observation exists; true with a null amount means the live read was unavailable.</remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "OwnerSiteWalletSnapshotRecorded",
                table: "TenantBotOrders",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "OwnerSiteBalanceBefore",
                table: "TenantBotOrders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OwnerSiteBalanceAfter",
                table: "TenantBotOrders",
                type: "INTEGER",
                nullable: true);
        }

        /// <summary>Removes only the website-wallet observation columns on explicit rollback.</summary>
        /// <param name="migrationBuilder">Schema builder for users.db; payment and bot-wallet receipts remain intact.</param>
        /// <remarks>
        /// Explicit rollback discards website-wallet audit observations and should not be used after production
        /// capture. Raw SQLite DROP COLUMN avoids EF's unsupported DropColumnOperation during a downgrade; it does
        /// not touch the bot wallet, provider payment, owner ledger, or older financial downgrade guards.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"TenantBotOrders\" DROP COLUMN \"OwnerSiteWalletSnapshotRecorded\";");
            migrationBuilder.Sql("ALTER TABLE \"TenantBotOrders\" DROP COLUMN \"OwnerSiteBalanceBefore\";");
            migrationBuilder.Sql("ALTER TABLE \"TenantBotOrders\" DROP COLUMN \"OwnerSiteBalanceAfter\";");
        }
    }
}
