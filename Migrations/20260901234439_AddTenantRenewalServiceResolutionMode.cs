using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds nullable tenant-renewal service-resolution evidence to bot-scoped state and tenant orders.
    /// </summary>
    /// <remarks>
    /// Existing rows remain null and are not backfilled. The migration performs no payment, wallet, provider, Telegram,
    /// XUI, or account operation. Historical unpaid ambiguous orders therefore require a fresh selection, while paid
    /// ambiguous orders remain blocked for support review before any panel mutation.
    /// </remarks>
    public partial class AddTenantRenewalServiceResolutionMode : Migration
    {
        /// <summary>Adds the nullable evidence columns without modifying historical values.</summary>
        /// <param name="migrationBuilder">EF Core schema builder for the tenant users.db database.</param>
        /// <remarks>No data backfill or financial side effect is executed.</remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RenewalServiceResolutionMode",
                table: "TenantBotOrders",
                type: "TEXT",
                maxLength: 48,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RenewalServiceResolutionMode",
                table: "BotUserStates",
                type: "TEXT",
                maxLength: 48,
                nullable: true);
        }

        /// <summary>Removes the tenant-renewal evidence columns when the schema migration is explicitly rolled back.</summary>
        /// <param name="migrationBuilder">EF Core schema builder for the tenant users.db database.</param>
        /// <remarks>Rollback discards only the stored classification evidence; it does not alter orders or balances.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RenewalServiceResolutionMode",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "RenewalServiceResolutionMode",
                table: "BotUserStates");
        }
    }
}
