using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Requeues provider-paid UniquePay rows that remained unsettled because a stale singleton change tracker hid the
    /// paid state from the owned-wallet settlement service.
    /// </summary>
    /// <remarks>
    /// This data-only migration never credits a wallet and does not trust the saved paid flag as a new payment proof.
    /// It schedules one fresh authenticated provider inquiry; normal identity, currency, fee, amount, paid-state, and
    /// idempotent settlement checks still run before any credentials.db or wallet-ledger mutation.
    /// </remarks>
    [DbContext(typeof(UserDbContext))]
    [Migration("20260803180000_RequeueUniquePayVerifiedUnsettled")]
    public partial class RequeueUniquePayVerifiedUnsettled : Migration
    {
        /// <summary>
        /// Schedules safely unclaimed, provider-paid UniquePay rows for immediate reconciliation after deployment.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder targeting the existing users.db database.</param>
        /// <remarks>
        /// Only rows with no wallet credit and no active or ambiguous settlement claim are selected. Rows such as
        /// <c>UP:16</c> are re-inquired rather than directly credited, preserving fail-closed financial verification.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE "UniquePayPaymentInfos"
                SET "NextInquiryAtUtc" = CURRENT_TIMESTAMP,
                    "ErrorCode" = NULL,
                    "ErrorMessage" = NULL,
                    "UpdatedAtUtc" = CURRENT_TIMESTAMP
                WHERE "IsAddedToBalance" = 0
                  AND "PaymentStatus" = 'paid'
                  AND "PaidAtUtc" IS NOT NULL
                  AND ("SettlementState" IS NULL OR "SettlementState" = 'pending');
                """);
        }

        /// <summary>
        /// Leaves reconciliation and settlement outcomes unchanged when the migration is rolled back.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder targeting users.db during rollback.</param>
        /// <remarks>
        /// The migration creates no schema object. Removing a schedule after it may have triggered an authenticated
        /// inquiry could hide a valid unsettled payment, so rollback intentionally performs no data mutation.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
