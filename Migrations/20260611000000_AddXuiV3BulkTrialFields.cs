using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    [DbContext(typeof(UserDbContext))]
    [Migration("20260611000000_AddXuiV3BulkTrialFields")]
    public partial class AddXuiV3BulkTrialFields : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PendingAccountCount",
                table: "Users",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "PendingUserComment",
                table: "Users",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastFreeNationalAcc",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<DateTime>(
                name: "LastFreeNormalAcc",
                table: "Users",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "PendingAccountCount",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "PendingUserComment",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastFreeNationalAcc",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "LastFreeNormalAcc",
                table: "Users");
        }
    }
}
