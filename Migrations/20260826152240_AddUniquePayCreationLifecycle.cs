using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds the durable one-attempt UniquePay creation lifecycle and repairs only deterministic tenant-order links.
    /// </summary>
    /// <remarks>
    /// Historical rows are classified from already persisted evidence without calling UniquePay, rescheduling an
    /// inquiry, changing a wallet/order settlement marker, or creating a provider invoice. A tenant order is linked
    /// only when exactly one historical UniquePay row identifies that order.
    /// </remarks>
    public partial class AddUniquePayCreationLifecycle : Migration
    {
        /// <summary>
        /// Adds creation-state columns and indexes, then safely classifies historical rows and fills unambiguous order
        /// foreign keys without any provider or financial side effect.
        /// </summary>
        /// <param name="migrationBuilder">EF Core builder for the users.db schema migration.</param>
        /// <remarks>
        /// Existing inquiry counters and schedules are deliberately preserved. Rows with paid/link/reference evidence
        /// are classified as created; terminal rows without creation evidence are failed; every other historical row
        /// remains ambiguous and can only use the existing read-only inquiry path.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CreationAttemptCount",
                table: "UniquePayPaymentInfos",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreationAttemptedAtUtc",
                table: "UniquePayPaymentInfos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreationErrorCode",
                table: "UniquePayPaymentInfos",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreationResolvedAtUtc",
                table: "UniquePayPaymentInfos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreationState",
                table: "UniquePayPaymentInfos",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "ambiguous");

            migrationBuilder.Sql(
                """
                UPDATE "UniquePayPaymentInfos"
                SET
                    "CreationAttemptCount" = CASE
                        WHEN "CreationAttemptCount" < 1 THEN 1
                        ELSE "CreationAttemptCount"
                    END,
                    "CreationAttemptedAtUtc" = COALESCE("CreationAttemptedAtUtc", "CreatedAtUtc"),
                    "CreationState" = CASE
                        WHEN "IsAddedToBalance" = 1
                             OR lower(COALESCE("PaymentStatus", '')) = 'paid'
                             OR trim(COALESCE("PaymentLink", '')) <> ''
                             OR trim(COALESCE("RefId", '')) <> ''
                            THEN 'created'
                        WHEN lower(COALESCE("PaymentStatus", '')) IN ('failed', 'expired', 'cancelled')
                            THEN 'failed'
                        ELSE 'ambiguous'
                    END,
                    "CreationResolvedAtUtc" = CASE
                        WHEN "IsAddedToBalance" = 1
                             OR lower(COALESCE("PaymentStatus", '')) = 'paid'
                             OR trim(COALESCE("PaymentLink", '')) <> ''
                             OR trim(COALESCE("RefId", '')) <> ''
                             OR lower(COALESCE("PaymentStatus", '')) IN ('failed', 'expired', 'cancelled')
                            THEN COALESCE("UpdatedAtUtc", "CreatedAtUtc")
                        ELSE NULL
                    END,
                    "CreationErrorCode" = CASE
                        WHEN "IsAddedToBalance" = 1
                             OR lower(COALESCE("PaymentStatus", '')) = 'paid'
                             OR trim(COALESCE("PaymentLink", '')) <> ''
                             OR trim(COALESCE("RefId", '')) <> ''
                            THEN NULL
                        WHEN lower(COALESCE("PaymentStatus", '')) IN ('failed', 'expired', 'cancelled')
                            THEN COALESCE(NULLIF(trim(COALESCE("ErrorCode", '')), ''), 'historical_create_failed')
                        ELSE 'historical_ambiguous_create'
                    END;

                UPDATE "TenantBotOrders"
                SET "UniquePayPaymentInfoId" = (
                    SELECT "payment"."Id"
                    FROM "UniquePayPaymentInfos" AS "payment"
                    WHERE "payment"."TenantBotOrderId" = "TenantBotOrders"."Id"
                    LIMIT 1
                )
                WHERE "UniquePayPaymentInfoId" IS NULL
                  AND (
                      SELECT COUNT(*)
                      FROM "UniquePayPaymentInfos" AS "payment"
                      WHERE "payment"."TenantBotOrderId" = "TenantBotOrders"."Id"
                  ) = 1;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_UniquePayPaymentInfos_CreationState_NextInquiryAtUtc",
                table: "UniquePayPaymentInfos",
                columns: new[] { "CreationState", "NextInquiryAtUtc" });
        }

        /// <summary>
        /// Removes only the creation-lifecycle schema added by this migration.
        /// </summary>
        /// <param name="migrationBuilder">EF Core builder for reverting the users.db schema.</param>
        /// <remarks>
        /// Deterministic tenant-order links filled during upgrade are retained because they restore an existing logical
        /// relationship and removing them could detach valid production payments from their orders.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UniquePayPaymentInfos_CreationState_NextInquiryAtUtc",
                table: "UniquePayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "CreationAttemptCount",
                table: "UniquePayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "CreationAttemptedAtUtc",
                table: "UniquePayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "CreationErrorCode",
                table: "UniquePayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "CreationResolvedAtUtc",
                table: "UniquePayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "CreationState",
                table: "UniquePayPaymentInfos");
        }
    }
}
