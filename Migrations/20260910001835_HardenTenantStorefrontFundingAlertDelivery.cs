using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class HardenTenantStorefrontFundingAlertDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "EpisodeNumber",
                table: "TenantStorefrontFundingAlerts",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "SendStartedAtUtc",
                table: "TenantStorefrontFundingAlerts",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantStorefrontFundingAlerts_Status_DeliveredAtUtc",
                table: "TenantStorefrontFundingAlerts",
                columns: new[] { "Status", "DeliveredAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantStorefrontFundingAlerts_Status_DeliveredAtUtc",
                table: "TenantStorefrontFundingAlerts");

            migrationBuilder.DropColumn(
                name: "EpisodeNumber",
                table: "TenantStorefrontFundingAlerts");

            migrationBuilder.DropColumn(
                name: "SendStartedAtUtc",
                table: "TenantStorefrontFundingAlerts");
        }
    }
}
