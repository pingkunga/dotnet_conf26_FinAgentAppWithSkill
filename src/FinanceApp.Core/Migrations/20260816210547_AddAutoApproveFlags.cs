using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FinanceApp.Core.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoApproveFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoApproveExecuteScript",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "AutoApproveWrites",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoApproveExecuteScript",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "AutoApproveWrites",
                table: "AspNetUsers");
        }
    }
}
