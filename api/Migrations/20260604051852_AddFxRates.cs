using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BidBuilder.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFxRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "FxRate",
                table: "Estimates",
                type: "numeric(18,6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FxRateAt",
                table: "Estimates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SecondaryCurrency",
                table: "Estimates",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "CurrencyRates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    RateToBase = table.Column<decimal>(type: "numeric(18,6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurrencyRates", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CurrencyRates_TenantId",
                table: "CurrencyRates",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CurrencyRates_TenantId_Code",
                table: "CurrencyRates",
                columns: new[] { "TenantId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CurrencyRates");

            migrationBuilder.DropColumn(
                name: "FxRate",
                table: "Estimates");

            migrationBuilder.DropColumn(
                name: "FxRateAt",
                table: "Estimates");

            migrationBuilder.DropColumn(
                name: "SecondaryCurrency",
                table: "Estimates");
        }
    }
}
