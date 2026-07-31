using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds UniquePay audit/polling rows and tenant-specific UniquePay preference/order linkage to users.db.
    /// </summary>
    /// <remarks>
    /// Existing tenants receive an enabled local preference, but effective availability still requires the live global
    /// switch. The migration intentionally leaves credentials.db and all secret storage unchanged.
    /// </remarks>
    [DbContext(typeof(UserDbContext))]
    [Migration("20260731000000_AddUniquePayPayments")]
    public partial class AddUniquePayPayments : Migration
    {
        /// <summary>
        /// Creates UniquePay payment state, reconciliation indexes, and nullable tenant-order association.
        /// </summary>
        /// <param name="migrationBuilder">EF Core schema builder targeting users.db.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UniquePayPaymentInfoId",
                table: "TenantBotOrders",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "TenantUniquePayEnabled",
                table: "BotInstances",
                type: "INTEGER",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "UniquePayPaymentInfos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    HashId = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    RefId = table.Column<string>(type: "TEXT", maxLength: 180, nullable: true),
                    PaymentLink = table.Column<string>(type: "TEXT", nullable: true),
                    BaseAmountToman = table.Column<long>(type: "INTEGER", nullable: false),
                    ProviderAmountToman = table.Column<long>(type: "INTEGER", nullable: true),
                    ProviderFeeToman = table.Column<long>(type: "INTEGER", nullable: true),
                    FeePercent = table.Column<decimal>(type: "TEXT", nullable: false),
                    FeePayer = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    Currency = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    PaymentStatus = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsProviderVerified = table.Column<bool>(type: "INTEGER", nullable: false),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    TelMsgId = table.Column<long>(type: "INTEGER", nullable: true),
                    BotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    BotUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    PaymentPurpose = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TenantBotOrderId = table.Column<int>(type: "INTEGER", nullable: true),
                    TenantOwnerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    RawRequestJson = table.Column<string>(type: "TEXT", nullable: true),
                    RawResponseJson = table.Column<string>(type: "TEXT", nullable: true),
                    LastInquiryAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NextInquiryAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    InquiryAttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    PaidAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SettledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    IsAddedToBalance = table.Column<bool>(type: "INTEGER", nullable: false),
                    SettlementState = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "pending"),
                    SettlementAttemptId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SettlementStartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BalanceBefore = table.Column<long>(type: "INTEGER", nullable: true),
                    BalanceAfter = table.Column<long>(type: "INTEGER", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    LastErrorLoggedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SuccessLoggedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UniquePayPaymentInfos", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantBotOrders_UniquePayPaymentInfoId",
                table: "TenantBotOrders",
                column: "UniquePayPaymentInfoId");

            migrationBuilder.CreateIndex(
                name: "IX_UniquePayPaymentInfos_BotId",
                table: "UniquePayPaymentInfos",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_UniquePayPaymentInfos_HashId",
                table: "UniquePayPaymentInfos",
                column: "HashId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UniquePayPaymentInfos_IsAddedToBalance_SettlementState_NextInquiryAtUtc",
                table: "UniquePayPaymentInfos",
                columns: new[] { "IsAddedToBalance", "SettlementState", "NextInquiryAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_UniquePayPaymentInfos_PaymentPurpose",
                table: "UniquePayPaymentInfos",
                column: "PaymentPurpose");

            migrationBuilder.CreateIndex(
                name: "IX_UniquePayPaymentInfos_RefId",
                table: "UniquePayPaymentInfos",
                column: "RefId",
                unique: true,
                filter: "\"RefId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_UniquePayPaymentInfos_TelegramUserId",
                table: "UniquePayPaymentInfos",
                column: "TelegramUserId");

            migrationBuilder.CreateIndex(
                name: "IX_UniquePayPaymentInfos_TenantBotOrderId",
                table: "UniquePayPaymentInfos",
                column: "TenantBotOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_UniquePayPaymentInfos_TenantOwnerTelegramUserId",
                table: "UniquePayPaymentInfos",
                column: "TenantOwnerTelegramUserId");
        }

        /// <summary>
        /// Removes only UniquePay users.db schema and tenant linkage introduced by this migration.
        /// </summary>
        /// <param name="migrationBuilder">EF Core schema builder targeting users.db.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "UniquePayPaymentInfos");

            migrationBuilder.DropIndex(
                name: "IX_TenantBotOrders_UniquePayPaymentInfoId",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "UniquePayPaymentInfoId",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "TenantUniquePayEnabled",
                table: "BotInstances");
        }
    }
}
