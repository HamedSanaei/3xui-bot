using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds nullable volume-reminder eligibility diagnostics and durable GET-only probe backoff timestamps.
    /// </summary>
    /// <remarks>
    /// Existing cycle numbers, observed usage, handled thresholds, delivery claims, and message ids are preserved.
    /// The migration performs no backfill, panel request, Telegram send, wallet operation, or account mutation.
    /// </remarks>
    public partial class AddXuiV3VolumeReminderEligibilityDiagnostics : Migration
    {
        /// <summary>
        /// Adds the latest sanitized decision code/summary and nullable probe timestamps to reminder state rows.
        /// </summary>
        /// <param name="migrationBuilder">EF Core builder for the tenant-independent <c>users.db</c> schema.</param>
        /// <remarks>All four columns are nullable so historical rows remain unchanged until their next normal scan.</remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastEligibilityCode",
                table: "XuiV3VolumeReminderStates",
                type: "TEXT",
                maxLength: 48,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastEligibilityProbeAtUtc",
                table: "XuiV3VolumeReminderStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LastEligibilitySummary",
                table: "XuiV3VolumeReminderStates",
                type: "TEXT",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "NextEligibilityProbeAtUtc",
                table: "XuiV3VolumeReminderStates",
                type: "TEXT",
                nullable: true);
        }

        /// <summary>
        /// Removes only the eligibility diagnostic and probe-backoff columns added by this migration.
        /// </summary>
        /// <param name="migrationBuilder">EF Core builder for the tenant-independent <c>users.db</c> schema.</param>
        /// <remarks>Reminder cycles, handled thresholds, usage observations, and delivery history remain intact.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastEligibilityCode",
                table: "XuiV3VolumeReminderStates");

            migrationBuilder.DropColumn(
                name: "LastEligibilityProbeAtUtc",
                table: "XuiV3VolumeReminderStates");

            migrationBuilder.DropColumn(
                name: "LastEligibilitySummary",
                table: "XuiV3VolumeReminderStates");

            migrationBuilder.DropColumn(
                name: "NextEligibilityProbeAtUtc",
                table: "XuiV3VolumeReminderStates");
        }
    }
}
