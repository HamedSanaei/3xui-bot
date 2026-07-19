using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>
    /// Adds global owned-bot referral relationships, retryable payment/reward rows, and idempotent ledger keys.
    /// </summary>
    /// <remarks>
    /// Existing payment and ledger rows are preserved. New constraints enforce one immutable referrer per referred
    /// Telegram user, one first-eligible event, and one reward per source/beneficiary/kind.
    /// </remarks>
    public partial class AddGlobalOwnedBotReferrals : Migration
    {
        /// <summary>Creates referral tables, check/unique indexes, and the nullable ledger idempotency column.</summary>
        /// <param name="migrationBuilder">EF migration builder for the application users.db schema.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdempotencyKey",
                table: "WalletLedgerEntries",
                type: "TEXT",
                maxLength: 240,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReferralPaymentEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReferralRelationshipId = table.Column<long>(type: "INTEGER", nullable: false),
                    SourcePaymentKey = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    Provider = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    PaymentType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ProviderPaymentId = table.Column<string>(type: "TEXT", maxLength: 160, nullable: false),
                    BotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ReferredTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ReferrerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceAmountToman = table.Column<long>(type: "INTEGER", nullable: false),
                    IsFirstEligiblePayment = table.Column<bool>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    SourceSettledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferralPaymentEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReferralRelationships",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReferrerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ReferredTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    AttributionBotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ReferralCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferralRelationships", x => x.Id);
                    table.CheckConstraint("CK_ReferralRelationships_NoSelfReferral", "\"ReferrerTelegramUserId\" <> \"ReferredTelegramUserId\"");
                });

            migrationBuilder.CreateTable(
                name: "ReferralRewards",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReferralPaymentEventId = table.Column<long>(type: "INTEGER", nullable: false),
                    ReferralRelationshipId = table.Column<long>(type: "INTEGER", nullable: false),
                    SourcePaymentKey = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    BeneficiaryTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ReferrerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ReferredTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    BotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RewardKind = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    RewardAmountToman = table.Column<long>(type: "INTEGER", nullable: false),
                    SourceAmountToman = table.Column<long>(type: "INTEGER", nullable: false),
                    RewardPercentSnapshot = table.Column<decimal>(type: "TEXT", precision: 9, scale: 4, nullable: false),
                    MinimumRewardTomanSnapshot = table.Column<long>(type: "INTEGER", nullable: false),
                    MaximumRewardTomanSnapshot = table.Column<long>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 24, nullable: false),
                    WalletMutationKey = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    WalletLedgerEntryId = table.Column<int>(type: "INTEGER", nullable: true),
                    BalanceBefore = table.Column<long>(type: "INTEGER", nullable: true),
                    BalanceAfter = table.Column<long>(type: "INTEGER", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AppliedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    NotifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReferralRewards", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalletLedgerEntries_IdempotencyKey",
                table: "WalletLedgerEntries",
                column: "IdempotencyKey",
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReferralPaymentEvents_ReferralRelationshipId",
                table: "ReferralPaymentEvents",
                column: "ReferralRelationshipId");

            migrationBuilder.CreateIndex(
                name: "IX_ReferralPaymentEvents_ReferredTelegramUserId_IsFirstEligiblePayment",
                table: "ReferralPaymentEvents",
                columns: new[] { "ReferredTelegramUserId", "IsFirstEligiblePayment" },
                unique: true,
                filter: "\"IsFirstEligiblePayment\" = 1");

            migrationBuilder.CreateIndex(
                name: "IX_ReferralPaymentEvents_SourcePaymentKey",
                table: "ReferralPaymentEvents",
                column: "SourcePaymentKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReferralPaymentEvents_Status_UpdatedAtUtc",
                table: "ReferralPaymentEvents",
                columns: new[] { "Status", "UpdatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ReferralRelationships_AttributionBotId",
                table: "ReferralRelationships",
                column: "AttributionBotId");

            migrationBuilder.CreateIndex(
                name: "IX_ReferralRelationships_ReferredTelegramUserId",
                table: "ReferralRelationships",
                column: "ReferredTelegramUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReferralRelationships_ReferrerTelegramUserId",
                table: "ReferralRelationships",
                column: "ReferrerTelegramUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ReferralRewards_BeneficiaryTelegramUserId_Status",
                table: "ReferralRewards",
                columns: new[] { "BeneficiaryTelegramUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ReferralRewards_ReferralPaymentEventId",
                table: "ReferralRewards",
                column: "ReferralPaymentEventId");

            migrationBuilder.CreateIndex(
                name: "IX_ReferralRewards_ReferralRelationshipId",
                table: "ReferralRewards",
                column: "ReferralRelationshipId");

            migrationBuilder.CreateIndex(
                name: "IX_ReferralRewards_SourcePaymentKey_BeneficiaryTelegramUserId_RewardKind",
                table: "ReferralRewards",
                columns: new[] { "SourcePaymentKey", "BeneficiaryTelegramUserId", "RewardKind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReferralRewards_WalletMutationKey",
                table: "ReferralRewards",
                column: "WalletMutationKey",
                unique: true);
        }

        /// <summary>Removes referral tables and the ledger idempotency column.</summary>
        /// <param name="migrationBuilder">EF migration builder for the application users.db schema.</param>
        /// <remarks>Rollback deletes referral audit/history rows and should therefore be used only with an explicit backup.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReferralPaymentEvents");

            migrationBuilder.DropTable(
                name: "ReferralRelationships");

            migrationBuilder.DropTable(
                name: "ReferralRewards");

            migrationBuilder.DropIndex(
                name: "IX_WalletLedgerEntries_IdempotencyKey",
                table: "WalletLedgerEntries");

            migrationBuilder.DropColumn(
                name: "IdempotencyKey",
                table: "WalletLedgerEntries");
        }
    }
}
