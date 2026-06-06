using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BidBuilder.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddBidOutcomeRegister : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AwardedValue",
                table: "Projects",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DecisionAt",
                table: "Projects",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "FinalCost",
                table: "Projects",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SubmittedBidValue",
                table: "Projects",
                type: "numeric(18,2)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "WinLossNote",
                table: "Projects",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Projects_TenantId_DecisionAt",
                table: "Projects",
                columns: new[] { "TenantId", "DecisionAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Projects_TenantId_DecisionAt",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "AwardedValue",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "DecisionAt",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "FinalCost",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "SubmittedBidValue",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "WinLossNote",
                table: "Projects");
        }
    }
}
