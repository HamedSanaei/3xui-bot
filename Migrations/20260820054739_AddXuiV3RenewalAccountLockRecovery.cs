using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds account-level unresolved-renewal locks and durable GET-only reconciliation scheduling.
    /// </summary>
    /// <remarks>
    /// Historical rows are normalized without debiting wallets, changing tenant orders, or calling the panel. Existing
    /// processing rows are conservatively treated as ambiguous because their POST may have started. If multiple
    /// unresolved historical rows identify the same account, all are parked in manual review and only the oldest owns
    /// the unique physical lock; lookup indexes still make every duplicate visible to operators and new-renewal checks.
    /// </remarks>
    public partial class AddXuiV3RenewalAccountLockRecovery : Migration
    {
        /// <summary>Adds recovery columns, safely classifies historical rows, and creates lock/recovery indexes.</summary>
        /// <param name="migrationBuilder">EF Core migration builder for the configured users.db SQLite database.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccountLockKey",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                maxLength: 240,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastReconcileAtUtc",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ManualReviewAtUtc",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "MutationStartedAtUtc",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextReconcileAtUtc",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedTargetEmail",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedTargetUuid",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReconcileAttemptCount",
                table: "XuiV3RenewalOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "RecoveryClaimToken",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecoveryLeaseUntilUtc",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                nullable: true);

            // This backfill is deliberately metadata-only. It neither assumes that an ambiguous mutation failed nor
            // repairs/debits any historical settlement. Processing rows may have crossed the network boundary, so
            // they become GET-only ambiguous work and retain a lock.
            migrationBuilder.Sql(
                """
                UPDATE "XuiV3RenewalOperations"
                SET "NormalizedTargetEmail" = lower(trim(COALESCE("TargetEmail", ''))),
                    "NormalizedTargetUuid" = CASE
                        WHEN length(trim(COALESCE("TargetUuid", ''))) = 36
                            THEN lower(trim("TargetUuid"))
                        ELSE ''
                    END;

                UPDATE "XuiV3RenewalOperations"
                SET "MutationStartedAtUtc" = COALESCE("UpdatedAtUtc", "CreatedAtUtc"),
                    "Status" = 'ambiguous',
                    "NextReconcileAtUtc" = CURRENT_TIMESTAMP,
                    "LastError" = 'Historical processing operation requires GET-only reconciliation.'
                WHERE "Status" = 'processing';

                UPDATE "XuiV3RenewalOperations" AS candidate
                SET "Status" = 'manual_review',
                    "ManualReviewAtUtc" = CURRENT_TIMESTAMP,
                    "NextReconcileAtUtc" = NULL,
                    "LastError" = 'Historical duplicate unresolved renewal requires manual review.'
                WHERE (
                        candidate."Status" IN ('pending', 'processing', 'ambiguous', 'manual_review')
                        OR (candidate."Status" = 'applied' AND candidate."SettlementStatus" <> 'settled')
                      )
                  AND (
                    SELECT COUNT(*)
                    FROM "XuiV3RenewalOperations" AS duplicate
                    WHERE (
                            duplicate."Status" IN ('pending', 'processing', 'ambiguous', 'manual_review')
                            OR (duplicate."Status" = 'applied' AND duplicate."SettlementStatus" <> 'settled')
                          )
                      AND CASE
                            WHEN candidate."NormalizedTargetUuid" <> '' AND duplicate."NormalizedTargetUuid" <> ''
                                THEN candidate."NormalizedTargetUuid" = duplicate."NormalizedTargetUuid"
                            ELSE candidate."NormalizedTargetEmail" <> ''
                                 AND candidate."NormalizedTargetEmail" = duplicate."NormalizedTargetEmail"
                          END
                  ) > 1;

                UPDATE "XuiV3RenewalOperations" AS candidate
                SET "AccountLockKey" = CASE
                        WHEN candidate."NormalizedTargetUuid" <> ''
                            THEN 'uuid:' || candidate."NormalizedTargetUuid"
                        WHEN candidate."NormalizedTargetEmail" <> ''
                            THEN 'email:' || candidate."NormalizedTargetEmail"
                        ELSE NULL
                    END
                WHERE (
                        candidate."Status" IN ('pending', 'processing', 'ambiguous', 'manual_review')
                        OR (candidate."Status" = 'applied' AND candidate."SettlementStatus" <> 'settled')
                      )
                  AND candidate."Id" = (
                    SELECT MIN(owner."Id")
                    FROM "XuiV3RenewalOperations" AS owner
                    WHERE (
                            owner."Status" IN ('pending', 'processing', 'ambiguous', 'manual_review')
                            OR (owner."Status" = 'applied' AND owner."SettlementStatus" <> 'settled')
                          )
                      AND CASE
                            WHEN candidate."NormalizedTargetUuid" <> '' AND owner."NormalizedTargetUuid" <> ''
                                THEN candidate."NormalizedTargetUuid" = owner."NormalizedTargetUuid"
                            ELSE candidate."NormalizedTargetEmail" <> ''
                                 AND candidate."NormalizedTargetEmail" = owner."NormalizedTargetEmail"
                          END
                  );
                """);

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3RenewalOperations_AccountLockKey",
                table: "XuiV3RenewalOperations",
                column: "AccountLockKey",
                unique: true,
                filter: "\"AccountLockKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3RenewalOperations_NormalizedTargetEmail_Status_SettlementStatus",
                table: "XuiV3RenewalOperations",
                columns: new[] { "NormalizedTargetEmail", "Status", "SettlementStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3RenewalOperations_NormalizedTargetUuid_Status_SettlementStatus",
                table: "XuiV3RenewalOperations",
                columns: new[] { "NormalizedTargetUuid", "Status", "SettlementStatus" });

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3RenewalOperations_Status_NextReconcileAtUtc_RecoveryLeaseUntilUtc",
                table: "XuiV3RenewalOperations",
                columns: new[] { "Status", "NextReconcileAtUtc", "RecoveryLeaseUntilUtc" });
        }

        /// <summary>Removes recovery metadata and indexes without changing historical renewal or financial rows.</summary>
        /// <param name="migrationBuilder">EF Core migration builder for the configured users.db SQLite database.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_XuiV3RenewalOperations_AccountLockKey",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropIndex(
                name: "IX_XuiV3RenewalOperations_NormalizedTargetEmail_Status_SettlementStatus",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropIndex(
                name: "IX_XuiV3RenewalOperations_NormalizedTargetUuid_Status_SettlementStatus",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropIndex(
                name: "IX_XuiV3RenewalOperations_Status_NextReconcileAtUtc_RecoveryLeaseUntilUtc",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "AccountLockKey",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "LastReconcileAtUtc",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "ManualReviewAtUtc",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "MutationStartedAtUtc",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "NextReconcileAtUtc",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "NormalizedTargetEmail",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "NormalizedTargetUuid",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "ReconcileAttemptCount",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "RecoveryClaimToken",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "RecoveryLeaseUntilUtc",
                table: "XuiV3RenewalOperations");
        }
    }
}
