using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class HardenTenantOrderNotificationOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SendStartedAtUtc",
                table: "TenantOrderNotifications",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantOrderNotifications_Status_DeliveredAtUtc",
                table: "TenantOrderNotifications",
                columns: new[] { "Status", "DeliveredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantOrderNotifications_Status_DeliveredAtUtc",
                table: "TenantOrderNotifications");

            migrationBuilder.DropColumn(
                name: "SendStartedAtUtc",
                table: "TenantOrderNotifications");
        }
    }
}
