using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Adminbot.Migrations
{
    /// <inheritdoc />
    public partial class Add_Props_ZibalTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ZibalPaymentInfos",
                table: "ZibalPaymentInfos");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SwapinoPaymentInfo",
                table: "SwapinoPaymentInfo");

            migrationBuilder.RenameTable(
                name: "SwapinoPaymentInfo",
                newName: "SwapinoPaymentInfos");

            migrationBuilder.AlterColumn<long>(
                name: "TrackId",
                table: "ZibalPaymentInfos",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "TEXT");

            migrationBuilder.AddColumn<int>(
                name: "Id",
                table: "ZibalPaymentInfos",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0)
                .Annotation("Sqlite:Autoincrement", true);

            migrationBuilder.AddColumn<int>(
                name: "AttemptsRemaining",
                table: "ZibalPaymentInfos",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ChatId",
                table: "ZibalPaymentInfos",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "ZibalPaymentInfos",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<bool>(
                name: "IsExpired",
                table: "ZibalPaymentInfos",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsPaid",
                table: "ZibalPaymentInfos",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "PaidAt",
                table: "ZibalPaymentInfos",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddPrimaryKey(
                name: "PK_ZibalPaymentInfos",
                table: "ZibalPaymentInfos",
                column: "Id");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SwapinoPaymentInfos",
                table: "SwapinoPaymentInfos",
                column: "Payment_Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_ZibalPaymentInfos",
                table: "ZibalPaymentInfos");

            migrationBuilder.DropPrimaryKey(
                name: "PK_SwapinoPaymentInfos",
                table: "SwapinoPaymentInfos");

            migrationBuilder.DropColumn(
                name: "Id",
                table: "ZibalPaymentInfos");

            migrationBuilder.DropColumn(
                name: "AttemptsRemaining",
                table: "ZibalPaymentInfos");

            migrationBuilder.DropColumn(
                name: "ChatId",
                table: "ZibalPaymentInfos");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "ZibalPaymentInfos");

            migrationBuilder.DropColumn(
                name: "IsExpired",
                table: "ZibalPaymentInfos");

            migrationBuilder.DropColumn(
                name: "IsPaid",
                table: "ZibalPaymentInfos");

            migrationBuilder.DropColumn(
                name: "PaidAt",
                table: "ZibalPaymentInfos");

            migrationBuilder.RenameTable(
                name: "SwapinoPaymentInfos",
                newName: "SwapinoPaymentInfo");

            migrationBuilder.AlterColumn<string>(
                name: "TrackId",
                table: "ZibalPaymentInfos",
                type: "TEXT",
                nullable: false,
                oldClrType: typeof(long),
                oldType: "INTEGER");

            migrationBuilder.AddPrimaryKey(
                name: "PK_ZibalPaymentInfos",
                table: "ZibalPaymentInfos",
                column: "TrackId");

            migrationBuilder.AddPrimaryKey(
                name: "PK_SwapinoPaymentInfo",
                table: "SwapinoPaymentInfo",
                column: "Payment_Id");
        }
    }
}
