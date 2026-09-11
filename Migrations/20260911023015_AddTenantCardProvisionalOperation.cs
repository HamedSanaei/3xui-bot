using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds the durable step-state table for tenant card-to-card provisional finalize and revoke sagas.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>TenantCardProvisionalOperations</c> is tenant-aggregate local state, not financial data. It records, per order
    /// and per operation kind, how far a post-receipt saga has durably progressed so a process restart can resume from
    /// proven evidence instead of replaying a panel mutation that may already have been applied.
    /// </para>
    /// <para>
    /// The table is keyed by <c>OperationKey</c> in <c>tenant-card-{finalize|revoke}:{publicOrderId}</c> form. Steps are
    /// forward-only; the rows carry no tokens, panel credentials, wallet amounts, ledger entries, or raw Telegram payloads.
    /// <c>EvidenceJson</c> holds only sanitized comparison values such as the proven quota, expiry, and enabled flag.
    /// </para>
    /// <para>
    /// This migration adds one new table with two indexes and touches no existing table, so no historical row is read,
    /// backfilled, rewritten, or reinterpreted. Existing provisional delivery columns on <c>TenantBotOrders</c> are
    /// untouched and every pre-existing order keeps its current null/legacy state.
    /// </para>
    /// <para>
    /// Retention: rows are append-only in business terms and act as a permanent audit trail of approved or revoked
    /// provisional deliveries. The <c>(Step, UpdatedAtUtc)</c> index exists so a recovery scan can find in-flight sagas
    /// without a table scan once the table grows.
    /// </para>
    /// </remarks>
    public partial class AddTenantCardProvisionalOperation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantCardProvisionalOperations",
                columns: table => new
                {
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Id = table.Column<int>(type: "INTEGER", nullable: false),
                    TenantBotOrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    OrderId = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Step = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    DisabledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ResetAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReDisabledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ZeroedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    QuotaWrittenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ProvenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EvidenceJson = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorCode = table.Column<string>(type: "TEXT", maxLength: 120, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantCardProvisionalOperations", x => x.OperationKey);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantCardProvisionalOperations_Step_UpdatedAtUtc",
                table: "TenantCardProvisionalOperations",
                columns: new[] { "Step", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantCardProvisionalOperations_TenantBotOrderId",
                table: "TenantCardProvisionalOperations",
                column: "TenantBotOrderId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantCardProvisionalOperations");
        }
    }
}
