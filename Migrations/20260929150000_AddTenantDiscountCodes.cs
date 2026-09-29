using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>Adds storefront-owned discount definitions, bound quotes, durable claims, and nullable order snapshots to users.db.</summary>
    /// <remarks>Existing orders keep their original sale, cost and profit unchanged; no backfill or credentials.db change is required. Quote bot ids logically reference tenant storefronts, and redemption code/order ids logically reference the retained definition and order; deleted codes and terminal claims remain for financial audit. A filtered per-bot live-code index, unique bound-message/order quote indexes and a unique per-order claim index enforce checkout identity without deleting history.</remarks>
    [DbContext(typeof(UserDbContext))]
    [Migration("20260929150000_AddTenantDiscountCodes")]
    public partial class AddTenantDiscountCodes : Migration
    {
        /// <summary>Creates only new discount tables, indexes, and nullable conversation/order fields.</summary>
        /// <param name="migrationBuilder">The users.db schema builder.</param>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "OwnerDiscountDraftJson", table: "BotUserStates", type: "TEXT", maxLength: 4096, nullable: true);
            migrationBuilder.AddColumn<string>(name: "RenewalDiscountSelectionJson", table: "BotUserStates", type: "TEXT", maxLength: 1024, nullable: true);
            migrationBuilder.AddColumn<int>(name: "PurchaseDiscountQuoteId", table: "BotUserStates", type: "INTEGER", nullable: true);

            migrationBuilder.AddColumn<long>(name: "OriginalSalePriceToman", table: "TenantBotOrders", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<long>(name: "DiscountAmountToman", table: "TenantBotOrders", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<int>(name: "TenantDiscountCodeId", table: "TenantBotOrders", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<string>(name: "AppliedDiscountCode", table: "TenantBotOrders", type: "TEXT", maxLength: 32, nullable: true);
            migrationBuilder.AddColumn<string>(name: "DiscountInvoiceAttemptState", table: "TenantBotOrders", type: "TEXT", maxLength: 32, nullable: true);
            migrationBuilder.AddColumn<DateTime>(name: "DiscountInvoiceAttemptedAtUtc", table: "TenantBotOrders", type: "TEXT", nullable: true);

            migrationBuilder.CreateTable(
                name: "TenantDiscountCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                    TenantBotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Code = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Kind = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Scope = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    FixedAmountToman = table.Column<long>(type: "INTEGER", nullable: true),
                    Percent = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxDiscountToman = table.Column<long>(type: "INTEGER", nullable: true),
                    MinimumOrderToman = table.Column<long>(type: "INTEGER", nullable: false),
                    MaxUses = table.Column<int>(type: "INTEGER", nullable: false),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table => table.PrimaryKey("PK_TenantDiscountCodes", x => x.Id));

            migrationBuilder.CreateTable(
                name: "TenantDiscountQuotes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                    TenantBotId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CustomerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ChatId = table.Column<long>(type: "INTEGER", nullable: false),
                    MessageId = table.Column<int>(type: "INTEGER", nullable: true),
                    SelectionKey = table.Column<string>(type: "TEXT", maxLength: 240, nullable: false),
                    CodeId = table.Column<int>(type: "INTEGER", nullable: true),
                    CodeUpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    GrossToman = table.Column<long>(type: "INTEGER", nullable: false),
                    BaseCostToman = table.Column<long>(type: "INTEGER", nullable: false),
                    DiscountAmountToman = table.Column<long>(type: "INTEGER", nullable: false),
                    NetToman = table.Column<long>(type: "INTEGER", nullable: false),
                    OrderId = table.Column<int>(type: "INTEGER", nullable: true),
                    SelectedProvider = table.Column<string>(type: "TEXT", maxLength: 32, nullable: true),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AdmittedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_TenantDiscountQuotes", x => x.Id));

            migrationBuilder.CreateTable(
                name: "TenantDiscountRedemptions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false).Annotation("Sqlite:Autoincrement", true),
                    CodeId = table.Column<int>(type: "INTEGER", nullable: false),
                    TenantBotOrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    ReservedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ConsumedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ReleasedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table => table.PrimaryKey("PK_TenantDiscountRedemptions", x => x.Id));

            migrationBuilder.CreateIndex(name: "IX_TenantDiscountCodes_TenantBotId_Code", table: "TenantDiscountCodes", columns: new[] { "TenantBotId", "Code" }, unique: true, filter: "\"IsDeleted\" = 0");
            migrationBuilder.CreateIndex(name: "IX_TenantDiscountQuotes_TenantBotId_CustomerTelegramUserId_ChatId_MessageId", table: "TenantDiscountQuotes", columns: new[] { "TenantBotId", "CustomerTelegramUserId", "ChatId", "MessageId" }, unique: true, filter: "\"MessageId\" IS NOT NULL");
            migrationBuilder.CreateIndex(name: "IX_TenantDiscountQuotes_OrderId", table: "TenantDiscountQuotes", column: "OrderId", unique: true, filter: "\"OrderId\" IS NOT NULL");
            migrationBuilder.CreateIndex(name: "IX_TenantDiscountQuotes_State_ExpiresAtUtc", table: "TenantDiscountQuotes", columns: new[] { "State", "ExpiresAtUtc" });
            migrationBuilder.CreateIndex(name: "IX_TenantDiscountRedemptions_TenantBotOrderId", table: "TenantDiscountRedemptions", column: "TenantBotOrderId", unique: true);
            migrationBuilder.CreateIndex(name: "IX_TenantDiscountRedemptions_CodeId_State", table: "TenantDiscountRedemptions", columns: new[] { "CodeId", "State" });
        }

        /// <summary>Removes only schema introduced by this migration, never historical financial rows or migration IDs.</summary>
        /// <param name="migrationBuilder">The users.db schema builder.</param>
        /// <remarks>Explicit rollback irreversibly loses discount definitions, quotes, redemption audit and order discount snapshots; do not downgrade a live discounted storefront.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "TenantDiscountRedemptions");
            migrationBuilder.DropTable(name: "TenantDiscountQuotes");
            migrationBuilder.DropTable(name: "TenantDiscountCodes");
            migrationBuilder.Sql("ALTER TABLE \"BotUserStates\" DROP COLUMN \"OwnerDiscountDraftJson\";");
            migrationBuilder.Sql("ALTER TABLE \"BotUserStates\" DROP COLUMN \"RenewalDiscountSelectionJson\";");
            migrationBuilder.Sql("ALTER TABLE \"BotUserStates\" DROP COLUMN \"PurchaseDiscountQuoteId\";");
            migrationBuilder.Sql("ALTER TABLE \"TenantBotOrders\" DROP COLUMN \"OriginalSalePriceToman\";");
            migrationBuilder.Sql("ALTER TABLE \"TenantBotOrders\" DROP COLUMN \"DiscountAmountToman\";");
            migrationBuilder.Sql("ALTER TABLE \"TenantBotOrders\" DROP COLUMN \"TenantDiscountCodeId\";");
            migrationBuilder.Sql("ALTER TABLE \"TenantBotOrders\" DROP COLUMN \"AppliedDiscountCode\";");
            migrationBuilder.Sql("ALTER TABLE \"TenantBotOrders\" DROP COLUMN \"DiscountInvoiceAttemptState\";");
            migrationBuilder.Sql("ALTER TABLE \"TenantBotOrders\" DROP COLUMN \"DiscountInvoiceAttemptedAtUtc\";");
        }
    }
}
