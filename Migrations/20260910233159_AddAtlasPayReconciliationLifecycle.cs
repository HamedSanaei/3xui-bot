using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class AddAtlasPayReconciliationLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ReconciliationExhaustedAtUtc",
                table: "AtlasPayPaymentInfos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReconciliationState",
                table: "AtlasPayPaymentInfos",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "active");

            migrationBuilder.CreateIndex(
                name: "IX_AtlasPayPaymentInfos_ReconciliationState_ReconciliationExhaustedAtUtc",
                table: "AtlasPayPaymentInfos",
                columns: new[] { "ReconciliationState", "ReconciliationExhaustedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AtlasPayPaymentInfos_ReconciliationState_ReconciliationExhaustedAtUtc",
                table: "AtlasPayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "ReconciliationExhaustedAtUtc",
                table: "AtlasPayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "ReconciliationState",
                table: "AtlasPayPaymentInfos");
        }
    }
}
