using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantStorefrontFundingAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TenantStorefrontFundingAlerts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    BusinessKey = table.Column<string>(type: "TEXT", maxLength: 180, nullable: false),
                    TenantBotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    OwnerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    TenantBotUsername = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 48, nullable: false),
                    BotBalanceToman = table.Column<long>(type: "INTEGER", nullable: false),
                    SiteWalletToman = table.Column<long>(type: "INTEGER", nullable: true),
                    MinimumSiteWalletToman = table.Column<long>(type: "INTEGER", nullable: false),
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
                    table.PrimaryKey("PK_TenantStorefrontFundingAlerts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantStorefrontFundingAlertStates",
                columns: table => new
                {
                    TenantBotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    IsUnderfunded = table.Column<bool>(type: "INTEGER", nullable: false),
                    EpisodeNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    UnderfundedSinceUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UnderfundedEpisodeNotifiedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastCustomerAttemptAlertAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastObservedBotBalanceToman = table.Column<long>(type: "INTEGER", nullable: false),
                    LastObservedSiteWalletToman = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantStorefrontFundingAlertStates", x => x.TenantBotId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TenantStorefrontFundingAlerts_BusinessKey",
                table: "TenantStorefrontFundingAlerts",
                column: "BusinessKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantStorefrontFundingAlerts_LeaseUntilUtc",
                table: "TenantStorefrontFundingAlerts",
                column: "LeaseUntilUtc");

            migrationBuilder.CreateIndex(
                name: "IX_TenantStorefrontFundingAlerts_Status_NextAttemptAtUtc",
                table: "TenantStorefrontFundingAlerts",
                columns: new[] { "Status", "NextAttemptAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_TenantStorefrontFundingAlerts_TenantBotId_CreatedAtUtc",
                table: "TenantStorefrontFundingAlerts",
                columns: new[] { "TenantBotId", "CreatedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TenantStorefrontFundingAlerts");

            migrationBuilder.DropTable(
                name: "TenantStorefrontFundingAlertStates");
        }
    }
}
