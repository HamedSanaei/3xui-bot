using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class TenantCustomerWalletBotIdentityBinding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "WalletOriginTelegramBotId",
                table: "UniquePayPaymentInfos",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WalletOriginTelegramBotId",
                table: "TetraminatorPaymentInfos",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WalletOriginTelegramBotId",
                table: "SwapinoPaymentInfos",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WalletOriginBotType",
                table: "PaymentSettlementNotifications",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "owned");

            migrationBuilder.AddColumn<long>(
                name: "WalletOriginTelegramBotId",
                table: "PaymentSettlementNotifications",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WalletOriginTelegramBotId",
                table: "HooshPayPaymentInfos",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WalletOriginTelegramBotId",
                table: "AtlasPayPaymentInfos",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WalletOriginTelegramBotId",
                table: "UniquePayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "WalletOriginTelegramBotId",
                table: "TetraminatorPaymentInfos");

            migrationBuilder.DropColumn(
                name: "WalletOriginTelegramBotId",
                table: "SwapinoPaymentInfos");

            migrationBuilder.DropColumn(
                name: "WalletOriginBotType",
                table: "PaymentSettlementNotifications");

            migrationBuilder.DropColumn(
                name: "WalletOriginTelegramBotId",
                table: "PaymentSettlementNotifications");

            migrationBuilder.DropColumn(
                name: "WalletOriginTelegramBotId",
                table: "HooshPayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "WalletOriginTelegramBotId",
                table: "AtlasPayPaymentInfos");
        }
    }
}
