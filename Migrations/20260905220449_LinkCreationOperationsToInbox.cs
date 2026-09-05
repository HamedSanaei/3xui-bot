using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class LinkCreationOperationsToInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "InboxSequence",
                table: "XuiV3CreationOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3CreationOperations_InboxSequence",
                table: "XuiV3CreationOperations",
                column: "InboxSequence");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_XuiV3CreationOperations_InboxSequence",
                table: "XuiV3CreationOperations");

            migrationBuilder.DropColumn(
                name: "InboxSequence",
                table: "XuiV3CreationOperations");
        }
    }
}
