using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds durable per-client XUI v3 traffic-reminder cycle and delivery-claim state to users.db.
    /// </summary>
    /// <remarks>
    /// Existing accounts require no backfill. The background worker lazily creates rows from the first successful
    /// complete panel-list scan. The migration does not modify credentials, wallets, payments, ledgers, or XUI data.
    /// </remarks>
    [DbContext(typeof(UserDbContext))]
    [Migration("20260809000000_AddXuiV3VolumeReminderStates")]
    public partial class AddXuiV3VolumeReminderStates : Migration
    {
        /// <summary>
        /// Creates the reminder state table plus unique client identity, lease, recipient, and observation indexes.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder targeting the configured users.db database.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "XuiV3VolumeReminderStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    PanelKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientCreatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    Email = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    BotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    CycleNumber = table.Column<long>(type: "INTEGER", nullable: false),
                    PanelUpdatedAt = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    UsedBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    LastRenewedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    HighestHandledThreshold = table.Column<int>(type: "INTEGER", nullable: false),
                    ClaimedThreshold = table.Column<int>(type: "INTEGER", nullable: true),
                    DeliveryStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    LeaseUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    TelegramMessageId = table.Column<int>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastObservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastDeliveredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_XuiV3VolumeReminderStates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3VolumeReminderStates_BotId_TelegramUserId",
                table: "XuiV3VolumeReminderStates",
                columns: new[] { "BotId", "TelegramUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3VolumeReminderStates_DeliveryStatus_LeaseUntilUtc",
                table: "XuiV3VolumeReminderStates",
                columns: new[] { "DeliveryStatus", "LeaseUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3VolumeReminderStates_LastObservedAtUtc",
                table: "XuiV3VolumeReminderStates",
                column: "LastObservedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3VolumeReminderStates_PanelKey_ClientId",
                table: "XuiV3VolumeReminderStates",
                columns: new[] { "PanelKey", "ClientId" },
                unique: true);
        }

        /// <summary>Removes only the XUI v3 volume-reminder state table from users.db.</summary>
        /// <param name="migrationBuilder">EF Core migration builder targeting users.db during rollback.</param>
        /// <remarks>
        /// Rollback forgets notification deduplication history but leaves every account, bot, payment, and balance
        /// unchanged. Reapplying later initializes new cycle-one rows lazily.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "XuiV3VolumeReminderStates");
        }
    }
}
