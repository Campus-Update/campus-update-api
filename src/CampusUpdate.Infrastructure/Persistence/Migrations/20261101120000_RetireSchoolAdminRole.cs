using CampusUpdate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CampusUpdate.Infrastructure.Persistence.Migrations;

[DbContext(typeof(CampusUpdateDbContext))]
[Migration("20261101120000_RetireSchoolAdminRole")]
public sealed class RetireSchoolAdminRole : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // The stored Role column is an integer; preserve stable values and map
        // the retired SchoolAdmin value (2) to LeadSchoolAdmin (4).
        migrationBuilder.Sql("UPDATE \"Users\" SET \"Role\" = 4 WHERE \"Role\" = 2;");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Intentionally irreversible: converting every LeadSchoolAdmin back
        // would also downgrade accounts created after this migration.
    }
}
