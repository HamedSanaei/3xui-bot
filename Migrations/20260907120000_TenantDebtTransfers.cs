using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations;

/// <summary>Adds append-retained owner debt transfer intents without changing existing balances or financial history.</summary>
/// <remarks>Owner ids logically reference credentials.db profiles. One pending transfer per owner prevents sibling-store duplication.</remarks>
public partial class TenantDebtTransfers : Migration
{
    /// <summary>Creates empty transfer storage and the unique unresolved-owner constraint.</summary>
    /// <param name="migrationBuilder">EF users.db migration builder; no website requests or financial backfill.</param>
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("TenantDebtTransfer", columns: table => new
        {
            Id = table.Column<string>(type: "TEXT", nullable: false),
            OwnerTelegramUserId = table.Column<long>(type: "INTEGER", nullable: false),
            AmountToman = table.Column<long>(type: "INTEGER", nullable: false),
            Status = table.Column<string>(type: "TEXT", nullable: true),
            CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_TenantDebtTransfer", x => x.Id));
        migrationBuilder.CreateIndex("IX_TenantDebtTransfer_OwnerTelegramUserId", "TenantDebtTransfer",
            "OwnerTelegramUserId", unique: true, filter: "\"Status\" = 'pending'");
    }

    /// <summary>Refuses removal of financial history and removes only an unused transfer table.</summary>
    /// <param name="migrationBuilder">EF users.db downgrade builder.</param>
    /// <remarks>Any retained transfer requires a deliberate compatible recovery release rather than automatic rollback.</remarks>
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("CREATE TEMP TABLE DebtTransferRollbackGuard (Value INTEGER CHECK(Value = 0)); INSERT INTO DebtTransferRollbackGuard SELECT COUNT(*) FROM TenantDebtTransfer; DROP TABLE DebtTransferRollbackGuard;");
        migrationBuilder.DropTable("TenantDebtTransfer");
    }
}
