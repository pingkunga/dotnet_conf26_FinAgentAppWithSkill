using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceApp.Core.Migrations
{
    /// <summary>
    /// Data-only migration — ReceiptOcrStatus.Unsupported was renamed to Manual in the C# enum (clearer
    /// label: "entered by a human", not "an error"). The column is mapped .HasConversion&lt;string&gt;()
    /// (FinanceDbContext.OnModelCreating), so Postgres stores the literal member name as text; an enum
    /// rename alone produces zero schema diff (dotnet ef migrations add generated an empty Up/Down here)
    /// but leaves any already-saved 'Unsupported' rows unreadable — confirmed for real against a live DB
    /// (2026-08-18): the app threw `InvalidOperationException: Cannot convert string value 'Unsupported'...`
    /// the moment the renamed code ran against existing data. This migration is the actual fix.
    /// </summary>
    public partial class RenameReceiptOcrStatusUnsupportedToManual : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE \"Receipts\" SET \"OcrStatus\" = 'Manual' WHERE \"OcrStatus\" = 'Unsupported';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE \"Receipts\" SET \"OcrStatus\" = 'Unsupported' WHERE \"OcrStatus\" = 'Manual';");
        }
    }
}
