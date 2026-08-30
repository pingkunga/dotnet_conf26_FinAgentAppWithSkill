using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceApp.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddSavingsCategorySeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Categories",
                columns: new[] { "Id", "IsSystemDefault", "Kind", "Name", "UserId" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000108"), true, "Expense", "Savings", null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Categories",
                keyColumn: "Id",
                keyValue: new Guid("00000000-0000-0000-0000-000000000108"));
        }
    }
}
