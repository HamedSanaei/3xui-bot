using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds tenant tutorial storage and renewal-order metadata to <c>users.db</c>.
    /// </summary>
    /// <remarks>
    /// The migration is intentionally scoped to the user database. Tenant tutorials are owned by one
    /// <c>BotInstance</c>, and renewal metadata lets tenant storefront payments update an existing XUI client
    /// without changing <c>credentials.db</c>.
    /// </remarks>
    [DbContext(typeof(UserDbContext))]
    [Migration("20260627010000_AddTenantTutorialsAndRenewOrders")]
    public partial class AddTenantTutorialsAndRenewOrders : Migration
    {
        /// <summary>
        /// Adds nullable tenant tutorial JSON and renewal target columns.
        /// </summary>
        /// <param name="migrationBuilder">
        /// EF Core migration builder for users.db schema changes.
        /// </param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TenantTutorialsJson",
                table: "BotInstances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OrderKind",
                table: "TenantBotOrders",
                type: "TEXT",
                maxLength: 32,
                nullable: true,
                defaultValue: "purchase");

            migrationBuilder.AddColumn<string>(
                name: "TargetAccountEmail",
                table: "TenantBotOrders",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

        }

        /// <summary>
        /// Removes the tenant tutorial and renewal-order columns added by this migration.
        /// </summary>
        /// <param name="migrationBuilder">
        /// EF Core migration builder for users.db rollback changes.
        /// </param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantTutorialsJson",
                table: "BotInstances");

            migrationBuilder.DropColumn(
                name: "OrderKind",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "TargetAccountEmail",
                table: "TenantBotOrders");
        }
    }
}
