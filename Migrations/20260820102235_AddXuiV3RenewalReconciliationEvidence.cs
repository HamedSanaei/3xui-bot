using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds durable pre-mutation evidence and sanitized detailed comparison fields for GET-only renewal recovery.
    /// </summary>
    /// <remarks>
    /// Existing rows receive no reconstructed snapshot and no status, settlement, wallet, or lock changes. A null
    /// snapshot intentionally prevents historical/in-flight rows from being auto-unlocked as DefinitelyPreMutation;
    /// they may still prove Applied from their previously stored absolute target under the existing eligibility gate.
    /// </remarks>
    public partial class AddXuiV3RenewalReconciliationEvidence : Migration
    {
        /// <summary>Adds nullable evidence columns plus a zero observation-count default without backfilling finance.</summary>
        /// <param name="migrationBuilder">EF migration builder targeting the global users.db schema.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "FirstPreMutationObservedAtUtc",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastComparisonOutcome",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastMismatchSummary",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PreMutationObservationCount",
                table: "XuiV3RenewalOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PreMutationSnapshotJson",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                maxLength: 8000,
                nullable: true);
        }

        /// <summary>Removes only the reconciliation-evidence columns added by this migration.</summary>
        /// <param name="migrationBuilder">EF migration builder targeting the global users.db schema.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FirstPreMutationObservedAtUtc",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "LastComparisonOutcome",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "LastMismatchSummary",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "PreMutationObservationCount",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "PreMutationSnapshotJson",
                table: "XuiV3RenewalOperations");
        }
    }
}
