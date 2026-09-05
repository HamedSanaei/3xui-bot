using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations.CredentialsDb
{
    /// <inheritdoc />
    public partial class LinkWalletOperationsToInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "InboxSequence",
                table: "WalletOperations",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_WalletOperations_InboxSequence",
                table: "WalletOperations",
                column: "InboxSequence");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WalletOperations_InboxSequence",
                table: "WalletOperations");

            migrationBuilder.DropColumn(
                name: "InboxSequence",
                table: "WalletOperations");
        }
    }
}
