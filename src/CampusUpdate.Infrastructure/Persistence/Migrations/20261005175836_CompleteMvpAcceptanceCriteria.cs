using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CampusUpdate.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteMvpAcceptanceCriteria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "Aggregated",
                table: "UserActivities",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "TargetAudience",
                table: "ContentAudiences",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "DailyUsageCounters",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstitutionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Day = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Registrations = table.Column<long>(type: "bigint", nullable: false),
                    Sessions = table.Column<long>(type: "bigint", nullable: false),
                    FeedFetches = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyUsageCounters", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DailyUsageCounters_InstitutionId_Day",
                table: "DailyUsageCounters",
                columns: new[] { "InstitutionId", "Day" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DailyUsageCounters");

            migrationBuilder.DropColumn(
                name: "Aggregated",
                table: "UserActivities");

            migrationBuilder.DropColumn(
                name: "TargetAudience",
                table: "ContentAudiences");
        }
    }
}
