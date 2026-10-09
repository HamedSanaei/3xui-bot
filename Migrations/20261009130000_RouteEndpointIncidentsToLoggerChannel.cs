using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations;

/// <summary>Replaces private-recipient endpoint alerts with one durable logger-channel intent and permanent receipt per incident.</summary>
/// <remarks>Only the two endpoint outbox tables are rebuilt. State, history, financial tables, and foreign keys are unchanged. Any old acknowledgment, pruned receipt, or potentially started send prevents new delivery. Private recipient identities are intentionally discarded and cannot safely be restored.</remarks>
public partial class RouteEndpointIncidentsToLoggerChannel : Migration
{
    /// <summary>Consolidates legacy recipients while retaining at-most-once delivery evidence and compact incident receipts.</summary>
    /// <param name="migrationBuilder">Required users.db SQLite migration builder; EF executes the table cutover transactionally.</param>
    /// <remarks>Receipts missing their matching detailed recipient row prove a previously pruned acknowledgment and fence the entire incident. A started or uncertain legacy send quarantines the incident. Only provably unsent records resume Pending, with no destination until channel verification. No Telegram operation or stored message/token is involved.</remarks>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE "TelegramEndpointAlerts_Logger" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_TelegramEndpointAlerts" PRIMARY KEY AUTOINCREMENT,
                "IncidentKey" TEXT NOT NULL,
                "BotId" TEXT NOT NULL,
                "TelegramBotId" INTEGER NOT NULL,
                "DestinationChatId" INTEGER NULL,
                "Category" TEXT NOT NULL,
                "MigrationState" INTEGER NOT NULL,
                "DesiredEndpoint" INTEGER NOT NULL,
                "EffectiveEndpoint" INTEGER NOT NULL,
                "Generation" INTEGER NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                "ClaimId" TEXT NULL,
                "LeaseUntilUtc" TEXT NULL,
                "SendStartedAtUtc" TEXT NULL,
                "SendDeadlineAtUtc" TEXT NULL,
                "Attempts" INTEGER NOT NULL,
                "NextAttemptAtUtc" TEXT NOT NULL,
                "ErrorCategory" TEXT NULL,
                "DeliveredAtUtc" TEXT NULL
            );
            CREATE TABLE "TelegramEndpointAlertReceipts_Logger" (
                "IncidentKey" TEXT NOT NULL CONSTRAINT "PK_TelegramEndpointAlertReceipts" PRIMARY KEY
            );
            INSERT INTO "TelegramEndpointAlertReceipts_Logger" ("IncidentKey")
                SELECT "IncidentKey" FROM "TelegramEndpointAlertReceipts"
                UNION SELECT "IncidentKey" FROM "TelegramEndpointAlerts";

            -- A missing detail for even one old recipient is a permanent acknowledged/pruned fence.
            WITH evidence AS (
                SELECT a."IncidentKey", MIN(a."Id") AS "FirstId", MIN(a."CreatedAtUtc") AS "Created",
                    MAX(CASE WHEN a."Status" = 2 OR a."DeliveredAtUtc" IS NOT NULL THEN 1 ELSE 0 END) AS "Acknowledged",
                    MAX(CASE WHEN a."SendStartedAtUtc" IS NOT NULL OR a."Status" = 3
                        OR a."ErrorCategory" IN ('send_uncertain', 'worker_interrupted') THEN 1 ELSE 0 END) AS "Uncertain",
                    MIN(a."SendStartedAtUtc") AS "Started", MAX(a."DeliveredAtUtc") AS "Delivered",
                    MAX(CASE WHEN a."ErrorCategory" IN ('transport_unconfigured', 'transport_unavailable', 'notifier_not_cloud', 'recipient_unauthorized')
                        THEN 0 ELSE a."Attempts" END) AS "Attempts", MIN(a."NextAttemptAtUtc") AS "NextAttempt"
                FROM "TelegramEndpointAlerts" a GROUP BY a."IncidentKey"
            ), consolidated AS (
                SELECT e.*, EXISTS (
                    SELECT 1 FROM "TelegramEndpointAlertReceipts" r
                    WHERE r."IncidentKey" = e."IncidentKey" AND NOT EXISTS (
                        SELECT 1 FROM "TelegramEndpointAlerts" a WHERE a."IncidentKey" = r."IncidentKey"
                            AND a."RecipientTelegramUserId" = r."RecipientTelegramUserId"
                    )
                ) AS "Pruned"
                FROM evidence e
            )
            INSERT INTO "TelegramEndpointAlerts_Logger" (
                "Id", "IncidentKey", "BotId", "TelegramBotId", "DestinationChatId", "Category", "MigrationState",
                "DesiredEndpoint", "EffectiveEndpoint", "Generation", "CreatedAtUtc", "Status", "ClaimId", "LeaseUntilUtc",
                "SendStartedAtUtc", "SendDeadlineAtUtc", "Attempts", "NextAttemptAtUtc", "ErrorCategory", "DeliveredAtUtc"
            )
            SELECT a."Id", a."IncidentKey", a."BotId", a."TelegramBotId", NULL, a."Category", a."MigrationState",
                a."DesiredEndpoint", a."EffectiveEndpoint", a."Generation", e."Created",
                CASE WHEN e."Acknowledged" = 1 OR e."Pruned" = 1 THEN 2 WHEN e."Uncertain" = 1 THEN 3 ELSE 0 END,
                NULL, NULL, e."Started", NULL, e."Attempts", e."NextAttempt",
                CASE WHEN e."Acknowledged" = 1 OR e."Pruned" = 1 THEN NULL
                    WHEN e."Uncertain" = 1 THEN 'send_uncertain' ELSE NULL END,
                CASE WHEN e."Acknowledged" = 1 OR e."Pruned" = 1 THEN e."Delivered" ELSE NULL END
            FROM consolidated e JOIN "TelegramEndpointAlerts" a ON a."Id" = e."FirstId";

            DROP TABLE "TelegramEndpointAlertReceipts";
            DROP TABLE "TelegramEndpointAlerts";
            ALTER TABLE "TelegramEndpointAlerts_Logger" RENAME TO "TelegramEndpointAlerts";
            ALTER TABLE "TelegramEndpointAlertReceipts_Logger" RENAME TO "TelegramEndpointAlertReceipts";
            CREATE UNIQUE INDEX "IX_TelegramEndpointAlerts_IncidentKey" ON "TelegramEndpointAlerts" ("IncidentKey");
            CREATE INDEX "IX_TelegramEndpointAlerts_Status_NextAttemptAtUtc_Id" ON "TelegramEndpointAlerts" ("Status", "NextAttemptAtUtc", "Id");
            CREATE INDEX "IX_TelegramEndpointAlerts_Status_LeaseUntilUtc" ON "TelegramEndpointAlerts" ("Status", "LeaseUntilUtc");
            """);
    }

    /// <summary>Refuses downgrade because discarded private recipient identities cannot be reconstructed without creating unsafe sends.</summary>
    /// <param name="migrationBuilder">Required migration builder; no downgrade operation is emitted.</param>
    /// <remarks>Restore a pre-cutover database backup only with explicit operator incident reconciliation. Keeping the logger schema and receipts is safer than silently rebuilding private-chat Pending intents.</remarks>
    /// <exception cref="NotSupportedException">Always thrown before modifying any schema or deduplication evidence.</exception>
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Logger-channel incident consolidation is irreversible; restoring private-recipient delivery requires a reconciled pre-cutover backup.");
}
