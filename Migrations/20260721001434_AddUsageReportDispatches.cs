using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds users.db-only delivery state for idempotent aggregate weekly usage reports.
    /// </summary>
    /// <remarks>
    /// Existing installations require no backfill. Rows are created lazily when a completed-week report is first due;
    /// the unique report key is the final duplicate-delivery claim guard. This migration does not target credentials.db.
    /// </remarks>
    public partial class AddUsageReportDispatches : Migration
    {
        /// <summary>Creates the report dispatch table and its unique claim and retry lookup indexes.</summary>
        /// <param name="migrationBuilder">EF Core builder for the configured users.db migration transaction.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "UsageReportDispatches",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReportKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    PeriodStartUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PeriodEndUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LeaseUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    TelegramMessageId = table.Column<int>(type: "INTEGER", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    SentAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UsageReportDispatches", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UsageReportDispatches_PeriodEndUtc",
                table: "UsageReportDispatches",
                column: "PeriodEndUtc");

            migrationBuilder.CreateIndex(
                name: "IX_UsageReportDispatches_ReportKey",
                table: "UsageReportDispatches",
                column: "ReportKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UsageReportDispatches_Status_LeaseUntilUtc",
                table: "UsageReportDispatches",
                columns: new[] { "Status", "LeaseUntilUtc" });
        }

        /// <summary>Removes only the scheduled report dispatch table from users.db.</summary>
        /// <param name="migrationBuilder">EF Core builder for the configured users.db rollback transaction.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UsageReportDispatches");
        }
    }
}
