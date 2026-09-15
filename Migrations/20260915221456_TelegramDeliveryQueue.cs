using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>Adds empty bot-scoped output storage without backfilling or replaying historical financial events.</summary>
    /// <remarks>Payloads are private. The status/bot/priority/sequence index supports ordered eligible-head scheduling.</remarks>
    public partial class TelegramDeliveryQueue : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TelegramDeliveryJobs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CompletionKey = table.Column<string>(type: "TEXT", nullable: true),
                    RequiresLiveCaller = table.Column<bool>(type: "INTEGER", nullable: false),
                    BotId = table.Column<string>(type: "TEXT", nullable: false),
                    Kind = table.Column<string>(type: "TEXT", nullable: false),
                    Payload = table.Column<string>(type: "TEXT", nullable: true),
                    Priority = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    NotBeforeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Attempts = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramDeliveryJobs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramDeliveryJobs_Status_BotId_Priority_Id",
                table: "TelegramDeliveryJobs",
                columns: new[] { "Status", "BotId", "Priority", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelegramDeliveryJobs");
        }
    }
}
