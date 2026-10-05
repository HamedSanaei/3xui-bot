using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <summary>Adds independent, default-enrolled public-channel participation consent to each Tenant storefront.</summary>
    /// <remarks>Only users.db BotInstances changes. Existing stores receive true; an explicit owner opt-out persists until
    /// that exact owner enables the store's participation again, including through reset or token replacement. No orders,
    /// financial records, conversation state, indexes or foreign keys change. Owned bots ignore this Tenant-only preference.</remarks>
    [DbContext(typeof(UserDbContext))]
    [Migration("20261005120000_AddTenantPublicChannelPostsEnabled")]
    public partial class AddTenantPublicChannelPostsEnabled : Migration
    {
        /// <summary>Enrolls existing and newly inserted storefront rows without rewriting any other store data.</summary>
        /// <param name="migrationBuilder">The users.db schema builder, not the credentials database.</param>
        /// <remarks>The non-null SQLite INTEGER default also enrolls raw-SQL inserts that omit the field. Explicit false remains false.</remarks>
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(name: "TenantPublicChannelPostsEnabled", table: "BotInstances",
                type: "INTEGER", nullable: false, defaultValue: true);
        }

        /// <summary>Removes only the participation preference when deliberately downgrading users.db.</summary>
        /// <param name="migrationBuilder">The users.db SQLite schema builder.</param>
        /// <remarks>Native DROP COLUMN avoids a model-less EF rebuild. Downgrade discards opt-out consent but leaves stores and history intact.</remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("ALTER TABLE \"BotInstances\" DROP COLUMN \"TenantPublicChannelPostsEnabled\";");
        }
    }
}
