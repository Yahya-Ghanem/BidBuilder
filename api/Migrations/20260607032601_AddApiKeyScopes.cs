using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BidBuilder.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RateLimitPerMinute",
                table: "ApiKeys",
                type: "integer",
                nullable: true);

            // Backfill pre-22.2 keys to full access so they keep working unchanged. The
            // EF default scaffold is "" (CLR default); we want "read,write" for existing rows.
            migrationBuilder.AddColumn<string>(
                name: "Scopes",
                table: "ApiKeys",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "read,write");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RateLimitPerMinute",
                table: "ApiKeys");

            migrationBuilder.DropColumn(
                name: "Scopes",
                table: "ApiKeys");
        }
    }
}
