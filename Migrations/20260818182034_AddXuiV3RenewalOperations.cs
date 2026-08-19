using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds the durable exactly-once renewal operation table and the bot-state renewal session identity.
    /// </summary>
    /// <remarks>
    /// <see cref="XuiV3RenewalOperation"/> rows are written to <c>users.db</c> before the XUI renewal mutation and
    /// survive process restarts. The unique <c>OperationKey</c>, the lease-bound processing claim, the atomic applied
    /// transition, and the settlement guard prevent one intended renewal from being applied or settled twice under
    /// Telegram redelivery, repeated confirm presses, concurrent duplicate requests, XUI timeouts, and process
    /// restarts. <c>RenewalSessionId</c> on <c>BotUserStates</c> gives each confirm step a stable identity; existing
    /// rows remain null and fall back to an intent-based operation key. The migration changes only <c>users.db</c>;
    /// credentials, wallets, payments, and XUI clients are untouched.
    /// </remarks>
    public partial class AddXuiV3RenewalOperations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RenewalSessionId",
                table: "BotUserStates",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "XuiV3RenewalOperations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    OperationId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TenantBotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TenantBotOrderId = table.Column<string>(type: "TEXT", maxLength: 140, nullable: true),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    TargetEmail = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    TargetUuid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ServiceKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AddedTrafficGb = table.Column<int>(type: "INTEGER", nullable: false),
                    AddedTrafficBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    AddedDurationDays = table.Column<int>(type: "INTEGER", nullable: false),
                    PriceToman = table.Column<long>(type: "INTEGER", nullable: false),
                    PaymentMethod = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ExpectedTotalBytesBefore = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpectedExpiryTimeBefore = table.Column<long>(type: "INTEGER", nullable: false),
                    TargetTotalBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    TargetExpiryTime = table.Column<long>(type: "INTEGER", nullable: false),
                    MutationPayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    ShouldResetTraffic = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsUnlimited = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SettlementStatus = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    SettlementStartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SettledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SuccessLogSentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ClaimToken = table.Column<string>(type: "TEXT", maxLength: 40, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_XuiV3RenewalOperations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3RenewalOperations_BotId",
                table: "XuiV3RenewalOperations",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3RenewalOperations_OperationKey",
                table: "XuiV3RenewalOperations",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3RenewalOperations_SettlementStatus_SettlementStartedAtUtc",
                table: "XuiV3RenewalOperations",
                columns: new[] { "SettlementStatus", "SettlementStartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3RenewalOperations_Status_LeaseUntilUtc",
                table: "XuiV3RenewalOperations",
                columns: new[] { "Status", "LeaseUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3RenewalOperations_TelegramUserId",
                table: "XuiV3RenewalOperations",
                column: "TelegramUserId");

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3RenewalOperations_TenantBotOrderId",
                table: "XuiV3RenewalOperations",
                column: "TenantBotOrderId",
                unique: true,
                filter: "\"TenantBotOrderId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "RenewalSessionId",
                table: "BotUserStates");
        }
    }
}
