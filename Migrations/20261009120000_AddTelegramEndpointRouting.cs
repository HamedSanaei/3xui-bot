using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations;

/// <summary>Adds only identity-scoped Telegram endpoint metadata, append-only transition history, and independent operator alert outbox.</summary>
/// <remarks>Existing bots receive no rows and continue on Cloud. No financial tables, tokens, or Telegram API calls are touched.</remarks>
public partial class AddTelegramEndpointRouting : Migration
{
    /// <summary>Creates additive users.db endpoint tables and their CAS/deduplication indexes.</summary>
    /// <param name="migrationBuilder">Required users.db SQLite migration builder.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "TelegramEndpointStates",
            columns: table => new
            {
                BotId = table.Column<string>(type: "TEXT", nullable: false, maxLength: 64),
                TelegramBotId = table.Column<long>(type: "INTEGER", nullable: false),
                DesiredEndpoint = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                EffectiveEndpoint = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                MigrationState = table.Column<int>(type: "INTEGER", nullable: false, defaultValue: 0),
                Generation = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 1L),
                Revision = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                ControlRevision = table.Column<long>(type: "INTEGER", nullable: false, defaultValue: 0L),
                AutoFailoverEnabled = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: true),
                LastSuccessfulHealthAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastHealthCheckAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastFailureAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                LastFailureCategory = table.Column<string>(type: "TEXT", nullable: true, maxLength: 64),
                LastMigrationAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                CloudReuseEligibleAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                ConsecutiveFailures = table.Column<int>(type: "INTEGER", nullable: false),
                ConsecutiveSuccesses = table.Column<int>(type: "INTEGER", nullable: false),
                OperationId = table.Column<string>(type: "TEXT", nullable: true, maxLength: 32),
                ActorTelegramUserId = table.Column<long>(type: "INTEGER", nullable: true),
                Trigger = table.Column<string>(type: "TEXT", nullable: true, maxLength: 32),
                MigrationStartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                LogoutAttemptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                LogoutEndpoint = table.Column<int>(type: "INTEGER", nullable: true),
                LogoutAcknowledgedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                RecoveryAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                OutageId = table.Column<string>(type: "TEXT", nullable: true, maxLength: 32),
                LastOutageNotifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_TelegramEndpointStates", x => new { x.BotId, x.TelegramBotId }));
        migrationBuilder.CreateIndex(name: "IX_TelegramEndpointStates_MigrationState_NextAttemptAtUtc", table: "TelegramEndpointStates", columns: new[] { "MigrationState", "NextAttemptAtUtc" });
        migrationBuilder.CreateTable(
            name: "TelegramEndpointHistory",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                BotId = table.Column<string>(type: "TEXT", nullable: false, maxLength: 64),
                TelegramBotId = table.Column<long>(type: "INTEGER", nullable: false),
                OperationId = table.Column<string>(type: "TEXT", nullable: true, maxLength: 32),
                ActorTelegramUserId = table.Column<long>(type: "INTEGER", nullable: true),
                FromDesiredEndpoint = table.Column<int>(type: "INTEGER", nullable: false),
                ToDesiredEndpoint = table.Column<int>(type: "INTEGER", nullable: false),
                FromEffectiveEndpoint = table.Column<int>(type: "INTEGER", nullable: false),
                ToEffectiveEndpoint = table.Column<int>(type: "INTEGER", nullable: false),
                MigrationState = table.Column<int>(type: "INTEGER", nullable: false),
                Reason = table.Column<string>(type: "TEXT", nullable: false, maxLength: 64),
                Outcome = table.Column<string>(type: "TEXT", nullable: false, maxLength: 64),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                Revision = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_TelegramEndpointHistory", x => x.Id));
        migrationBuilder.CreateIndex(name: "IX_TelegramEndpointHistory_BotId_TelegramBotId_Id", table: "TelegramEndpointHistory", columns: new[] { "BotId", "TelegramBotId", "Id" });
        migrationBuilder.CreateTable(
            name: "TelegramEndpointAlerts",
            columns: table => new
            {
                Id = table.Column<long>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                IncidentKey = table.Column<string>(type: "TEXT", nullable: false, maxLength: 240),
                BotId = table.Column<string>(type: "TEXT", nullable: false, maxLength: 64),
                TelegramBotId = table.Column<long>(type: "INTEGER", nullable: false),
                RecipientTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                Category = table.Column<string>(type: "TEXT", nullable: false, maxLength: 64),
                MigrationState = table.Column<int>(type: "INTEGER", nullable: false),
                DesiredEndpoint = table.Column<int>(type: "INTEGER", nullable: false),
                EffectiveEndpoint = table.Column<int>(type: "INTEGER", nullable: false),
                Generation = table.Column<long>(type: "INTEGER", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                Status = table.Column<int>(type: "INTEGER", nullable: false),
                ClaimId = table.Column<string>(type: "TEXT", nullable: true, maxLength: 32),
                LeaseUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                SendStartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                SendDeadlineAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                Attempts = table.Column<int>(type: "INTEGER", nullable: false),
                NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                ErrorCategory = table.Column<string>(type: "TEXT", nullable: true, maxLength: 64),
                DeliveredAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_TelegramEndpointAlerts", x => x.Id));
        migrationBuilder.CreateIndex(name: "IX_TelegramEndpointAlerts_IncidentKey_RecipientTelegramUserId", table: "TelegramEndpointAlerts", columns: new[] { "IncidentKey", "RecipientTelegramUserId" }, unique: true);
        migrationBuilder.CreateIndex(name: "IX_TelegramEndpointAlerts_Status_NextAttemptAtUtc_Id", table: "TelegramEndpointAlerts", columns: new[] { "Status", "NextAttemptAtUtc", "Id" });
        migrationBuilder.CreateIndex(name: "IX_TelegramEndpointAlerts_Status_LeaseUntilUtc", table: "TelegramEndpointAlerts", columns: new[] { "Status", "LeaseUntilUtc" });
        migrationBuilder.CreateTable(
            name: "TelegramEndpointAlertReceipts",
            columns: table => new
            {
                IncidentKey = table.Column<string>(type: "TEXT", nullable: false, maxLength: 240),
                RecipientTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false)
            },
            constraints: table => table.PrimaryKey("PK_TelegramEndpointAlertReceipts", x => new { x.IncidentKey, x.RecipientTelegramUserId }));
    }

    /// <summary>Drops only endpoint metadata when deliberately downgrading users.db.</summary>
    /// <param name="migrationBuilder">Required users.db SQLite migration builder.</param>
    /// <remarks>Endpoint incident history is discarded on downgrade; financial data remains unchanged.</remarks>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("TelegramEndpointAlertReceipts");
        migrationBuilder.DropTable("TelegramEndpointAlerts");
        migrationBuilder.DropTable("TelegramEndpointHistory");
        migrationBuilder.DropTable("TelegramEndpointStates");
    }
}
