using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>Adds empty tenant-scoped customer restrictions and indexes pending receipt evidence without changing existing receipts.</summary>
    public partial class AddTenantCustomerBlocksAndReceiptLookup : Migration
    {
        /// <summary>Creates the storefront/customer key and a nonunique lookup index that preserves historical duplicates.</summary>
        /// <param name="migrationBuilder">EF users.db schema builder for this forward migration.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantCustomerBlocks",
                columns: table => new
                {
                    TenantBotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CustomerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ReceiptId = table.Column<int>(type: "INTEGER", nullable: false),
                    BlockedByTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    BlockedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantCustomerBlocks", x => new { x.TenantBotId, x.CustomerTelegramUserId });
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantManualPaymentReceipts_TenantBotId_CustomerTelegramUserId_Status",
                table: "TenantManualPaymentReceipts",
                columns: new[] { "TenantBotId", "CustomerTelegramUserId", "Status" });
        }

        /// <summary>Removes the lookup index and active local restrictions while retaining orders and receipt decisions.</summary>
        /// <param name="migrationBuilder">EF users.db schema builder for this downgrade.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantCustomerBlocks");

            migrationBuilder.DropIndex(
                name: "IX_TenantManualPaymentReceipts_TenantBotId_CustomerTelegramUserId_Status",
                table: "TenantManualPaymentReceipts");
        }
    }
}
