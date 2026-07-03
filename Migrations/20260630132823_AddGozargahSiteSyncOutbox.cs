using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds the append-only outbox table used to retry Gozargah website synchronization events from users.db.
    /// </summary>
    /// <remarks>
    /// This migration is scoped to <see cref="UserDbContext"/> only. The table stores tenant-aware identifiers,
    /// local idempotency keys, serialized API payloads, retry state, and website responses so 3x-ui account
    /// operations can finish even when the website API is temporarily unavailable.
    /// </remarks>
    public partial class AddGozargahSiteSyncOutbox : Migration
    {
        /// <summary>
        /// Creates <c>GozargahSiteSyncEvents</c> and indexes the fields used by retry, tenant filtering, and lookup.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder for the users.db schema migration.</param>
        /// <remarks>
        /// The composite index on operation and account identifiers helps avoid duplicate queued events for the
        /// same account lifecycle operation. No table or column in credentials.db is created or changed here.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GozargahSiteSyncEvents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TenantBotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    OwnerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    BuyerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    Email = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    PreviousEmail = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    Uuid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SubId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    SubLink = table.Column<string>(type: "TEXT", nullable: true),
                    Operation = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    SiteOrderId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    RetryCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    RequestJson = table.Column<string>(type: "TEXT", nullable: true),
                    ResponseJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SucceededAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GozargahSiteSyncEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_GozargahSiteSyncEvents_BotId",
                table: "GozargahSiteSyncEvents",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_GozargahSiteSyncEvents_BuyerTelegramUserId",
                table: "GozargahSiteSyncEvents",
                column: "BuyerTelegramUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GozargahSiteSyncEvents_Email",
                table: "GozargahSiteSyncEvents",
                column: "Email");

            migrationBuilder.CreateIndex(
                name: "IX_GozargahSiteSyncEvents_Operation_Email_PreviousEmail_Uuid_SubId",
                table: "GozargahSiteSyncEvents",
                columns: new[] { "Operation", "Email", "PreviousEmail", "Uuid", "SubId" });

            migrationBuilder.CreateIndex(
                name: "IX_GozargahSiteSyncEvents_OwnerTelegramUserId",
                table: "GozargahSiteSyncEvents",
                column: "OwnerTelegramUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GozargahSiteSyncEvents_Status",
                table: "GozargahSiteSyncEvents",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_GozargahSiteSyncEvents_SubId",
                table: "GozargahSiteSyncEvents",
                column: "SubId");

            migrationBuilder.CreateIndex(
                name: "IX_GozargahSiteSyncEvents_TelegramUserId",
                table: "GozargahSiteSyncEvents",
                column: "TelegramUserId");

            migrationBuilder.CreateIndex(
                name: "IX_GozargahSiteSyncEvents_TenantBotId",
                table: "GozargahSiteSyncEvents",
                column: "TenantBotId");

            migrationBuilder.CreateIndex(
                name: "IX_GozargahSiteSyncEvents_Uuid",
                table: "GozargahSiteSyncEvents",
                column: "Uuid");
        }

        /// <summary>
        /// Drops the Gozargah website sync outbox table when the users.db migration is rolled back.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder for the users.db rollback.</param>
        /// <remarks>
        /// Rolling back removes retry history and unsent website sync events. It does not touch credentials.db.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GozargahSiteSyncEvents");
        }
    }
}
