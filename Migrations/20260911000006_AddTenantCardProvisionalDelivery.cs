using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantCardProvisionalDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProvisionalAccountEmail",
                table: "TenantBotOrders",
                type: "TEXT",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProvisionalAccountUuid",
                table: "TenantBotOrders",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProvisionalCreatedAtUtc",
                table: "TenantBotOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProvisionalDeliveredAtUtc",
                table: "TenantBotOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProvisionalDeliveryState",
                table: "TenantBotOrders",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "none");

            migrationBuilder.AddColumn<string>(
                name: "ProvisionalErrorCode",
                table: "TenantBotOrders",
                type: "TEXT",
                maxLength: 96,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProvisionalFinalizedAtUtc",
                table: "TenantBotOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProvisionalRevokedAtUtc",
                table: "TenantBotOrders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProvisionalSubId",
                table: "TenantBotOrders",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantBotOrders_ProvisionalDeliveryState_TenantBotId",
                table: "TenantBotOrders",
                columns: new[] { "ProvisionalDeliveryState", "TenantBotId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TenantBotOrders_ProvisionalDeliveryState_TenantBotId",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "ProvisionalAccountEmail",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "ProvisionalAccountUuid",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "ProvisionalCreatedAtUtc",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "ProvisionalDeliveredAtUtc",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "ProvisionalDeliveryState",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "ProvisionalErrorCode",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "ProvisionalFinalizedAtUtc",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "ProvisionalRevokedAtUtc",
                table: "TenantBotOrders");

            migrationBuilder.DropColumn(
                name: "ProvisionalSubId",
                table: "TenantBotOrders");
        }
    }
}
