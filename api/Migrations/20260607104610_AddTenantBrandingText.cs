using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BidBuilder.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantBrandingText : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BrandFooterText",
                table: "TenantSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BrandHeaderText",
                table: "TenantSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BrandSignatureText",
                table: "TenantSettings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BrandFooterText",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "BrandHeaderText",
                table: "TenantSettings");

            migrationBuilder.DropColumn(
                name: "BrandSignatureText",
                table: "TenantSettings");
        }
    }
}
