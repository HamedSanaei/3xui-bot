using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    [DbContext(typeof(UserDbContext))]
    [Migration("20260625000000_AddMultiBotState")]
    public partial class AddMultiBotState : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BotInstances",
                columns: table => new
                {
                    Id = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Username = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    BrandName = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    IsDefault = table.Column<bool>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    OwnerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    ChannelIdsJson = table.Column<string>(type: "TEXT", nullable: true),
                    SupportAccount = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    LoggerChannel = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    BackupChannel = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    IosTutorialJson = table.Column<string>(type: "TEXT", nullable: true),
                    AndroidTutorialJson = table.Column<string>(type: "TEXT", nullable: true),
                    WindowsTutorialJson = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotInstances", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BotUserStates",
                columns: table => new
                {
                    BotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    TelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    SelectedCountry = table.Column<string>(type: "TEXT", nullable: true),
                    SelectedPeriod = table.Column<string>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", nullable: true),
                    Flow = table.Column<string>(type: "TEXT", nullable: true),
                    LastStep = table.Column<string>(type: "TEXT", nullable: true),
                    TotoalGB = table.Column<string>(type: "TEXT", nullable: true),
                    ConfigLink = table.Column<string>(type: "TEXT", nullable: true),
                    SubLink = table.Column<string>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    _ConfigPrice = table.Column<string>(type: "TEXT", nullable: true),
                    LastFreeAcc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PaymentMethod = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    AccountCounter = table.Column<int>(type: "INTEGER", nullable: false),
                    PendingAccountCount = table.Column<int>(type: "INTEGER", nullable: false),
                    PendingUserComment = table.Column<string>(type: "TEXT", nullable: true),
                    LastFreeNationalAcc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastFreeNormalAcc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BotUserStates", x => new { x.BotId, x.TelegramUserId });
                });

            migrationBuilder.AddColumn<string>(
                name: "BotId",
                table: "SwapinoPaymentInfos",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BotUsername",
                table: "SwapinoPaymentInfos",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BotId",
                table: "HooshPayPaymentInfos",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BotUsername",
                table: "HooshPayPaymentInfos",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BotId",
                table: "ZibalPaymentInfos",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BotUsername",
                table: "ZibalPaymentInfos",
                type: "TEXT",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BotInstances_Username",
                table: "BotInstances",
                column: "Username");

            migrationBuilder.CreateIndex(
                name: "IX_BotInstances_OwnerTelegramUserId",
                table: "BotInstances",
                column: "OwnerTelegramUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BotUserStates_TelegramUserId",
                table: "BotUserStates",
                column: "TelegramUserId");

            migrationBuilder.CreateIndex(
                name: "IX_BotUserStates_Flow",
                table: "BotUserStates",
                column: "Flow");

            migrationBuilder.CreateIndex(
                name: "IX_SwapinoPaymentInfos_BotId",
                table: "SwapinoPaymentInfos",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_HooshPayPaymentInfos_BotId",
                table: "HooshPayPaymentInfos",
                column: "BotId");

            migrationBuilder.CreateIndex(
                name: "IX_ZibalPaymentInfos_BotId",
                table: "ZibalPaymentInfos",
                column: "BotId");

            migrationBuilder.Sql("INSERT OR IGNORE INTO BotInstances (Id, Username, BrandName, Type, IsDefault, Enabled, CreatedAtUtc) VALUES ('vpnetiranbot', 'vpnetiranbot', 'VpnetIran', 'owned', 1, 1, CURRENT_TIMESTAMP)");

            migrationBuilder.Sql(@"
                INSERT OR IGNORE INTO BotUserStates
                (BotId, TelegramUserId, SelectedCountry, SelectedPeriod, Type, Flow, LastStep, TotoalGB, ConfigLink, SubLink, Email, _ConfigPrice, LastFreeAcc, PaymentMethod, AccountCounter, PendingAccountCount, PendingUserComment, LastFreeNationalAcc, LastFreeNormalAcc, CreatedAtUtc)
                SELECT 'vpnetiranbot', Id, SelectedCountry, SelectedPeriod, Type, Flow, LastStep, TotoalGB, ConfigLink, SubLink, Email, _ConfigPrice, LastFreeAcc, PaymentMethod, AccountCounter, PendingAccountCount, PendingUserComment, LastFreeNationalAcc, LastFreeNormalAcc, CURRENT_TIMESTAMP
                FROM Users");

            migrationBuilder.Sql("UPDATE SwapinoPaymentInfos SET BotId = 'vpnetiranbot', BotUsername = 'vpnetiranbot' WHERE BotId IS NULL OR BotId = ''");
            migrationBuilder.Sql("UPDATE HooshPayPaymentInfos SET BotId = 'vpnetiranbot', BotUsername = 'vpnetiranbot' WHERE BotId IS NULL OR BotId = ''");
            migrationBuilder.Sql("UPDATE ZibalPaymentInfos SET BotId = 'vpnetiranbot', BotUsername = 'vpnetiranbot' WHERE BotId IS NULL OR BotId = ''");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "BotUserStates");
            migrationBuilder.DropTable(name: "BotInstances");

            migrationBuilder.DropIndex(name: "IX_SwapinoPaymentInfos_BotId", table: "SwapinoPaymentInfos");
            migrationBuilder.DropIndex(name: "IX_HooshPayPaymentInfos_BotId", table: "HooshPayPaymentInfos");
            migrationBuilder.DropIndex(name: "IX_ZibalPaymentInfos_BotId", table: "ZibalPaymentInfos");

            migrationBuilder.DropColumn(name: "BotId", table: "SwapinoPaymentInfos");
            migrationBuilder.DropColumn(name: "BotUsername", table: "SwapinoPaymentInfos");
            migrationBuilder.DropColumn(name: "BotId", table: "HooshPayPaymentInfos");
            migrationBuilder.DropColumn(name: "BotUsername", table: "HooshPayPaymentInfos");
            migrationBuilder.DropColumn(name: "BotId", table: "ZibalPaymentInfos");
            migrationBuilder.DropColumn(name: "BotUsername", table: "ZibalPaymentInfos");
        }
    }
}
