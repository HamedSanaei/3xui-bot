using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>Retains the optional second payment route for each existing bot-scoped Tetraminator invoice.</summary>
    /// <remarks>Additive nullable column only: historical links, invoice identities, settlement markers and balances
    /// are untouched. No backfill, provider request or wallet mutation occurs. Retention follows the existing invoice row.</remarks>
    public partial class AddTetraminatorGatewayLink : Migration
    {
        /// <summary>Adds independent storage for gateway_link, initially null for every historical invoice.</summary>
        /// <param name="migrationBuilder">EF migration builder targeting users.db, not the credentials wallet database.</param>
        /// <remarks>No keys, indexes or financial data change; the existing invoice id continues to identify both routes.</remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "GatewayLink",
                table: "TetraminatorPaymentInfos",
                type: "TEXT",
                nullable: true);
        }

        /// <summary>Removes only the optional online-route column when explicitly rolling back this migration.</summary>
        /// <param name="migrationBuilder">EF migration builder targeting users.db.</param>
        /// <remarks>Rollback discards stored gateway URLs but retains the original payment link and all settlement records.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GatewayLink",
                table: "TetraminatorPaymentInfos");
        }
    }
}
