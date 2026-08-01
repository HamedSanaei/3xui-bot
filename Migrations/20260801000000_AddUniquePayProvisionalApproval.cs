using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds durable UniquePay provisional-approval audit fields and requeues unsettled rows rejected by the former contract.
    /// </summary>
    /// <remarks>
    /// The recovery update affects only uncredited failed rows whose error was produced by the former IRT/buyer-only
    /// validator. Financially settled rows and all tenant fulfillment state remain unchanged.
    /// </remarks>
    [DbContext(typeof(UserDbContext))]
    [Migration("20260801000000_AddUniquePayProvisionalApproval")]
    public partial class AddUniquePayProvisionalApproval : Migration
    {
        /// <summary>
        /// Adds provisional approval audit columns and schedules eligible legacy failures for an authoritative inquiry.
        /// </summary>
        /// <param name="migrationBuilder">EF Core schema builder targeting the existing users.db database.</param>
        /// <remarks>
        /// Requeued rows are not credited by this migration. The UniquePay reconciliation worker must still receive and
        /// validate a paid provider response before normal settlement can change any wallet or tenant order.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsProvisionallyApproved",
                table: "UniquePayPaymentInfos",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProvisionalApprovedAtUtc",
                table: "UniquePayPaymentInfos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProvisionalApprovedByTelegramUserId",
                table: "UniquePayPaymentInfos",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderConfirmedAfterProvisionalAtUtc",
                table: "UniquePayPaymentInfos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE "UniquePayPaymentInfos"
                SET "PaymentStatus" = 'pending',
                    "NextInquiryAtUtc" = CURRENT_TIMESTAMP,
                    "ErrorCode" = NULL,
                    "ErrorMessage" = NULL,
                    "UpdatedAtUtc" = CURRENT_TIMESTAMP
                WHERE "IsAddedToBalance" = 0
                  AND "PaymentStatus" = 'failed'
                  AND "ErrorCode" IN ('provider_currency_mismatch', 'provider_fee_payer_mismatch')
                  AND ("SettlementState" IS NULL OR "SettlementState" = 'pending');
                """);
        }

        /// <summary>
        /// Removes only the provisional-approval audit columns introduced by this migration.
        /// </summary>
        /// <param name="migrationBuilder">EF Core schema builder targeting users.db during rollback.</param>
        /// <remarks>
        /// Requeued payment statuses are intentionally not reverted because a row may have been officially reconciled
        /// after deployment; restoring its obsolete validation failure would corrupt current financial audit state.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // EF Core's generic SQLite generator rejects DropColumnOperation for this hand-authored migration.
            // The bundled SQLite supports native DROP COLUMN, which preserves every unrelated payment column/index.
            migrationBuilder.Sql("ALTER TABLE \"UniquePayPaymentInfos\" DROP COLUMN \"IsProvisionallyApproved\";");
            migrationBuilder.Sql("ALTER TABLE \"UniquePayPaymentInfos\" DROP COLUMN \"ProvisionalApprovedAtUtc\";");
            migrationBuilder.Sql("ALTER TABLE \"UniquePayPaymentInfos\" DROP COLUMN \"ProvisionalApprovedByTelegramUserId\";");
            migrationBuilder.Sql("ALTER TABLE \"UniquePayPaymentInfos\" DROP COLUMN \"ProviderConfirmedAfterProvisionalAtUtc\";");
        }
    }
}
