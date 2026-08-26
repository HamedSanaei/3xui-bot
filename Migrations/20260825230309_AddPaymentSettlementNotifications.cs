using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds the users.db outbox used to retry owned-wallet settlement notifications independently of settlement.
    /// </summary>
    /// <remarks>
    /// The migration creates an empty table and indexes only. It performs no historical backfill, sends no Telegram
    /// message, invokes no provider or XUI API, and never credits or debits any wallet. Therefore settlements completed
    /// before deployment do not receive a new notification automatically.
    /// </remarks>
    public partial class AddPaymentSettlementNotifications : Migration
    {
        /// <summary>
        /// Creates the durable notification table, its unique idempotency key, and due-row processing indexes.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder targeting the global users.db schema.</param>
        /// <remarks>
        /// No existing payment, wallet, ledger, tenant, or XUI row is read or changed by this operation.
        /// </remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentSettlementNotifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    NotificationKey = table.Column<string>(type: "TEXT", maxLength: 220, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    ProviderPaymentId = table.Column<int>(type: "INTEGER", nullable: false),
                    BotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    AmountToman = table.Column<long>(type: "INTEGER", nullable: false),
                    MessageText = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false, defaultValue: "pending"),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClaimToken = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    TelegramMessageId = table.Column<int>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 512, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    DeliveredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentSettlementNotifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSettlementNotifications_BotId",
                table: "PaymentSettlementNotifications",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSettlementNotifications_NotificationKey",
                table: "PaymentSettlementNotifications",
                column: "NotificationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSettlementNotifications_Status_NextAttemptAtUtc_LeaseUntilUtc",
                table: "PaymentSettlementNotifications",
                columns: new[] { "Status", "NextAttemptAtUtc", "LeaseUntilUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentSettlementNotifications_TelegramUserId",
                table: "PaymentSettlementNotifications",
                column: "TelegramUserId");
        }

        /// <summary>
        /// Removes only the settlement-notification outbox table and its indexes.
        /// </summary>
        /// <param name="migrationBuilder">EF Core migration builder targeting the global users.db schema.</param>
        /// <remarks>Financial settlement rows and wallet balances are not reverted by this schema rollback.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentSettlementNotifications");
        }
    }
}
