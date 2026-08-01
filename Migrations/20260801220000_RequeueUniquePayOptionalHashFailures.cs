using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Requeues unsettled UniquePay inquiries rejected because production responses omitted the optional root hash echo.
    /// </summary>
    /// <remarks>
    /// This data-only migration does not credit a wallet or fulfill an order. The reconciliation worker performs a fresh
    /// authenticated inquiry and still rejects any returned conflicting hash, reference, amount, currency, fee, or payer.
    /// </remarks>
    [DbContext(typeof(UserDbContext))]
    [Migration("20260801220000_RequeueUniquePayOptionalHashFailures")]
    public partial class RequeueUniquePayOptionalHashFailures : Migration
    {
        /// <summary>
        /// Schedules eligible hash-echo failures for immediate authoritative reconciliation.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder targeting the existing users.db database.</param>
        /// <remarks>
        /// Only uncredited rows with a pending settlement claim are changed. A genuine identity conflict is harmlessly
        /// rejected again by the corrected verifier and cannot cause a wallet credit or tenant delivery.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
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
                  AND "ErrorCode" = 'provider_hash_id_mismatch'
                  AND ("SettlementState" IS NULL OR "SettlementState" = 'pending');
                """);
        }

        /// <summary>
        /// Leaves reconciled payment state unchanged when rolling the migration back.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder targeting users.db during rollback.</param>
        /// <remarks>
        /// Reverting payment status would be unsafe because the worker may already have officially settled a requeued row.
        /// The migration introduces no schema objects that require removal.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
