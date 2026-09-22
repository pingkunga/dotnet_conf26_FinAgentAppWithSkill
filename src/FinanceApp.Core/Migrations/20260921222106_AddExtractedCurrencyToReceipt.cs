using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceApp.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddExtractedCurrencyToReceipt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExtractedCurrency",
                table: "Receipts",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExtractedCurrency",
                table: "Receipts");
        }
    }
}
