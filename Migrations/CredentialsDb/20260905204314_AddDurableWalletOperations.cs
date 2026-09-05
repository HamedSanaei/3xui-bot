using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations.CredentialsDb
{
    /// <inheritdoc />
    public partial class AddDurableWalletOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WalletOperations",
                columns: table => new
                {
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountToman = table.Column<long>(type: "INTEGER", nullable: false),
                    BeforeBalance = table.Column<long>(type: "INTEGER", nullable: false),
                    AfterBalance = table.Column<long>(type: "INTEGER", nullable: false),
                    BotId = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ReconciledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletOperations", x => x.OperationKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalletOperations_ReconciledAtUtc_CreatedAtUtc",
                table: "WalletOperations",
                columns: new[] { "ReconciledAtUtc", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_WalletOperations_TelegramUserId",
                table: "WalletOperations",
                column: "TelegramUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WalletOperations");
        }
    }
}
