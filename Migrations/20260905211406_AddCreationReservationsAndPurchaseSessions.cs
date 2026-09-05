using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class AddCreationReservationsAndPurchaseSessions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PurchaseSessionId",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PurchaseSessionId",
                table: "BotUserStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "XuiV3CreationOperations",
                columns: table => new
                {
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    PanelKey = table.Column<string>(type: "TEXT", nullable: true),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ClientJson = table.Column<string>(type: "TEXT", nullable: true),
                    InboundIdsJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AppliedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_XuiV3CreationOperations", x => x.OperationKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3CreationOperations_TelegramUserId_CreatedAtUtc",
                table: "XuiV3CreationOperations",
                columns: new[] { "TelegramUserId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "XuiV3CreationOperations");

            migrationBuilder.DropColumn(
                name: "PurchaseSessionId",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PurchaseSessionId",
                table: "BotUserStates");
        }
    }
}
