using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>Adds global owned-colleague Tehran-day quota receipts without changing existing customer or tenant trials.</summary>
    /// <remarks>
    /// New grants begin at rollout with no historical backfill. TelegramUserId is the positive global colleague id;
    /// BotId records the internal originating owned bot only and never partitions the quota. GrantDateIran is a
    /// Tehran-local Gregorian midnight date. DeliveryRequestKey and OperationKey have separate unique indexes;
    /// the user/date/state index serves the shared normal/national capacity count. BotId and OperationKey are logical
    /// links without cascading foreign keys so bot deletion or conversation reset cannot erase idempotency evidence.
    /// FreeCreationStarted grants one executor before progress/panel I/O and never resets, even after release;
    /// started requests without definitive creation evidence remain held for review after restart.
    /// Retain all states indefinitely, including Released and Denied. PaidQuoteToman is an optional positive
    /// whole-toman paid-test preview snapshot on Denied rows, frozen to the committed debit amount by the sole
    /// PaidCreationState NotStarted-to-Started executor claim. Paid Started/Uncertain hold until authoritative evidence;
    /// Applied prevents refunds, and Rejected permits the caller's exactly-once receipt refund. No financial I/O occurs here.
    /// PaidRefundRecordedAtUtc is set once only after caller-proven exact receipt credit plus ledger completion.
    /// A paid-state/refund-proof/creation-time index supports bounded rejected-refund recovery independently of conversation state.
    /// </remarks>
    [DbContext(typeof(UserDbContext))]
    [Migration("20261001120000_AddColleagueTrialGrants")]
    public partial class AddColleagueTrialGrants : Migration
    {
        /// <summary>Creates only the new quota receipt table and event, creation-identity and daily-capacity indexes.</summary>
        /// <param name="migrationBuilder">The users.db schema builder; credentials.db and historical grants remain untouched.</param>
        /// <remarks>No pre-rollout account, order, wallet, ledger, profile, or conversation data is inserted or rewritten.</remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ColleagueTrialGrants",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    BotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ServiceKey = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DeliveryRequestKey = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    OperationKey = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    GrantDateIran = table.Column<DateTime>(type: "TEXT", nullable: false),
                    FreeCreationStarted = table.Column<bool>(type: "INTEGER", nullable: false, defaultValue: false),
                    State = table.Column<int>(type: "INTEGER", nullable: false),
                    PaidQuoteToman = table.Column<long>(type: "INTEGER", nullable: true),
                    PaidCreationState = table.Column<int>(type: "INTEGER", nullable: false),
                    PaidRefundRecordedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReleasedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_ColleagueTrialGrants", x => x.Id));

            migrationBuilder.CreateIndex(name: "IX_ColleagueTrialGrants_DeliveryRequestKey", table: "ColleagueTrialGrants",
                column: "DeliveryRequestKey", unique: true);
            migrationBuilder.CreateIndex(name: "IX_ColleagueTrialGrants_OperationKey", table: "ColleagueTrialGrants",
                column: "OperationKey", unique: true);
            migrationBuilder.CreateIndex(name: "IX_ColleagueTrialGrants_TelegramUserId_GrantDateIran_State", table: "ColleagueTrialGrants",
                columns: new[] { "TelegramUserId", "GrantDateIran", "State" });
            migrationBuilder.CreateIndex(name: "IX_ColleagueTrialGrants_PaidCreationState_PaidRefundRecordedAtUtc_CreatedAtUtc",
                table: "ColleagueTrialGrants", columns: new[] { "PaidCreationState", "PaidRefundRecordedAtUtc", "CreatedAtUtc" });
        }

        /// <summary>Removes the unused quota schema only when no retained delivery receipt exists.</summary>
        /// <param name="migrationBuilder">The users.db downgrade builder.</param>
        /// <remarks>
        /// Any receipt, including a denial or release, prevents rollback because deleting it could authorize a replayed
        /// event. Use a compatible forward release once the feature has recorded a request.
        /// </remarks>
        /// <exception cref="Microsoft.Data.Sqlite.SqliteException">At least one retained delivery receipt exists, so the rollback guard rejects destructive history deletion.</exception>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                CREATE TEMP TABLE ColleagueTrialRollbackGuard (Safe INTEGER CHECK (Safe = 1));
                INSERT INTO ColleagueTrialRollbackGuard SELECT CASE WHEN
                    EXISTS (SELECT 1 FROM ColleagueTrialGrants) THEN 0 ELSE 1 END;
                DROP TABLE ColleagueTrialRollbackGuard;
                """);
            migrationBuilder.DropTable(name: "ColleagueTrialGrants");
        }
    }
}
