using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds the shared wallet ledger, tenant card-to-card receipt tracking, and expanded tenant storefront settings to <c>users.db</c>.
    /// </summary>
    /// <remarks>
    /// This migration is scoped to <see cref="UserDbContext"/>. It does not alter <c>credentials.db</c>;
    /// wallet balances remain in the credentials database while auditable transaction history and tenant
    /// payment metadata live in <c>users.db</c>.
    /// </remarks>
    [DbContext(typeof(UserDbContext))]
    [Migration("20260626000000_AddWalletLedgerTenantStorefront")]
    public partial class AddWalletLedgerTenantStorefront : Migration
    {
        /// <summary>
        /// Creates tenant storefront payment settings, order provider metadata, general wallet ledger rows, and manual receipt rows.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder for the <c>users.db</c> schema.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TenantMandatoryJoinEnabled",
                table: "BotInstances",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TenantChannelIdsJson",
                table: "BotInstances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TenantCardPaymentEnabled",
                table: "BotInstances",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "TenantCardNumber",
                table: "BotInstances",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantCardHolderName",
                table: "BotInstances",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TenantHooshPayEnabled",
                table: "BotInstances",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "TenantNowPaymentsEnabled",
                table: "BotInstances",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentPurpose",
                table: "SwapinoPaymentInfos",
                type: "TEXT",
                maxLength: 64,
                nullable: true,
                defaultValue: "wallet_charge");

            migrationBuilder.AddColumn<int>(
                name: "TenantBotOrderId",
                table: "SwapinoPaymentInfos",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TenantOwnerTelegramUserId",
                table: "SwapinoPaymentInfos",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "NowPaymentsPaymentInfoId",
                table: "TenantBotOrders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ManualReceiptId",
                table: "TenantBotOrders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FulfillmentSource",
                table: "TenantBotOrders",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "OwnerWalletDelta",
                table: "TenantBotOrders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "WalletLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BotUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    BotType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    OwnerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    CounterpartyTelegramUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    Direction = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    AmountToman = table.Column<long>(type: "INTEGER", nullable: false),
                    BalanceBefore = table.Column<long>(type: "INTEGER", nullable: false),
                    BalanceAfter = table.Column<long>(type: "INTEGER", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ReferenceType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ReferenceId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 140, nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletLedgerEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantManualPaymentReceipts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantBotOrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 140, nullable: true),
                    TenantBotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TenantBotUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    OwnerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    CustomerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    CustomerChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    PhotoFileId = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    AmountToman = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    ReviewerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RejectedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinalConfirmedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantManualPaymentReceipts", x => x.Id);
                });

            migrationBuilder.CreateIndex("IX_SwapinoPaymentInfos_PaymentPurpose", "SwapinoPaymentInfos", "PaymentPurpose");
            migrationBuilder.CreateIndex("IX_SwapinoPaymentInfos_TenantBotOrderId", "SwapinoPaymentInfos", "TenantBotOrderId");
            migrationBuilder.CreateIndex("IX_TenantBotOrders_NowPaymentsPaymentInfoId", "TenantBotOrders", "NowPaymentsPaymentInfoId");
            migrationBuilder.CreateIndex("IX_TenantBotOrders_ManualReceiptId", "TenantBotOrders", "ManualReceiptId");
            migrationBuilder.CreateIndex("IX_WalletLedgerEntries_TelegramUserId_CreatedAtUtc", "WalletLedgerEntries", new[] { "TelegramUserId", "CreatedAtUtc" });
            migrationBuilder.CreateIndex("IX_WalletLedgerEntries_OrderId", "WalletLedgerEntries", "OrderId");
            migrationBuilder.CreateIndex("IX_WalletLedgerEntries_BotId", "WalletLedgerEntries", "BotId");
            migrationBuilder.CreateIndex("IX_TenantManualPaymentReceipts_TenantBotOrderId", "TenantManualPaymentReceipts", "TenantBotOrderId", unique: true);
            migrationBuilder.CreateIndex("IX_TenantManualPaymentReceipts_OrderId", "TenantManualPaymentReceipts", "OrderId");
            migrationBuilder.CreateIndex("IX_TenantManualPaymentReceipts_OwnerTelegramUserId", "TenantManualPaymentReceipts", "OwnerTelegramUserId");
            migrationBuilder.CreateIndex("IX_TenantManualPaymentReceipts_CustomerTelegramUserId", "TenantManualPaymentReceipts", "CustomerTelegramUserId");
            migrationBuilder.CreateIndex("IX_TenantManualPaymentReceipts_Status", "TenantManualPaymentReceipts", "Status");
        }

        /// <summary>
        /// Removes the tenant storefront extensions created by this migration from <c>users.db</c>.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder for rollback operations.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "TenantManualPaymentReceipts");
            migrationBuilder.DropTable(name: "WalletLedgerEntries");
            migrationBuilder.DropIndex(name: "IX_SwapinoPaymentInfos_PaymentPurpose", table: "SwapinoPaymentInfos");
            migrationBuilder.DropIndex(name: "IX_SwapinoPaymentInfos_TenantBotOrderId", table: "SwapinoPaymentInfos");
            migrationBuilder.DropIndex(name: "IX_TenantBotOrders_NowPaymentsPaymentInfoId", table: "TenantBotOrders");
            migrationBuilder.DropIndex(name: "IX_TenantBotOrders_ManualReceiptId", table: "TenantBotOrders");
            migrationBuilder.DropColumn(name: "PaymentPurpose", table: "SwapinoPaymentInfos");
            migrationBuilder.DropColumn(name: "TenantBotOrderId", table: "SwapinoPaymentInfos");
            migrationBuilder.DropColumn(name: "TenantOwnerTelegramUserId", table: "SwapinoPaymentInfos");
            migrationBuilder.DropColumn(name: "NowPaymentsPaymentInfoId", table: "TenantBotOrders");
            migrationBuilder.DropColumn(name: "ManualReceiptId", table: "TenantBotOrders");
            migrationBuilder.DropColumn(name: "FulfillmentSource", table: "TenantBotOrders");
            migrationBuilder.DropColumn(name: "OwnerWalletDelta", table: "TenantBotOrders");
            migrationBuilder.DropColumn(name: "TenantMandatoryJoinEnabled", table: "BotInstances");
            migrationBuilder.DropColumn(name: "TenantChannelIdsJson", table: "BotInstances");
            migrationBuilder.DropColumn(name: "TenantCardPaymentEnabled", table: "BotInstances");
            migrationBuilder.DropColumn(name: "TenantCardNumber", table: "BotInstances");
            migrationBuilder.DropColumn(name: "TenantCardHolderName", table: "BotInstances");
            migrationBuilder.DropColumn(name: "TenantHooshPayEnabled", table: "BotInstances");
            migrationBuilder.DropColumn(name: "TenantNowPaymentsEnabled", table: "BotInstances");
        }
    }
}
