using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BidBuilder.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTemplateLibraryFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Category",
                table: "EstimateTemplates",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsFeatured",
                table: "EstimateTemplates",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Tags",
                table: "EstimateTemplates",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_EstimateTemplates_TenantId_Category",
                table: "EstimateTemplates",
                columns: new[] { "TenantId", "Category" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_EstimateTemplates_TenantId_Category",
                table: "EstimateTemplates");

            migrationBuilder.DropColumn(
                name: "Category",
                table: "EstimateTemplates");

            migrationBuilder.DropColumn(
                name: "IsFeatured",
                table: "EstimateTemplates");

            migrationBuilder.DropColumn(
                name: "Tags",
                table: "EstimateTemplates");
        }
    }
}
