using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>Adds explicit administrator review audits and pre-dispatch markers for same-client provisional purchase recovery.</summary>
    /// <remarks>Additive only: existing steps, frozen entitlements and financial rows are untouched. No backfill,
    /// automatic approvals, panel requests or settlement occur. Nullable ReviewedRecoveryId leaves historical rows locked.</remarks>
    public partial class AddTenantCardProvisionalReviewedRecovery : Migration
    {
        /// <summary>Adds nullable proof/audit fields and an empty, logically order-bound review table with lookup indexes.</summary>
        /// <param name="migrationBuilder">EF schema builder; must run against the normal users.db migration pipeline.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastMutationStage",
                table: "TenantCardProvisionalOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastProvenStep",
                table: "TenantCardProvisionalOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MutationPending",
                table: "TenantCardProvisionalOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "ReviewedRecoveryId",
                table: "TenantCardProvisionalOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantCardProvisionalReviews",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    TenantBotOrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    ActorTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    BotId = table.Column<string>(type: "TEXT", nullable: true),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    BindingHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    SnapshotHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ExpectedInboundIdsJson = table.Column<string>(type: "TEXT", nullable: true),
                    ResumeStep = table.Column<string>(type: "TEXT", nullable: true),
                    OriginalReason = table.Column<string>(type: "TEXT", nullable: true),
                    EvidenceJson = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", nullable: true),
                    OutcomeCode = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AuthorizedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantCardProvisionalReviews", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantCardProvisionalReviews_Status_ExpiresAtUtc",
                table: "TenantCardProvisionalReviews",
                columns: new[] { "Status", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantCardProvisionalReviews_TenantBotOrderId_CreatedAtUtc",
                table: "TenantCardProvisionalReviews",
                columns: new[] { "TenantBotOrderId", "CreatedAtUtc" });
        }

        /// <summary>Removes review schema only; operators must preserve audit backups and never use old binaries to retry reviewed work.</summary>
        /// <param name="migrationBuilder">EF schema builder; down migration discards audit/uncertainty metadata and is not an operational recovery method.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantCardProvisionalReviews");

            migrationBuilder.DropColumn(
                name: "LastMutationStage",
                table: "TenantCardProvisionalOperations");

            migrationBuilder.DropColumn(
                name: "LastProvenStep",
                table: "TenantCardProvisionalOperations");

            migrationBuilder.DropColumn(
                name: "MutationPending",
                table: "TenantCardProvisionalOperations");

            migrationBuilder.DropColumn(
                name: "ReviewedRecoveryId",
                table: "TenantCardProvisionalOperations");
        }
    }
}
