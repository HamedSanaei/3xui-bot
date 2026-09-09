using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantManualReceiptNotificationOutbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantManualReceiptNotifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReceiptId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClaimToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TelegramMessageId = table.Column<int>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeliveredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantManualReceiptNotifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantManualReceiptNotifications_LeaseUntilUtc",
                table: "TenantManualReceiptNotifications",
                column: "LeaseUntilUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TenantManualReceiptNotifications_ReceiptId",
                table: "TenantManualReceiptNotifications",
                column: "ReceiptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantManualReceiptNotifications_Status_NextAttemptAtUtc",
                table: "TenantManualReceiptNotifications",
                columns: new[] { "Status", "NextAttemptAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantManualReceiptNotifications");
        }
    }
}
