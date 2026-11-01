using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CampusUpdate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RetireUnusedUserRolePostgresEnum : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:content_status", "draft,pending_approval,published,rejected,archived")
                .Annotation("Npgsql:Enum:content_type", "news,announcement,event,advertisement")
                .Annotation("Npgsql:Enum:source_type", "official,external,sponsored")
                .Annotation("Npgsql:Enum:urgency_level", "normal,important,urgent")
                .OldAnnotation("Npgsql:Enum:content_status", "draft,pending_approval,published,rejected,archived")
                .OldAnnotation("Npgsql:Enum:content_type", "news,announcement,event,advertisement")
                .OldAnnotation("Npgsql:Enum:source_type", "official,external,sponsored")
                .OldAnnotation("Npgsql:Enum:urgency_level", "normal,important,urgent")
                .OldAnnotation("Npgsql:Enum:user_role", "student,staff,school_admin,super_admin,lead_school_admin,news_admin,calendar_admin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:Enum:content_status", "draft,pending_approval,published,rejected,archived")
                .Annotation("Npgsql:Enum:content_type", "news,announcement,event,advertisement")
                .Annotation("Npgsql:Enum:source_type", "official,external,sponsored")
                .Annotation("Npgsql:Enum:urgency_level", "normal,important,urgent")
                .Annotation("Npgsql:Enum:user_role", "student,staff,school_admin,super_admin,lead_school_admin,news_admin,calendar_admin")
                .OldAnnotation("Npgsql:Enum:content_status", "draft,pending_approval,published,rejected,archived")
                .OldAnnotation("Npgsql:Enum:content_type", "news,announcement,event,advertisement")
                .OldAnnotation("Npgsql:Enum:source_type", "official,external,sponsored")
                .OldAnnotation("Npgsql:Enum:urgency_level", "normal,important,urgent");
        }
    }
}
