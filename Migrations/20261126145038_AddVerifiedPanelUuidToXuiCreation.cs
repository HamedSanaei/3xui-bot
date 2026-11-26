using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>Retains authenticated panel UUID evidence separately from the immutable pre-POST request.</summary>
    /// <remarks>Existing creation rows receive null. No backfill, provider read, review, or financial operation occurs.</remarks>
    public partial class AddVerifiedPanelUuidToXuiCreation : Migration
    {
        /// <summary>Adds optional evidence on the existing operation key without changing its ownership or indexes.</summary>
        /// <param name="migrationBuilder">EF migration builder for users.db.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "VerifiedPanelUuid",
                table: "XuiV3CreationOperations",
                type: "TEXT",
                maxLength: 36,
                nullable: true);
        }

        /// <summary>Removes only the supplemental UUID evidence on explicit downgrade.</summary>
        /// <param name="migrationBuilder">EF migration builder for users.db.</param>
        /// <remarks>Order and original reservation survive; use a forward fix to retain proof for pending reviews.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "VerifiedPanelUuid",
                table: "XuiV3CreationOperations");
        }
    }
}
