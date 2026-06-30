using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletLedgerTenantStorefrontGenerated : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WalletLedgerEntries_TelegramUserId_CreatedAtUtc",
                table: "WalletLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_TenantManualPaymentReceipts_OrderId",
                table: "TenantManualPaymentReceipts");

            migrationBuilder.DropIndex(
                name: "IX_TenantManualPaymentReceipts_TenantBotOrderId",
                table: "TenantManualPaymentReceipts");

            migrationBuilder.AlterColumn<string>(
                name: "Direction",
                table: "WalletLedgerEntries",
                type: "TEXT",
                maxLength: 16,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 16);

            migrationBuilder.AlterColumn<long>(
                name: "OwnerWalletDelta",
                table: "TenantBotOrders",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "INTEGER",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalletLedgerEntries_CreatedAtUtc",
                table: "WalletLedgerEntries",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_WalletLedgerEntries_OwnerTelegramUserId",
                table: "WalletLedgerEntries",
                column: "OwnerTelegramUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WalletLedgerEntries_TelegramUserId",
                table: "WalletLedgerEntries",
                column: "TelegramUserId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantManualPaymentReceipts_TenantBotId",
                table: "TenantManualPaymentReceipts",
                column: "TenantBotId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantManualPaymentReceipts_TenantBotOrderId",
                table: "TenantManualPaymentReceipts",
                column: "TenantBotOrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WalletLedgerEntries_CreatedAtUtc",
                table: "WalletLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_WalletLedgerEntries_OwnerTelegramUserId",
                table: "WalletLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_WalletLedgerEntries_TelegramUserId",
                table: "WalletLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_TenantManualPaymentReceipts_TenantBotId",
                table: "TenantManualPaymentReceipts");

            migrationBuilder.DropIndex(
                name: "IX_TenantManualPaymentReceipts_TenantBotOrderId",
                table: "TenantManualPaymentReceipts");

            migrationBuilder.AlterColumn<string>(
                name: "Direction",
                table: "WalletLedgerEntries",
                type: "TEXT",
                maxLength: 16,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "TEXT",
                oldMaxLength: 16,
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "OwnerWalletDelta",
                table: "TenantBotOrders",
                type: "INTEGER",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.CreateIndex(
                name: "IX_WalletLedgerEntries_TelegramUserId_CreatedAtUtc",
                table: "WalletLedgerEntries",
                columns: new[] { "TelegramUserId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantManualPaymentReceipts_OrderId",
                table: "TenantManualPaymentReceipts",
                column: "OrderId");

            migrationBuilder.CreateIndex(
                name: "IX_TenantManualPaymentReceipts_TenantBotOrderId",
                table: "TenantManualPaymentReceipts",
                column: "TenantBotOrderId",
                unique: true);
        }
    }
}
