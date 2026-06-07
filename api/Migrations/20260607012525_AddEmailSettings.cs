using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BidBuilder.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Default ON so existing tenants keep getting notified once SMTP is
            // configured; a tenant admin can opt out via Settings. New rows inserted
            // through EF use the entity initializer (also true).
            migrationBuilder.AddColumn<bool>(
                name: "NotificationEmailsEnabled",
                table: "TenantSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotificationEmailsEnabled",
                table: "TenantSettings");
        }
    }
}
