using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds Tetraminator payment audit rows, the per-tenant gateway preference, and the tenant-order payment link to users.db.
    /// </summary>
    /// <remarks>
    /// Existing tenant rows receive an enabled local preference; effective availability still requires the global
    /// application switch. This migration does not target credentials.db and does not modify wallet/profile schema.
    /// </remarks>
    public partial class AddTetraminatorPayments : Migration
    {
        /// <summary>Creates the Tetraminator users.db table, indexes, and nullable tenant-order association.</summary>
        /// <param name="migrationBuilder">EF Core schema builder for the configured users.db database.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TetraminatorPaymentInfoId",
                table: "TenantBotOrders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TenantTetraminatorEnabled",
                table: "BotInstances",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "TetraminatorPaymentInfos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    PayId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    PaymentLink = table.Column<string>(type: "TEXT", nullable: true),
                    AmountToman = table.Column<long>(type: "INTEGER", nullable: false),
                    PaymentStatus = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    CallbackUrl = table.Column<string>(type: "TEXT", nullable: true),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    TelMsgId = table.Column<long>(type: "INTEGER", nullable: true),
                    BotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BotUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PaymentPurpose = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TenantBotOrderId = table.Column<int>(type: "INTEGER", nullable: true),
                    TenantOwnerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    RawRequestJson = table.Column<string>(type: "TEXT", nullable: true),
                    RawResponseJson = table.Column<string>(type: "TEXT", nullable: true),
                    CallbackReceived = table.Column<bool>(type: "INTEGER", nullable: false),
                    CallbackReceivedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastInquiryAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    InquiryAttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PaidAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SettledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsAddedToBalance = table.Column<bool>(type: "INTEGER", nullable: false),
                    BalanceBefore = table.Column<long>(type: "INTEGER", nullable: true),
                    BalanceAfter = table.Column<long>(type: "INTEGER", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    IsProvisionallyApproved = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProvisionalApprovedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ProvisionalApprovedByTelegramUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    ProviderConfirmedAfterProvisionalAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TetraminatorPaymentInfos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantBotOrders_TetraminatorPaymentInfoId",
                table: "TenantBotOrders",
                column: "TetraminatorPaymentInfoId");

            migrationBuilder.CreateIndex(
                name: "IX_TetraminatorPaymentInfos_BotId",
                table: "TetraminatorPaymentInfos",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_TetraminatorPaymentInfos_OrderId",
                table: "TetraminatorPaymentInfos",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TetraminatorPaymentInfos_PayId",
                table: "TetraminatorPaymentInfos",
                column: "PayId",
                unique: true,
                filter: "\"PayId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_TetraminatorPaymentInfos_PaymentPurpose",
                table: "TetraminatorPaymentInfos",
                column: "PaymentPurpose");

            migrationBuilder.CreateIndex(
                name: "IX_TetraminatorPaymentInfos_TelegramUserId",
                table: "TetraminatorPaymentInfos",
                column: "TelegramUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TetraminatorPaymentInfos_TenantBotOrderId",
                table: "TetraminatorPaymentInfos",
                column: "TenantBotOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_TetraminatorPaymentInfos_TenantOwnerTelegramUserId",
                table: "TetraminatorPaymentInfos",
                column: "TenantOwnerTelegramUserId");
        }

        /// <summary>Removes only the users.db schema introduced by this migration.</summary>
        /// <param name="migrationBuilder">EF Core schema builder for the configured users.db database.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TetraminatorPaymentInfos");

            migrationBuilder.DropIndex(
                name: "IX_TenantBotOrders_TetraminatorPaymentInfoId",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "TetraminatorPaymentInfoId",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "TenantTetraminatorEnabled",
                table: "BotInstances");
        }
    }
}
