using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds account-level renewal locks, a post-migration recovery eligibility boundary, and GET-only scheduling.
    /// </summary>
    /// <remarks>
    /// Every pre-existing unresolved operation is quarantined in manual review with <c>RecoveryEligible = 0</c>.
    /// The migration performs metadata-only writes: it never calls XUI, debits a wallet, appends a financial ledger,
    /// settles a tenant order, or infers that an old mutation succeeded. Only runtime operations inserted after this
    /// migration explicitly set recovery eligibility to true.
    /// </remarks>
    public partial class AddXuiV3RenewalRecoverySafetyBoundary : Migration
    {
        /// <summary>Adds recovery columns, quarantines historical unresolved rows, and creates lookup/lock indexes.</summary>
        /// <param name="migrationBuilder">EF Core migration builder for the users.db SQLite schema.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AccountLockKey",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                maxLength: 240,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExpectedInboundIdsJson",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                maxLength: 2000,
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

            migrationBuilder.AddColumn<bool>(
                name: "RecoveryEligible",
                table: "XuiV3RenewalOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "RecoveryLeaseUntilUtc",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                nullable: true);

            // Historical operations predate the durable mutation-start protocol. Even an old Applied row may have an
            // unsettled financial side effect, so all unresolved states are permanently quarantined. No financial
            // table is touched here and RecoveryEligible intentionally retains its false database default.
            migrationBuilder.Sql(
                """
                UPDATE "XuiV3RenewalOperations"
                SET "NormalizedTargetEmail" = lower(trim(COALESCE("TargetEmail", ''))),
                    "NormalizedTargetUuid" = CASE
                        WHEN length(trim(COALESCE("TargetUuid", ''))) = 36
                            THEN lower(trim("TargetUuid"))
                        ELSE ''
                    END,
                    "Status" = 'manual_review',
                    "ManualReviewAtUtc" = CURRENT_TIMESTAMP,
                    "NextReconcileAtUtc" = NULL,
                    "RecoveryEligible" = 0,
                    "LastError" = 'Historical unresolved renewal quarantined; explicit administrator review required.'
                WHERE "Status" IN ('pending', 'processing', 'ambiguous', 'manual_review')
                   OR ("Status" = 'applied' AND "SettlementStatus" <> 'settled');

                UPDATE "XuiV3RenewalOperations" AS candidate
                SET "AccountLockKey" = CASE
                        WHEN candidate."NormalizedTargetUuid" <> ''
                            THEN 'uuid:' || candidate."NormalizedTargetUuid"
                        WHEN candidate."NormalizedTargetEmail" <> ''
                            THEN 'email:' || candidate."NormalizedTargetEmail"
                        ELSE NULL
                    END
                WHERE candidate."Status" = 'manual_review'
                  AND candidate."Id" = (
                    SELECT MIN(owner."Id")
                    FROM "XuiV3RenewalOperations" AS owner
                    WHERE owner."Status" = 'manual_review'
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

        /// <summary>Removes recovery metadata/indexes without changing renewal, order, wallet, or ledger history.</summary>
        /// <param name="migrationBuilder">EF Core migration builder for the users.db SQLite schema.</param>
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
                name: "ExpectedInboundIdsJson",
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
                name: "RecoveryEligible",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "RecoveryLeaseUntilUtc",
                table: "XuiV3RenewalOperations");
        }
    }
}
