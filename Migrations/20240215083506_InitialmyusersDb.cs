using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class InitialmyusersDb : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Cookies",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    SessionCookie = table.Column<string>(type: "TEXT", nullable: true),
                    ExpirationDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Cookies", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SelectedCountry = table.Column<string>(type: "TEXT", nullable: true),
                    SelectedPeriod = table.Column<string>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", nullable: true),
                    Flow = table.Column<string>(type: "TEXT", nullable: true),
                    LastStep = table.Column<string>(type: "TEXT", nullable: true),
                    TotoalGB = table.Column<string>(type: "TEXT", nullable: true),
                    ConfigLink = table.Column<string>(type: "TEXT", nullable: true),
                    SubLink = table.Column<string>(type: "TEXT", nullable: true),
                    Email = table.Column<string>(type: "TEXT", nullable: true),
                    ConfigPrice = table.Column<string>(name: "_ConfigPrice", type: "TEXT", nullable: true),
                    ConfigPrice0 = table.Column<long>(name: "ConfigPrice", type: "INTEGER", nullable: false),
                    LastFreeAcc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    PaymentMethod = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Cookies");

            migrationBuilder.DropTable(
                name: "Users");
        }
    }
}
