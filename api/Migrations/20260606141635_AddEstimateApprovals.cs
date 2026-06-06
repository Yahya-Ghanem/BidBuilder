using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace BidBuilder.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEstimateApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RequiredApprovalsToPublish",
                table: "TenantSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "EstimateApprovals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    EstimateId = table.Column<int>(type: "integer", nullable: false),
                    ApproverUserId = table.Column<int>(type: "integer", nullable: false),
                    ApproverEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    ApproverName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EstimateApprovals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EstimateApprovals_Estimates_EstimateId",
                        column: x => x.EstimateId,
                        principalTable: "Estimates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_EstimateApprovals_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EstimateApprovals_EstimateId",
                table: "EstimateApprovals",
                column: "EstimateId");

            migrationBuilder.CreateIndex(
                name: "IX_EstimateApprovals_EstimateId_ApproverUserId",
                table: "EstimateApprovals",
                columns: new[] { "EstimateId", "ApproverUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_EstimateApprovals_TenantId",
                table: "EstimateApprovals",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EstimateApprovals");

            migrationBuilder.DropColumn(
                name: "RequiredApprovalsToPublish",
                table: "TenantSettings");
        }
    }
}
