using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class AddDurableTelegramUpdateInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelegramUpdateInbox",
                columns: table => new
                {
                    Sequence = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BotId = table.Column<string>(type: "TEXT", nullable: false),
                    UpdateId = table.Column<int>(type: "INTEGER", nullable: false),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdateType = table.Column<string>(type: "TEXT", nullable: true),
                    Payload = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    AcceptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FailureCode = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramUpdateInbox", x => x.Sequence);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramUpdateInbox_BotId_TelegramUserId_Sequence",
                table: "TelegramUpdateInbox",
                columns: new[] { "BotId", "TelegramUserId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramUpdateInbox_BotId_UpdateId",
                table: "TelegramUpdateInbox",
                columns: new[] { "BotId", "UpdateId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramUpdateInbox_Status_Sequence",
                table: "TelegramUpdateInbox",
                columns: new[] { "Status", "Sequence" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelegramUpdateInbox");
        }
    }
}
