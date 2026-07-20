using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds durable, users.db-only XUI v3 link-change operations and one-active-operation uniqueness per panel client.
    /// </summary>
    public partial class AddXuiV3LinkChangeOperations : Migration
    {
        /// <summary>Creates the operation table and its callback, recovery, audit, and active-client indexes.</summary>
        /// <param name="migrationBuilder">EF Core builder targeting users.db; credentials.db is not involved.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "XuiV3LinkChangeOperations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    PanelKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    BotUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    BotType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<int>(type: "INTEGER", nullable: false),
                    ClientId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 16, nullable: true),
                    Page = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Stage = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OldEmail = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    OldUuid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    OldSubId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    NewEmail = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    NewUuid = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    NewSubId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: true),
                    SnapshotJson = table.Column<string>(type: "TEXT", nullable: true),
                    PayloadJson = table.Column<string>(type: "TEXT", nullable: true),
                    IdentityCommitted = table.Column<bool>(type: "INTEGER", nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LeaseUntilUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ConfirmationExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConfirmedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SiteSyncQueued = table.Column<bool>(type: "INTEGER", nullable: false),
                    SuccessAuditLogged = table.Column<bool>(type: "INTEGER", nullable: false),
                    SuccessNotificationSent = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_XuiV3LinkChangeOperations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3LinkChangeOperations_OperationKey",
                table: "XuiV3LinkChangeOperations",
                column: "OperationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3LinkChangeOperations_PanelKey_ClientId",
                table: "XuiV3LinkChangeOperations",
                columns: new[] { "PanelKey", "ClientId" },
                unique: true,
                filter: "\"Status\" IN ('awaiting_confirmation','processing','recovery_pending','manual_review')");

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3LinkChangeOperations_Status_NextAttemptAtUtc",
                table: "XuiV3LinkChangeOperations",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_XuiV3LinkChangeOperations_TelegramUserId_CreatedAtUtc",
                table: "XuiV3LinkChangeOperations",
                columns: new[] { "TelegramUserId", "CreatedAtUtc" });
        }

        /// <summary>Removes the link-change operation table and all indexes created with it.</summary>
        /// <param name="migrationBuilder">EF Core builder targeting users.db.</param>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "XuiV3LinkChangeOperations");
        }
    }
}
