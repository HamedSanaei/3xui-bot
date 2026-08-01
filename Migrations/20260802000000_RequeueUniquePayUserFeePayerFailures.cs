using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Requeues unsettled UniquePay rows rejected before the provider's <c>user</c> buyer alias was supported.
    /// </summary>
    /// <remarks>
    /// This data-only migration never credits a wallet or fulfills a tenant order. Each row must receive a fresh
    /// authenticated inquiry and pass identity, paid-state, base, payable, unique-amount, fee, payer, and currency
    /// validation before any financial side effect is allowed.
    /// </remarks>
    [DbContext(typeof(UserDbContext))]
    [Migration("20260802000000_RequeueUniquePayUserFeePayerFailures")]
    public partial class RequeueUniquePayUserFeePayerFailures : Migration
    {
        /// <summary>
        /// Schedules eligible fee-payer-alias failures for immediate authoritative reconciliation.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder targeting the existing users.db database.</param>
        /// <remarks>
        /// Only uncredited failed rows whose settlement claim is still pending are changed. Unknown or inconsistent
        /// provider responses are rejected again by the corrected fail-closed verifier.
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
                  AND "ErrorCode" = 'provider_fee_payer_mismatch'
                  AND ("SettlementState" IS NULL OR "SettlementState" = 'pending');
                """);
        }

        /// <summary>
        /// Leaves any subsequently reconciled payment state unchanged during rollback.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder targeting users.db during rollback.</param>
        /// <remarks>
        /// The migration creates no schema object. Restoring the obsolete failed state could invalidate an officially
        /// settled payment, so rollback intentionally performs no data mutation.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
