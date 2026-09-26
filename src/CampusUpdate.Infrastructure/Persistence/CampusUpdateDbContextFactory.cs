using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CampusUpdate.Infrastructure.Persistence;

public sealed class CampusUpdateDbContextFactory : IDesignTimeDbContextFactory<CampusUpdateDbContext>
{
    public CampusUpdateDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Postgres")
            ?? "Host=localhost;Port=5432;Database=campus_update;Username=postgres;Password=postgres";
        var options = new DbContextOptionsBuilder<CampusUpdateDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new CampusUpdateDbContext(options);
    }
}
