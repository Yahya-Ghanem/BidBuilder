using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BidBuilder.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantForeignKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddForeignKey(
                name: "FK_ActivityTypes_Tenants_TenantId",
                table: "ActivityTypes",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Areas_Tenants_TenantId",
                table: "Areas",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Assemblies_Tenants_TenantId",
                table: "Assemblies",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_AssemblyComponents_Tenants_TenantId",
                table: "AssemblyComponents",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BoqItems_Tenants_TenantId",
                table: "BoqItems",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BoqSections_Tenants_TenantId",
                table: "BoqSections",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CostComponentTypes_Tenants_TenantId",
                table: "CostComponentTypes",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_CurrencyRates_Tenants_TenantId",
                table: "CurrencyRates",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_EquipmentResources_Tenants_TenantId",
                table: "EquipmentResources",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Estimates_Tenants_TenantId",
                table: "Estimates",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_GroupModules_Tenants_TenantId",
                table: "GroupModules",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Groups_Tenants_TenantId",
                table: "Groups",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ItemCostComponents_Tenants_TenantId",
                table: "ItemCostComponents",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_LaborResources_Tenants_TenantId",
                table: "LaborResources",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Markups_Tenants_TenantId",
                table: "Markups",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MaterialResources_Tenants_TenantId",
                table: "MaterialResources",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Modules_Tenants_TenantId",
                table: "Modules",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Preliminaries_Tenants_TenantId",
                table: "Preliminaries",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectTeams_Tenants_TenantId",
                table: "ProjectTeams",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ProjectTypes_Tenants_TenantId",
                table: "ProjectTypes",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Projects_Tenants_TenantId",
                table: "Projects",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Subcontractors_Tenants_TenantId",
                table: "Subcontractors",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_UserGroups_Tenants_TenantId",
                table: "UserGroups",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_Tenants_TenantId",
                table: "Users",
                column: "TenantId",
                principalTable: "Tenants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ActivityTypes_Tenants_TenantId",
                table: "ActivityTypes");

            migrationBuilder.DropForeignKey(
                name: "FK_Areas_Tenants_TenantId",
                table: "Areas");

            migrationBuilder.DropForeignKey(
                name: "FK_Assemblies_Tenants_TenantId",
                table: "Assemblies");

            migrationBuilder.DropForeignKey(
                name: "FK_AssemblyComponents_Tenants_TenantId",
                table: "AssemblyComponents");

            migrationBuilder.DropForeignKey(
                name: "FK_BoqItems_Tenants_TenantId",
                table: "BoqItems");

            migrationBuilder.DropForeignKey(
                name: "FK_BoqSections_Tenants_TenantId",
                table: "BoqSections");

            migrationBuilder.DropForeignKey(
                name: "FK_CostComponentTypes_Tenants_TenantId",
                table: "CostComponentTypes");

            migrationBuilder.DropForeignKey(
                name: "FK_CurrencyRates_Tenants_TenantId",
                table: "CurrencyRates");

            migrationBuilder.DropForeignKey(
                name: "FK_EquipmentResources_Tenants_TenantId",
                table: "EquipmentResources");

            migrationBuilder.DropForeignKey(
                name: "FK_Estimates_Tenants_TenantId",
                table: "Estimates");

            migrationBuilder.DropForeignKey(
                name: "FK_GroupModules_Tenants_TenantId",
                table: "GroupModules");

            migrationBuilder.DropForeignKey(
                name: "FK_Groups_Tenants_TenantId",
                table: "Groups");

            migrationBuilder.DropForeignKey(
                name: "FK_ItemCostComponents_Tenants_TenantId",
                table: "ItemCostComponents");

            migrationBuilder.DropForeignKey(
                name: "FK_LaborResources_Tenants_TenantId",
                table: "LaborResources");

            migrationBuilder.DropForeignKey(
                name: "FK_Markups_Tenants_TenantId",
                table: "Markups");

            migrationBuilder.DropForeignKey(
                name: "FK_MaterialResources_Tenants_TenantId",
                table: "MaterialResources");

            migrationBuilder.DropForeignKey(
                name: "FK_Modules_Tenants_TenantId",
                table: "Modules");

            migrationBuilder.DropForeignKey(
                name: "FK_Preliminaries_Tenants_TenantId",
                table: "Preliminaries");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectTeams_Tenants_TenantId",
                table: "ProjectTeams");

            migrationBuilder.DropForeignKey(
                name: "FK_ProjectTypes_Tenants_TenantId",
                table: "ProjectTypes");

            migrationBuilder.DropForeignKey(
                name: "FK_Projects_Tenants_TenantId",
                table: "Projects");

            migrationBuilder.DropForeignKey(
                name: "FK_Subcontractors_Tenants_TenantId",
                table: "Subcontractors");

            migrationBuilder.DropForeignKey(
                name: "FK_UserGroups_Tenants_TenantId",
                table: "UserGroups");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_Tenants_TenantId",
                table: "Users");
        }
    }
}
