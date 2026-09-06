using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>Adds explicit creation outcomes and sanitized operator review evidence without replaying historical effects.</summary>
    public partial class AddCreationOutcomesAndInboxReview : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Outcome",
                table: "XuiV3CreationOperations",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PostStartedAtUtc",
                table: "XuiV3CreationOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReviewReference",
                table: "TelegramUpdateInbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ReviewedAtUtc",
                table: "TelegramUpdateInbox",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ReviewedByTelegramUserId",
                table: "TelegramUpdateInbox",
                type: "INTEGER",
                nullable: true);
            // Historical null AppliedAtUtc cannot prove that addClient never crossed the mutation boundary.
            migrationBuilder.Sql("UPDATE XuiV3CreationOperations SET Outcome = CASE WHEN AppliedAtUtc IS NOT NULL THEN 2 ELSE 4 END");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Outcome",
                table: "XuiV3CreationOperations");

            migrationBuilder.DropColumn(
                name: "PostStartedAtUtc",
                table: "XuiV3CreationOperations");

            migrationBuilder.DropColumn(
                name: "ReviewReference",
                table: "TelegramUpdateInbox");

            migrationBuilder.DropColumn(
                name: "ReviewedAtUtc",
                table: "TelegramUpdateInbox");

            migrationBuilder.DropColumn(
                name: "ReviewedByTelegramUserId",
                table: "TelegramUpdateInbox");
        }
    }
}
