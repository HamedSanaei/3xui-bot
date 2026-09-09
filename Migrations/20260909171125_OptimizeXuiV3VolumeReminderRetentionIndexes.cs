using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeXuiV3VolumeReminderRetentionIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_XuiV3VolumeReminderStates_BotId_TelegramUserId",
                table: "XuiV3VolumeReminderStates");

            migrationBuilder.DropIndex(
                name: "IX_XuiV3VolumeReminderStates_DeliveryStatus_LeaseUntilUtc",
                table: "XuiV3VolumeReminderStates");

            migrationBuilder.DropIndex(
                name: "IX_XuiV3VolumeReminderStates_LastObservedAtUtc",
                table: "XuiV3VolumeReminderStates");

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3VolumeReminderStates_PanelKey_LastObservedAtUtc",
                table: "XuiV3VolumeReminderStates",
                columns: new[] { "PanelKey", "LastObservedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_XuiV3VolumeReminderStates_PanelKey_LastObservedAtUtc",
                table: "XuiV3VolumeReminderStates");

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
        }
    }
}
