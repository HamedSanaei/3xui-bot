using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    [DbContext(typeof(UserDbContext))]
    [Migration("20260604000000_RewriteSwapinoPaymentInfo")]
    public partial class RewriteSwapinoPaymentInfo : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS SwapinoPaymentInfos");
            migrationBuilder.Sql("DROP TABLE IF EXISTS SwapinoPaymentInfo");

            migrationBuilder.CreateTable(
                name: "SwapinoPaymentInfos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: false),
                    ParentOrderId = table.Column<string>(type: "TEXT", nullable: true),
                    RootOrderId = table.Column<string>(type: "TEXT", nullable: true),
                    AttemptNo = table.Column<int>(type: "INTEGER", nullable: false),
                    Result = table.Column<string>(type: "TEXT", nullable: true),
                    RawRequestJson = table.Column<string>(type: "TEXT", nullable: true),
                    RawResponseJson = table.Column<string>(type: "TEXT", nullable: true),
                    RawIpnJson = table.Column<string>(type: "TEXT", nullable: true),
                    IpnCallbackUrl = table.Column<string>(type: "TEXT", nullable: true),
                    SuccessUrl = table.Column<string>(type: "TEXT", nullable: true),
                    CancelUrl = table.Column<string>(type: "TEXT", nullable: true),
                    AmountToman = table.Column<long>(type: "INTEGER", nullable: false),
                    BaseCurrency = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    BaseAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                    PayCurrency = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    InvoiceId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    InvoiceUrl = table.Column<string>(type: "TEXT", nullable: true),
                    PaymentId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    ParentPaymentId = table.Column<string>(type: "TEXT", nullable: true),
                    PaymentStatus = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    PayAddress = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    PayinHash = table.Column<string>(type: "TEXT", nullable: true),
                    PayoutHash = table.Column<string>(type: "TEXT", nullable: true),
                    OutcomeCurrency = table.Column<string>(type: "TEXT", nullable: true),
                    OutcomeAmount = table.Column<decimal>(type: "TEXT", nullable: false),
                    ActuallyPaid = table.Column<decimal>(type: "TEXT", nullable: false),
                    ActuallyPaidAtFiat = table.Column<decimal>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PaidAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SettledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsAddedToBalance = table.Column<bool>(type: "INTEGER", nullable: false),
                    BalanceBefore = table.Column<long>(type: "INTEGER", nullable: true),
                    BalanceAfter = table.Column<long>(type: "INTEGER", nullable: true),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    TelMsgId = table.Column<long>(type: "INTEGER", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwapinoPaymentInfos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SwapinoPaymentInfos_OrderId",
                table: "SwapinoPaymentInfos",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SwapinoPaymentInfos_ParentOrderId",
                table: "SwapinoPaymentInfos",
                column: "ParentOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_SwapinoPaymentInfos_PaymentId",
                table: "SwapinoPaymentInfos",
                column: "PaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_SwapinoPaymentInfos_TelegramUserId",
                table: "SwapinoPaymentInfos",
                column: "TelegramUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SwapinoPaymentInfos_ChatId",
                table: "SwapinoPaymentInfos",
                column: "ChatId");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS SwapinoPaymentInfos");

            migrationBuilder.CreateTable(
                name: "SwapinoPaymentInfos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CallbackUrl = table.Column<string>(type: "TEXT", nullable: true),
                    IsAddedToBallance = table.Column<bool>(type: "INTEGER", nullable: false),
                    Payment_Id = table.Column<string>(type: "TEXT", nullable: false),
                    RialAmount = table.Column<long>(type: "INTEGER", nullable: false),
                    Result = table.Column<string>(type: "TEXT", nullable: true),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    TelMsgId = table.Column<long>(type: "INTEGER", nullable: false),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    TronAmount = table.Column<double>(type: "REAL", nullable: false),
                    UsdtAmount = table.Column<double>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwapinoPaymentInfo", x => x.Payment_Id);
                });
        }
    }
}
