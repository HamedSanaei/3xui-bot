using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds audit-only columns that distinguish a super-admin provisional HooshPay wallet credit from the later
    /// official provider confirmation.
    /// </summary>
    /// <remarks>
    /// The columns belong only to <c>users.db</c>. Existing payment rows remain non-provisional by default, and this
    /// migration intentionally does not touch the shared <c>credentials.db</c> schema.
    /// </remarks>
    public partial class AddHooshPayProvisionalApproval : Migration
    {
        /// <summary>
        /// Adds provisional approval actor/time fields and the one-time provider reconciliation timestamp.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder targeting the application's users.db schema.</param>
        /// <remarks>
        /// A false default preserves the meaning of all historical HooshPay rows and prevents a historical paid invoice
        /// from being interpreted as a provisional approval.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsProvisionallyApproved",
                table: "HooshPayPaymentInfos",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderConfirmedAfterProvisionalAtUtc",
                table: "HooshPayPaymentInfos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProvisionalApprovedAtUtc",
                table: "HooshPayPaymentInfos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProvisionalApprovedByTelegramUserId",
                table: "HooshPayPaymentInfos",
                type: "INTEGER",
                nullable: true);
        }

        /// <summary>
        /// Removes the provisional HooshPay audit columns from users.db.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder targeting the application's users.db schema.</param>
        /// <remarks>
        /// Rolling back this migration discards provisional approval audit metadata only; it never reverses wallet or
        /// ledger movements that were already persisted by the financial settlement flow.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsProvisionallyApproved",
                table: "HooshPayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "ProviderConfirmedAfterProvisionalAtUtc",
                table: "HooshPayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "ProvisionalApprovedAtUtc",
                table: "HooshPayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "ProvisionalApprovedByTelegramUserId",
                table: "HooshPayPaymentInfos");
        }
    }
}
