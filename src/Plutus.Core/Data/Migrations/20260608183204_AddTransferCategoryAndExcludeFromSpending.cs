using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Plutus.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTransferCategoryAndExcludeFromSpending : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ExcludeFromSpending",
                table: "Categories",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 1,
                column: "ExcludeFromSpending",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 2,
                column: "ExcludeFromSpending",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 3,
                column: "ExcludeFromSpending",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 4,
                column: "ExcludeFromSpending",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 5,
                column: "ExcludeFromSpending",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 6,
                column: "ExcludeFromSpending",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 7,
                column: "ExcludeFromSpending",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 8,
                column: "ExcludeFromSpending",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 9,
                column: "ExcludeFromSpending",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 10,
                column: "ExcludeFromSpending",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 11,
                column: "ExcludeFromSpending",
                value: false);

            migrationBuilder.UpdateData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 12,
                column: "ExcludeFromSpending",
                value: false);

            migrationBuilder.InsertData(
                table: "Categories",
                columns: new[] { "Id", "Color", "ExcludeFromSpending", "IsSystem", "Name", "SortOrder" },
                values: new object[] { 13, "#94A3B8", true, true, "Transfer", 12 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: 13);

            migrationBuilder.DropColumn(
                name: "ExcludeFromSpending",
                table: "Categories");
        }
    }
}
