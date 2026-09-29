using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>Adds isolated pricing modes and optional sale rates to tenant storefronts and a bot-scoped owner editor draft.</summary>
    /// <remarks>Existing users.db BotInstances default to percent with all manual prices null, preserving every order and the historical markup. No credentials.db or global catalog prices are changed. BotInstances.Id owns its rates; BotUserStates uses the existing BotId/TelegramUserId composite key, so drafts cannot cross bots or owners. There are no new foreign keys or indexes: existing storefront and state identities enforce the required scope. Drafts are transient and cleared with the conversation; saved rates persist until the storefront owner resets or edits that store.</remarks>
    [DbContext(typeof(UserDbContext))]
    [Migration("20260929160000_AddTenantStorefrontPricingModes")]
    public partial class AddTenantStorefrontPricingModes : Migration
    {
        /// <summary>Adds percent-default pricing columns and nullable manual rates/draft without backfilling orders.</summary>
        /// <param name="migrationBuilder">The users.db schema builder; no other database is changed.</param>
        /// <remarks>Raw SQL inserts omitting the new fields receive the percent default and null manual values. Rates are whole toman per GB/day/plan; manual prices remain inactive in percentage mode.</remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(name: "TenantPricingMode", table: "BotInstances", type: "TEXT", maxLength: 16, nullable: false, defaultValue: "percent");
            migrationBuilder.AddColumn<long>(name: "TenantNormalPricePerGbToman", table: "BotInstances", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<long>(name: "TenantNormalPricePerDayToman", table: "BotInstances", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<long>(name: "TenantNationalPricePerGbToman", table: "BotInstances", type: "INTEGER", nullable: true);
            migrationBuilder.AddColumn<string>(name: "TenantUnlimitedPlanPricesJson", table: "BotInstances", type: "TEXT", maxLength: 8192, nullable: true);
            migrationBuilder.AddColumn<string>(name: "OwnerPricingDraftJson", table: "BotUserStates", type: "TEXT", maxLength: 16384, nullable: true);
        }

        /// <summary>Removes only pricing columns and the conversation draft introduced by this migration.</summary>
        /// <param name="migrationBuilder">The users.db SQLite schema builder; historical orders and markup remain unchanged.</param>
        /// <remarks>SQLite's native DROP COLUMN avoids EF's unsupported model-less rebuild on rollback. Downgrading
        /// discards stored manual prices and drafts but does not delete storefronts, state rows or financial history.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE "BotUserStates" DROP COLUMN "OwnerPricingDraftJson";
                ALTER TABLE "BotInstances" DROP COLUMN "TenantUnlimitedPlanPricesJson";
                ALTER TABLE "BotInstances" DROP COLUMN "TenantNationalPricePerGbToman";
                ALTER TABLE "BotInstances" DROP COLUMN "TenantNormalPricePerDayToman";
                ALTER TABLE "BotInstances" DROP COLUMN "TenantNormalPricePerGbToman";
                ALTER TABLE "BotInstances" DROP COLUMN "TenantPricingMode";
                """);
        }
    }
}
