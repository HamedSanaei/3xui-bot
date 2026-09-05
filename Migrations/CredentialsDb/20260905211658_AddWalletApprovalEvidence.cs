using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations.CredentialsDb
{
    /// <inheritdoc />
    public partial class AddWalletApprovalEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ApprovalKind",
                table: "WalletOperations",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ApprovedByTelegramUserId",
                table: "WalletOperations",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovalKind",
                table: "WalletOperations");

            migrationBuilder.DropColumn(
                name: "ApprovedByTelegramUserId",
                table: "WalletOperations");
        }
    }
}
