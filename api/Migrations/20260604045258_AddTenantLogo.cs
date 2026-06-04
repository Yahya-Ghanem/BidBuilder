using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BidBuilder.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantLogo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<byte[]>(
                name: "LogoBytes",
                table: "TenantSettings",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogoContentType",
                table: "TenantSettings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LogoBytes",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "LogoContentType",
                table: "TenantSettings");
        }
    }
}
