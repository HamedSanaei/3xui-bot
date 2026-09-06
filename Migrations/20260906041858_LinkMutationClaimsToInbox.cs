using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class LinkMutationClaimsToInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "InboxSequence",
                table: "XuiV3RenewalOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "InboxSequence",
                table: "XuiV3LinkChangeOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3RenewalOperations_InboxSequence",
                table: "XuiV3RenewalOperations",
                column: "InboxSequence");

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3LinkChangeOperations_InboxSequence",
                table: "XuiV3LinkChangeOperations",
                column: "InboxSequence");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_XuiV3RenewalOperations_InboxSequence",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropIndex(
                name: "IX_XuiV3LinkChangeOperations_InboxSequence",
                table: "XuiV3LinkChangeOperations");

            migrationBuilder.DropColumn(
                name: "InboxSequence",
                table: "XuiV3RenewalOperations");

            migrationBuilder.DropColumn(
                name: "InboxSequence",
                table: "XuiV3LinkChangeOperations");
        }
    }
}
