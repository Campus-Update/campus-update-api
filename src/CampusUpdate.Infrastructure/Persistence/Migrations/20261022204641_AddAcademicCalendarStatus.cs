using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CampusUpdate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAcademicCalendarStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "AcademicCalendars",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Published");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Status",
                table: "AcademicCalendars");
        }
    }
}
