using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class swapinopaymentinfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SwapinoPaymentInfo",
                columns: table => new
                {
                    PaymentId = table.Column<string>(name: "Payment_Id", type: "TEXT", nullable: false),
                    Result = table.Column<string>(type: "TEXT", nullable: true),
                    CallbackUrl = table.Column<string>(type: "TEXT", nullable: true),
                    RialAmount = table.Column<long>(type: "INTEGER", nullable: false),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    TronAmount = table.Column<double>(type: "REAL", nullable: false),
                    UsdtAmount = table.Column<double>(type: "REAL", nullable: false),
                    TelMsgId = table.Column<long>(type: "INTEGER", nullable: false),
                    IsAddedToBallance = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SwapinoPaymentInfo", x => x.PaymentId);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SwapinoPaymentInfo");
        }
    }
}
