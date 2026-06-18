using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    [DbContext(typeof(UserDbContext))]
    [Migration("20260618000000_AddHooshPayPaymentInfo")]
    public partial class AddHooshPayPaymentInfo : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "HooshPayPaymentInfos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    InvoiceUid = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    PaymentUrl = table.Column<string>(type: "TEXT", nullable: true),
                    AmountToman = table.Column<long>(type: "INTEGER", nullable: false),
                    PayableAmountToman = table.Column<long>(type: "INTEGER", nullable: false),
                    MerchantCreditToman = table.Column<long>(type: "INTEGER", nullable: false),
                    FeeAmountToman = table.Column<long>(type: "INTEGER", nullable: false),
                    FeePercent = table.Column<decimal>(type: "TEXT", nullable: false),
                    FeeMode = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    PaymentStatus = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TrackingCode = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    RawRequestJson = table.Column<string>(type: "TEXT", nullable: true),
                    RawResponseJson = table.Column<string>(type: "TEXT", nullable: true),
                    RawIpnJson = table.Column<string>(type: "TEXT", nullable: true),
                    IpnCallbackUrl = table.Column<string>(type: "TEXT", nullable: true),
                    ReturnUrl = table.Column<string>(type: "TEXT", nullable: true),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    TelMsgId = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PaidAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SettledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsAddedToBalance = table.Column<bool>(type: "INTEGER", nullable: false),
                    BalanceBefore = table.Column<long>(type: "INTEGER", nullable: true),
                    BalanceAfter = table.Column<long>(type: "INTEGER", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HooshPayPaymentInfos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_HooshPayPaymentInfos_OrderId",
                table: "HooshPayPaymentInfos",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HooshPayPaymentInfos_InvoiceUid",
                table: "HooshPayPaymentInfos",
                column: "InvoiceUid");

            migrationBuilder.CreateIndex(
                name: "IX_HooshPayPaymentInfos_TelegramUserId",
                table: "HooshPayPaymentInfos",
                column: "TelegramUserId");

            migrationBuilder.CreateIndex(
                name: "IX_HooshPayPaymentInfos_ChatId",
                table: "HooshPayPaymentInfos",
                column: "ChatId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HooshPayPaymentInfos");
        }
    }
}
