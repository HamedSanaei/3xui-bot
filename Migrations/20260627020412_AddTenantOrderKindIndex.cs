using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds the order-kind index used by tenant purchase and renewal reports.
    /// </summary>
    public partial class AddTenantOrderKindIndex : Migration
    {
        /// <summary>
        /// Creates the index on <c>TenantBotOrders.OrderKind</c>.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder for users.db schema changes.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_TenantBotOrders_OrderKind",
                table: "TenantBotOrders",
                column: "OrderKind");
        }

        /// <summary>
        /// Removes the order-kind index during rollback.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder for users.db rollback changes.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantBotOrders_OrderKind",
                table: "TenantBotOrders");
        }
    }
}
