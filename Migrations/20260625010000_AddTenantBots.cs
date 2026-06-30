using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds tenant storefront support to <c>users.db</c>.
    /// </summary>
    /// <remarks>
    /// This migration extends bot instances with tenant configuration, marks HooshPay rows by payment purpose,
    /// and creates order plus ledger tables for direct tenant storefront sales. It intentionally targets
    /// <see cref="UserDbContext"/> only and does not change <c>credentials.db</c>.
    /// </remarks>
    [DbContext(typeof(UserDbContext))]
    [Migration("20260625010000_AddTenantBots")]
    public partial class AddTenantBots : Migration
    {
        /// <summary>
        /// Applies tenant-bot schema changes and creates indexes used by IPN lookup, owner reports,
        /// customer manual checks, and idempotent ledger writes.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder for <c>users.db</c>.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Tenant bot metadata and sales accounting live only in users.db.
            migrationBuilder.AddColumn<string>(
                name: "Token",
                table: "BotInstances",
                type: "TEXT",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TenantPriceMarkupPercent",
                table: "BotInstances",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "TenantWelcomeText",
                table: "BotInstances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentPurpose",
                table: "HooshPayPaymentInfos",
                type: "TEXT",
                maxLength: 64,
                nullable: true,
                defaultValue: "wallet_charge");

            migrationBuilder.AddColumn<int>(
                name: "TenantBotOrderId",
                table: "HooshPayPaymentInfos",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TenantOwnerTelegramUserId",
                table: "HooshPayPaymentInfos",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantBotOrders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 140, nullable: false),
                    TenantBotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TenantBotUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    OwnerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    CustomerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    CustomerChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    CustomerUsername = table.Column<string>(type: "TEXT", nullable: true),
                    CustomerFirstName = table.Column<string>(type: "TEXT", nullable: true),
                    CustomerLastName = table.Column<string>(type: "TEXT", nullable: true),
                    ServiceKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TrafficGb = table.Column<int>(type: "INTEGER", nullable: true),
                    DurationKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    UnlimitedPlanKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AccountCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UserComment = table.Column<string>(type: "TEXT", nullable: true),
                    SalePriceToman = table.Column<long>(type: "INTEGER", nullable: false),
                    BaseCostToman = table.Column<long>(type: "INTEGER", nullable: false),
                    ProfitToman = table.Column<long>(type: "INTEGER", nullable: false),
                    PaymentProvider = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PaymentStatus = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    HooshPayPaymentInfoId = table.Column<int>(type: "INTEGER", nullable: true),
                    HooshPayInvoiceUid = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    PaymentUrl = table.Column<string>(type: "TEXT", nullable: true),
                    IsFulfilled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsOwnerCredited = table.Column<bool>(type: "INTEGER", nullable: false),
                    OwnerBalanceBefore = table.Column<long>(type: "INTEGER", nullable: true),
                    OwnerBalanceAfter = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAccountEmail = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedSubLink = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAccountJson = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PaidAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FulfilledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantBotOrders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantBotLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantBotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TenantBotUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    TenantBotOrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 140, nullable: true),
                    OwnerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    CustomerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    SalePriceToman = table.Column<long>(type: "INTEGER", nullable: false),
                    BaseCostToman = table.Column<long>(type: "INTEGER", nullable: false),
                    ProfitToman = table.Column<long>(type: "INTEGER", nullable: false),
                    OwnerBalanceBefore = table.Column<long>(type: "INTEGER", nullable: true),
                    OwnerBalanceAfter = table.Column<long>(type: "INTEGER", nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantBotLedgerEntries", x => x.Id);
                });

            migrationBuilder.CreateIndex("IX_HooshPayPaymentInfos_PaymentPurpose", "HooshPayPaymentInfos", "PaymentPurpose");
            migrationBuilder.CreateIndex("IX_HooshPayPaymentInfos_TenantBotOrderId", "HooshPayPaymentInfos", "TenantBotOrderId");
            migrationBuilder.CreateIndex("IX_HooshPayPaymentInfos_TenantOwnerTelegramUserId", "HooshPayPaymentInfos", "TenantOwnerTelegramUserId");
            migrationBuilder.CreateIndex("IX_TenantBotOrders_OrderId", "TenantBotOrders", "OrderId", unique: true);
            migrationBuilder.CreateIndex("IX_TenantBotOrders_TenantBotId", "TenantBotOrders", "TenantBotId");
            migrationBuilder.CreateIndex("IX_TenantBotOrders_OwnerTelegramUserId", "TenantBotOrders", "OwnerTelegramUserId");
            migrationBuilder.CreateIndex("IX_TenantBotOrders_CustomerTelegramUserId", "TenantBotOrders", "CustomerTelegramUserId");
            migrationBuilder.CreateIndex("IX_TenantBotOrders_HooshPayPaymentInfoId", "TenantBotOrders", "HooshPayPaymentInfoId");
            migrationBuilder.CreateIndex("IX_TenantBotLedgerEntries_TenantBotId", "TenantBotLedgerEntries", "TenantBotId");
            migrationBuilder.CreateIndex("IX_TenantBotLedgerEntries_OwnerTelegramUserId", "TenantBotLedgerEntries", "OwnerTelegramUserId");
            migrationBuilder.CreateIndex("IX_TenantBotLedgerEntries_CustomerTelegramUserId", "TenantBotLedgerEntries", "CustomerTelegramUserId");
            migrationBuilder.CreateIndex("IX_TenantBotLedgerEntries_TenantBotOrderId", "TenantBotLedgerEntries", "TenantBotOrderId", unique: true);
        }

        /// <summary>
        /// Reverts tenant-bot tables, indexes, and HooshPay purpose columns from <c>users.db</c>.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder for rollback operations.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "TenantBotLedgerEntries");
            migrationBuilder.DropTable(name: "TenantBotOrders");
            migrationBuilder.DropIndex(name: "IX_HooshPayPaymentInfos_PaymentPurpose", table: "HooshPayPaymentInfos");
            migrationBuilder.DropIndex(name: "IX_HooshPayPaymentInfos_TenantBotOrderId", table: "HooshPayPaymentInfos");
            migrationBuilder.DropIndex(name: "IX_HooshPayPaymentInfos_TenantOwnerTelegramUserId", table: "HooshPayPaymentInfos");
            migrationBuilder.DropColumn(name: "PaymentPurpose", table: "HooshPayPaymentInfos");
            migrationBuilder.DropColumn(name: "TenantBotOrderId", table: "HooshPayPaymentInfos");
            migrationBuilder.DropColumn(name: "TenantOwnerTelegramUserId", table: "HooshPayPaymentInfos");
            migrationBuilder.DropColumn(name: "Token", table: "BotInstances");
            migrationBuilder.DropColumn(name: "TenantPriceMarkupPercent", table: "BotInstances");
            migrationBuilder.DropColumn(name: "TenantWelcomeText", table: "BotInstances");
        }
    }
}
