using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BidBuilder.Api.Migrations
{
    /// <inheritdoc />
    public partial class SamlSsoConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SamlConfigs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IdpEntityId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    IdpSsoUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    IdpCertificatePem = table.Column<string>(type: "text", nullable: false),
                    EmailAttribute = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NameAttribute = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AllowJitProvisioning = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SamlConfigs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SamlConfigs_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SamlConfigs_TenantId",
                table: "SamlConfigs",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SamlConfigs");
        }
    }
}
