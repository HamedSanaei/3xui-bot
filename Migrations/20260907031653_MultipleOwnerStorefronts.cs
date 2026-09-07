using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>Adds owner-local storefront numbers and durable funding intent without changing balances or existing bot ids.</summary>
    /// <remarks>Existing canonical stores receive number one. Historical duplicate bot identities fail the unique index and require review.</remarks>
    public partial class MultipleOwnerStorefronts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OwnerStoreId",
                table: "BotUserStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TelegramBotId",
                table: "BotInstances",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TenantCreationKey",
                table: "BotInstances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TenantStoreNumber",
                table: "BotInstances",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.Sql("""
                WITH numbered AS (
                    SELECT Id, ROW_NUMBER() OVER (PARTITION BY OwnerTelegramUserId
                        ORDER BY CASE WHEN Id = 'tenant-' || OwnerTelegramUserId THEN 0 ELSE 1 END, CreatedAtUtc, Id) AS number
                    FROM BotInstances WHERE Type = 'tenant' AND OwnerTelegramUserId IS NOT NULL
                )
                UPDATE BotInstances SET TenantStoreNumber = (SELECT number FROM numbered WHERE numbered.Id = BotInstances.Id)
                WHERE Id IN (SELECT Id FROM numbered);
                UPDATE BotInstances SET TelegramBotId = CAST(substr(Token, 1, instr(Token, ':') - 1) AS INTEGER)
                WHERE instr(Token, ':') > 1
                    AND substr(Token, 1, instr(Token, ':') - 1) NOT GLOB '*[^0-9]*'
                    AND CAST(substr(Token, 1, instr(Token, ':') - 1) AS INTEGER) > 0;
                """);

            migrationBuilder.CreateTable(
                name: "SiteWalletDebitOperation",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", nullable: false),
                    OwnerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountToman = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    BeforeBalance = table.Column<long>(type: "INTEGER", nullable: false),
                    AfterBalance = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteWalletDebitOperation", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantWalletRoute",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    OwnerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountToman = table.Column<long>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantWalletRoute", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BotInstances_OwnerTelegramUserId_TenantStoreNumber",
                table: "BotInstances",
                columns: new[] { "OwnerTelegramUserId", "TenantStoreNumber" },
                unique: true);

            // Historical panel success without final settlement cannot prove which wallet was charged.
            // Record only review metadata; credentials.db receipts may later prove a local debit without replay.
            migrationBuilder.Sql("""
                INSERT INTO TenantWalletRoute (Id, OwnerTelegramUserId, AmountToman, Source)
                SELECT o.Id, o.OwnerTelegramUserId, o.BaseCostToman, 'review'
                FROM TenantBotOrders o
                WHERE o.IsFulfilled = 0 AND o.PaymentProvider = 'tenant_card' COLLATE NOCASE AND o.BaseCostToman > 0
                  AND (COALESCE(o.CreatedAccountEmail, '') <> ''
                    OR EXISTS (SELECT 1 FROM XuiV3CreationOperations c
                        WHERE (c.OperationKey = 'tenant-create:' || o.Id OR c.OperationKey LIKE 'tenant-create:' || o.Id || ':retry:%')
                          AND c.AppliedAtUtc IS NOT NULL)
                    OR EXISTS (SELECT 1 FROM XuiV3RenewalOperations r
                        WHERE r.TenantBotOrderId = o.OrderId AND r.Status = 'applied'));
                """);

            migrationBuilder.CreateIndex(
                name: "IX_BotInstances_TelegramBotId",
                table: "BotInstances",
                column: "TelegramBotId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SiteWalletDebitOperation_OwnerTelegramUserId_Status",
                table: "SiteWalletDebitOperation",
                columns: new[] { "OwnerTelegramUserId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Do not erase new financial evidence or make additional storefronts unaddressable during rollback.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE StorefrontRollbackGuard (Safe INTEGER CHECK (Safe = 1));
                INSERT INTO StorefrontRollbackGuard SELECT 0 WHERE EXISTS (SELECT 1 FROM SiteWalletDebitOperation)
                    OR EXISTS (SELECT 1 FROM TenantWalletRoute)
                    OR EXISTS (SELECT 1 FROM BotInstances WHERE TenantStoreNumber > 1);
                DROP TABLE StorefrontRollbackGuard;
                """);
            migrationBuilder.DropTable(
                name: "SiteWalletDebitOperation");

            migrationBuilder.DropTable(
                name: "TenantWalletRoute");

            migrationBuilder.DropIndex(
                name: "IX_BotInstances_OwnerTelegramUserId_TenantStoreNumber",
                table: "BotInstances");

            migrationBuilder.DropIndex(
                name: "IX_BotInstances_TelegramBotId",
                table: "BotInstances");

            migrationBuilder.DropColumn(
                name: "OwnerStoreId",
                table: "BotUserStates");

            migrationBuilder.DropColumn(
                name: "TelegramBotId",
                table: "BotInstances");

            migrationBuilder.DropColumn(
                name: "TenantCreationKey",
                table: "BotInstances");

            migrationBuilder.DropColumn(
                name: "TenantStoreNumber",
                table: "BotInstances");
        }
    }
}
