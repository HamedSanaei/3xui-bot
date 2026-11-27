using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>Adds disabled-by-default campaign settings and invoice-scoped bonus recovery metadata.</summary>
    /// <remarks>No historical promises, wallet credits, or notifications are created during migration.</remarks>
    public partial class AddWalletChargeCampaign : Migration
    {
        /// <summary>Creates both users.db tables and the due-recovery index without modifying existing payment rows.</summary>
        /// <param name="migrationBuilder">EF builder for the platform users.db schema.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WalletChargeBonuses",
                columns: table => new
                {
                    Provider = table.Column<string>(type: "TEXT", nullable: false),
                    PaymentId = table.Column<int>(type: "INTEGER", nullable: false),
                    BotId = table.Column<string>(type: "TEXT", nullable: true),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Percent = table.Column<int>(type: "INTEGER", nullable: false),
                    PrincipalToman = table.Column<long>(type: "INTEGER", nullable: false),
                    BonusToman = table.Column<long>(type: "INTEGER", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ProvisionalConfirmed = table.Column<bool>(type: "INTEGER", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletChargeBonuses", x => new { x.Provider, x.PaymentId });
                });

            migrationBuilder.CreateTable(
                name: "WalletChargeCampaigns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: true),
                    Percent = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletChargeCampaigns", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalletChargeBonuses_CompletedAtUtc_NextAttemptAtUtc",
                table: "WalletChargeBonuses",
                columns: new[] { "CompletedAtUtc", "NextAttemptAtUtc" });
        }

        /// <summary>Drops campaign settings and promises on explicit downgrade; only use when no offers are outstanding.</summary>
        /// <param name="migrationBuilder">EF builder for the platform users.db schema.</param>
        /// <remarks>Existing credentials.db receipts and ledger entries are not rolled back. Prefer a forward fix.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WalletChargeBonuses");

            migrationBuilder.DropTable(
                name: "WalletChargeCampaigns");
        }
    }
}
