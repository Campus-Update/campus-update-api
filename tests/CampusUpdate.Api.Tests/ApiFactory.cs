using CampusUpdate.Api.Authentication;
using CampusUpdate.Domain.Content;
using CampusUpdate.Domain.Schools;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CampusUpdate.Api.Tests;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _databaseName = Guid.NewGuid().ToString();
    public Guid SchoolId { get; } = Guid.NewGuid();
    public Guid OtherSchoolId { get; } = Guid.NewGuid();
    public Guid ForeignFacultyId { get; } = Guid.NewGuid();
    public Guid DraftId { get; } = Guid.NewGuid();
    public Guid PublishedId { get; } = Guid.NewGuid();
    public Guid TargetedId { get; } = Guid.NewGuid();
    public Guid ForeignPublishedId { get; } = Guid.NewGuid();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = "Host=unused;Database=unused",
            ["Jwt:Key"] = "integration-test-signing-key-with-at-least-32-bytes"
        }));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<CampusUpdateDbContext>();
            services.RemoveAll<DbContextOptions<CampusUpdateDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<CampusUpdateDbContext>>();
            services.AddDbContext<CampusUpdateDbContext>(options => options.UseInMemoryDatabase(_databaseName));
        });
    }

    public void Seed()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
        if (db.Institutions.Any())
            return;
        var school = new Institution { Id = SchoolId, Name = "School A", Slug = "school-a" };
        var other = new Institution { Id = OtherSchoolId, Name = "School B", Slug = "school-b" };
        var faculty = new Faculty { Id = ForeignFacultyId, Name = "Foreign faculty", Code = "FOREIGN", Institution = other };
        var ownFaculty = new Faculty { Name = "Computing", Code = "COMP", Institution = school };
        var author = new AppUser
        {
            Email = "author@example.test",
            PasswordHash = "unused",
            FirstName = "Author",
            LastName = "Admin",
            Role = UserRole.SchoolAdmin,
            Institution = other,
            FeedPreference = new FeedPreference()
        };
        db.AddRange(school, other, faculty, ownFaculty, author);
        db.ContentItems.AddRange(
            new ContentItem
            {
                Id = ForeignPublishedId,
                Title = "Foreign published news",
                Body = "Published",
                SourceName = "School B",
                Author = author,
                Status = ContentStatus.Published,
                Audiences = [new ContentAudience { Institution = other }]
            },
            new ContentItem
            {
                Id = DraftId,
                Title = "Private foreign draft",
                Body = "Private",
                SourceName = "School B",
                Author = author,
                Audiences = [new ContentAudience { Institution = other }]
            },
            new ContentItem
            {
                Id = PublishedId,
                Title = "Campus news",
                Body = "Published",
                SourceName = "School A",
                Author = author,
                Status = ContentStatus.Published,
                Audiences = [new ContentAudience { Institution = school }]
            },
            new ContentItem
            {
                Id = TargetedId,
                Title = "Faculty news",
                Body = "Published",
                SourceName = "School A",
                Author = author,
                Status = ContentStatus.Published,
                Audiences = [new ContentAudience { Institution = school, Faculty = ownFaculty }]
            });
        db.SaveChanges();
    }

    public (Guid UserId, string Token) AddUser(UserRole role, Guid? schoolId = null)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
        var user = new AppUser
        {
            Email = $"{Guid.NewGuid()}@example.test",
            PasswordHash = "unused",
            FirstName = "Test",
            LastName = role.ToString(),
            Role = role,
            InstitutionId = schoolId ?? SchoolId,
            FeedPreference = new FeedPreference()
        };
        db.Users.Add(user);
        db.SaveChanges();
        return (user.Id, scope.ServiceProvider.GetRequiredService<ITokenService>().Create(user).AccessToken);
    }
}
