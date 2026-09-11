using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds the three durable columns the same-client provisional finalization saga needs to be crash-safe.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>FinalizationEffectiveAtUtc</c>, <c>FinalQuotaBytes</c>, and <c>FinalExpiryTimeMs</c> are frozen exactly once per
    /// order. They must be durable because a restart has to rebuild the identical final panel write: recomputing the
    /// expiry from the current clock would extend the customer's paid period by the outage duration, and recomputing the
    /// quota from measured usage would drift the entitlement upward. Both are precisely the outcomes the finalization
    /// design forbids, so neither value can be derived on the fly.
    /// </para>
    /// <para>
    /// All three columns are nullable and additive. Existing rows keep null, which the saga reads as "final entitlement not
    /// yet frozen"; no historical order is backfilled, no existing column is rewritten, and no deployed migration history
    /// is edited. The table is scoped to provisional tenant card-to-card sagas, so this change touches no financial
    /// ledger, wallet, or settlement data.
    /// </para>
    /// </remarks>
    public partial class AddProvisionalFinalizationFreeze : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "FinalExpiryTimeMs",
                table: "TenantCardProvisionalOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "FinalQuotaBytes",
                table: "TenantCardProvisionalOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FinalizationEffectiveAtUtc",
                table: "TenantCardProvisionalOperations",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinalExpiryTimeMs",
                table: "TenantCardProvisionalOperations");

            migrationBuilder.DropColumn(
                name: "FinalQuotaBytes",
                table: "TenantCardProvisionalOperations");

            migrationBuilder.DropColumn(
                name: "FinalizationEffectiveAtUtc",
                table: "TenantCardProvisionalOperations");
        }
    }
}
