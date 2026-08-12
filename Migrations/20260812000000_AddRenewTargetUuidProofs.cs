using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds bot-scoped and tenant-order UUID proofs used to authorize renewal without transferring account ownership.
    /// </summary>
    /// <remarks>
    /// Existing state rows and tenant orders remain null and keep their owner-only behavior. No backfill is possible or
    /// required because a UUID proof must originate from a newly validated Telegram input or restricted search result.
    /// The migration changes only <c>users.db</c>; credentials, wallets, ledgers, payments, and XUI clients are untouched.
    /// </remarks>
    [DbContext(typeof(UserDbContext))]
    [Migration("20260812000000_AddRenewTargetUuidProofs")]
    public partial class AddRenewTargetUuidProofs : Migration
    {
        /// <summary>
        /// Adds nullable UUID proof columns to bot conversation state and tenant renewal orders.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder targeting the configured users.db database.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RenewTargetUuid",
                table: "BotUserStates",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetAccountUuid",
                table: "TenantBotOrders",
                type: "TEXT",
                maxLength: 64,
                nullable: true);
        }

        /// <summary>
        /// Removes the UUID proof columns without changing any account, payment, wallet, or order identity fields.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder targeting users.db during rollback.</param>
        /// <remarks>
        /// After rollback, pending external UUID renewals cannot be completed safely and must be restarted through an
        /// owner-authorized flow. Existing owner-only renewal orders retain their legacy behavior.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "RenewTargetUuid", table: "BotUserStates");
            migrationBuilder.DropColumn(name: "TargetAccountUuid", table: "TenantBotOrders");
        }
    }
}
