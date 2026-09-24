using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class AtlasPayWebhookPrimaryAndManualApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsProvisionallyApproved",
                table: "AtlasPayPaymentInfos",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProviderConfirmedAfterProvisionalAtUtc",
                table: "AtlasPayPaymentInfos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ProvisionalApprovedAtUtc",
                table: "AtlasPayPaymentInfos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProvisionalApprovedByTelegramUserId",
                table: "AtlasPayPaymentInfos",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WebhookEvent",
                table: "AtlasPayPaymentInfos",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WebhookProcessedAtUtc",
                table: "AtlasPayPaymentInfos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WebhookProviderTimestampUtc",
                table: "AtlasPayPaymentInfos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "WebhookReceivedAtUtc",
                table: "AtlasPayPaymentInfos",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AtlasPayPaymentInfos_WebhookReceivedAtUtc_WebhookProcessedAtUtc",
                table: "AtlasPayPaymentInfos",
                columns: new[] { "WebhookReceivedAtUtc", "WebhookProcessedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AtlasPayPaymentInfos_WebhookReceivedAtUtc_WebhookProcessedAtUtc",
                table: "AtlasPayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "IsProvisionallyApproved",
                table: "AtlasPayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "ProviderConfirmedAfterProvisionalAtUtc",
                table: "AtlasPayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "ProvisionalApprovedAtUtc",
                table: "AtlasPayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "ProvisionalApprovedByTelegramUserId",
                table: "AtlasPayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "WebhookEvent",
                table: "AtlasPayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "WebhookProcessedAtUtc",
                table: "AtlasPayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "WebhookProviderTimestampUtc",
                table: "AtlasPayPaymentInfos");

            migrationBuilder.DropColumn(
                name: "WebhookReceivedAtUtc",
                table: "AtlasPayPaymentInfos");
        }
    }
}
