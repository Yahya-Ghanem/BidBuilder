using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BidBuilder.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCostComponents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CostComponentTypes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CalcKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Builtin = table.Column<bool>(type: "boolean", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CostComponentTypes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ItemCostComponents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoqItemId = table.Column<int>(type: "integer", nullable: false),
                    CostComponentTypeId = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<decimal>(type: "numeric(18,4)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ItemCostComponents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ItemCostComponents_BoqItems_BoqItemId",
                        column: x => x.BoqItemId,
                        principalTable: "BoqItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ItemCostComponents_CostComponentTypes_CostComponentTypeId",
                        column: x => x.CostComponentTypeId,
                        principalTable: "CostComponentTypes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CostComponentTypes_TenantId",
                table: "CostComponentTypes",
                column: "TenantId");

            migrationBuilder.CreateIndex(
                name: "IX_CostComponentTypes_TenantId_Code",
                table: "CostComponentTypes",
                columns: new[] { "TenantId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemCostComponents_BoqItemId_CostComponentTypeId",
                table: "ItemCostComponents",
                columns: new[] { "BoqItemId", "CostComponentTypeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ItemCostComponents_CostComponentTypeId",
                table: "ItemCostComponents",
                column: "CostComponentTypeId");

            migrationBuilder.CreateIndex(
                name: "IX_ItemCostComponents_TenantId",
                table: "ItemCostComponents",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ItemCostComponents");

            migrationBuilder.DropTable(
                name: "CostComponentTypes");
        }
    }
}
