using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class AddXuiV3RenewalManualReviewLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ManualReviewNotifiedAtUtc",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ManualReviewResolution",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ManualReviewResolvedAtUtc",
                table: "XuiV3RenewalOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ManualReviewResolvedByTelegramUserId",
                table: "XuiV3RenewalOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3RenewalOperations_Status_ManualReviewNotifiedAtUtc",
                table: "XuiV3RenewalOperations",
                columns: new[] { "Status", "ManualReviewNotifiedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_XuiV3RenewalOperations_Status_ManualReviewNotifiedAtUtc",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "ManualReviewNotifiedAtUtc",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "ManualReviewResolution",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "ManualReviewResolvedAtUtc",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "ManualReviewResolvedByTelegramUserId",
                table: "XuiV3RenewalOperations");
        }
    }
}
