using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>Adds fail-closed storefront approval, unique wallet-order admission and immutable central-charge origin.</summary>
    /// <remarks>Existing stores remain unapproved and historical gateway rows retain owned origin. No balances, historical
    /// financial effects or credentials.db schema change. Order admission keys are unique when non-null; approval evidence
    /// binds one internal store to its owner and verified Telegram bot identity. Retain financial rows for reconciliation.</remarks>
    public partial class TenantCustomerWallet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WalletOriginBotType",
                table: "UniquePayPaymentInfos",
                type: "TEXT",
                nullable: true,
                defaultValue: "owned");

            migrationBuilder.AddColumn<string>(
                name: "WalletOriginBotType",
                table: "TetraminatorPaymentInfos",
                type: "TEXT",
                nullable: true,
                defaultValue: "owned");

            migrationBuilder.AddColumn<string>(
                name: "CustomerWalletAdmissionKey",
                table: "TenantBotOrders",
                type: "TEXT",
                maxLength: 240,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CustomerWalletState",
                table: "TenantBotOrders",
                type: "TEXT",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WalletOriginBotType",
                table: "SwapinoPaymentInfos",
                type: "TEXT",
                nullable: true,
                defaultValue: "owned");

            migrationBuilder.AddColumn<string>(
                name: "WalletOriginBotType",
                table: "HooshPayPaymentInfos",
                type: "TEXT",
                nullable: true,
                defaultValue: "owned");

            migrationBuilder.AddColumn<DateTime>(
                name: "TenantCustomerWalletApprovedAtUtc",
                table: "BotInstances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TenantCustomerWalletApprovedBotId",
                table: "BotInstances",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TenantCustomerWalletApprovedByTelegramUserId",
                table: "BotInstances",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TenantCustomerWalletApprovedOwnerId",
                table: "BotInstances",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TenantCustomerWalletEnabled",
                table: "BotInstances",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "WalletOriginBotType",
                table: "AtlasPayPaymentInfos",
                type: "TEXT",
                nullable: true,
                defaultValue: "owned");

            migrationBuilder.CreateIndex(
                name: "IX_TenantBotOrders_CustomerWalletAdmissionKey",
                table: "TenantBotOrders",
                column: "CustomerWalletAdmissionKey",
                unique: true);
        }

        /// <summary>Removes unused capability metadata only before any tenant customer-wallet financial history exists.</summary>
        /// <param name="migrationBuilder">EF users.db downgrade builder.</param>
        /// <remarks>Financial origins and recovery markers cannot be discarded by a downgrade. Use a compatible forward
        /// release when any tenant-origin charge or admitted wallet order exists, including completed operations.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE CustomerWalletRollbackGuard (Safe INTEGER CHECK (Safe = 1));
                INSERT INTO CustomerWalletRollbackGuard SELECT CASE WHEN
                    EXISTS (SELECT 1 FROM TenantBotOrders WHERE CustomerWalletAdmissionKey IS NOT NULL OR PaymentProvider = 'wallet') OR
                    EXISTS (SELECT 1 FROM HooshPayPaymentInfos WHERE WalletOriginBotType = 'tenant') OR
                    EXISTS (SELECT 1 FROM TetraminatorPaymentInfos WHERE WalletOriginBotType = 'tenant') OR
                    EXISTS (SELECT 1 FROM UniquePayPaymentInfos WHERE WalletOriginBotType = 'tenant') OR
                    EXISTS (SELECT 1 FROM AtlasPayPaymentInfos WHERE WalletOriginBotType = 'tenant') OR
                    EXISTS (SELECT 1 FROM SwapinoPaymentInfos WHERE WalletOriginBotType = 'tenant')
                    THEN 0 ELSE 1 END;
                DROP TABLE CustomerWalletRollbackGuard;
                """);
            migrationBuilder.DropIndex(
                name: "IX_TenantBotOrders_CustomerWalletAdmissionKey",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "WalletOriginBotType",
                table: "UniquePayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "WalletOriginBotType",
                table: "TetraminatorPaymentInfos");

            migrationBuilder.DropColumn(
                name: "CustomerWalletAdmissionKey",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "CustomerWalletState",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "WalletOriginBotType",
                table: "SwapinoPaymentInfos");

            migrationBuilder.DropColumn(
                name: "WalletOriginBotType",
                table: "HooshPayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "TenantCustomerWalletApprovedAtUtc",
                table: "BotInstances");

            migrationBuilder.DropColumn(
                name: "TenantCustomerWalletApprovedBotId",
                table: "BotInstances");

            migrationBuilder.DropColumn(
                name: "TenantCustomerWalletApprovedByTelegramUserId",
                table: "BotInstances");

            migrationBuilder.DropColumn(
                name: "TenantCustomerWalletApprovedOwnerId",
                table: "BotInstances");

            migrationBuilder.DropColumn(
                name: "TenantCustomerWalletEnabled",
                table: "BotInstances");

            migrationBuilder.DropColumn(
                name: "WalletOriginBotType",
                table: "AtlasPayPaymentInfos");
        }
    }
}
