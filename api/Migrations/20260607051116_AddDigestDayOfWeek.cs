using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace BidBuilder.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDigestDayOfWeek : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 23.1 — Default 1 = Monday (start-of-work-week) so existing opted-in users backfill
            // to a sensible day, NOT Sunday (which is what EF scaffolds for an int default).
            // Third instance of the "scaffold default doesn't match model default" gotcha
            // (after 22.1 NotificationEmailsEnabled and 22.2 ApiKey.Scopes).
            migrationBuilder.AddColumn<int>(
                name: "DayOfWeek",
                table: "NotificationDigestPreferences",
                type: "integer",
                nullable: false,
                defaultValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DayOfWeek",
                table: "NotificationDigestPreferences");
        }
    }
}
