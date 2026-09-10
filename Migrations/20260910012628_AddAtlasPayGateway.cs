using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class AddAtlasPayGateway : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AtlasPayPaymentInfoId",
                table: "TenantBotOrders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TenantAtlasPayEnabled",
                table: "BotInstances",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "AtlasPayPaymentInfos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    MerchantOrderRef = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    ProviderOrderId = table.Column<int>(type: "INTEGER", nullable: true),
                    TrackingCode = table.Column<string>(type: "TEXT", maxLength: 180, nullable: true),
                    CustomerStartLink = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    CardNumberMasked = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BaseAmountToman = table.Column<long>(type: "INTEGER", nullable: false),
                    TotalAmountToman = table.Column<long>(type: "INTEGER", nullable: true),
                    ActualReceivedAmountToman = table.Column<long>(type: "INTEGER", nullable: true),
                    ProviderStatus = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RequiresManualDelivery = table.Column<bool>(type: "INTEGER", nullable: false),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    TelMsgId = table.Column<int>(type: "INTEGER", nullable: true),
                    BotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BotUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PaymentPurpose = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TenantBotOrderId = table.Column<int>(type: "INTEGER", nullable: true),
                    TenantOwnerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreationState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "ambiguous"),
                    CreationAttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreationAttemptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreationResolvedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreationErrorCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LastInquiryAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextInquiryAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    InquiryAttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PaidAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SettledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsAddedToBalance = table.Column<bool>(type: "INTEGER", nullable: false),
                    SettlementState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "pending"),
                    SettlementAttemptId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SettlementStartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BalanceBefore = table.Column<long>(type: "INTEGER", nullable: true),
                    BalanceAfter = table.Column<long>(type: "INTEGER", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LastErrorLoggedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SuccessLoggedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PaymentDeadlineAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AtlasPayPaymentInfos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantBotOrders_AtlasPayPaymentInfoId",
                table: "TenantBotOrders",
                column: "AtlasPayPaymentInfoId");

            migrationBuilder.CreateIndex(
                name: "IX_AtlasPayPaymentInfos_BotId",
                table: "AtlasPayPaymentInfos",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_AtlasPayPaymentInfos_MerchantOrderRef",
                table: "AtlasPayPaymentInfos",
                column: "MerchantOrderRef",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AtlasPayPaymentInfos_ProviderOrderId",
                table: "AtlasPayPaymentInfos",
                column: "ProviderOrderId",
                unique: true,
                filter: "\"ProviderOrderId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_AtlasPayPaymentInfos_ProviderStatus",
                table: "AtlasPayPaymentInfos",
                column: "ProviderStatus");

            migrationBuilder.CreateIndex(
                name: "IX_AtlasPayPaymentInfos_SettlementState_NextInquiryAtUtc",
                table: "AtlasPayPaymentInfos",
                columns: new[] { "SettlementState", "NextInquiryAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AtlasPayPaymentInfos_TelegramUserId",
                table: "AtlasPayPaymentInfos",
                column: "TelegramUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AtlasPayPaymentInfos_TenantBotOrderId",
                table: "AtlasPayPaymentInfos",
                column: "TenantBotOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_AtlasPayPaymentInfos_TrackingCode",
                table: "AtlasPayPaymentInfos",
                column: "TrackingCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AtlasPayPaymentInfos");

            migrationBuilder.DropIndex(
                name: "IX_TenantBotOrders_AtlasPayPaymentInfoId",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "AtlasPayPaymentInfoId",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "TenantAtlasPayEnabled",
                table: "BotInstances");
        }
    }
}
