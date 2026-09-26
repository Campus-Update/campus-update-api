using CampusUpdate.Api.Authentication;
using CampusUpdate.Domain.Content;
using CampusUpdate.Domain.Schools;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace CampusUpdate.Api.Tests;

public sealed class CoreSchemaTests
{
    [Fact]
    public async Task AcademicHierarchyCanBePersisted()
    {
        await using var db = CreateContext();
        var institution = new Institution
        {
            Name = "University of Example",
            Slug = "university-of-example",
            Faculties =
            [
                new Faculty
                {
                    Name = "Computing",
                    Code = "COMP",
                    Departments =
                    [
                        new Department
                        {
                            Name = "Computer Science",
                            Code = "CSC",
                            Programmes =
                            [
                                new Programme
                                {
                                    Name = "Computer Science",
                                    Code = "BSC-CS",
                                    Levels = [new AcademicLevel { Name = "100 Level", SortOrder = 100 }]
                                }
                            ]
                        }
                    ]
                }
            ]
        };

        db.Institutions.Add(institution);
        await db.SaveChangesAsync();

        var saved = await db.Institutions
            .Include(x => x.Faculties).ThenInclude(x => x.Departments)
            .ThenInclude(x => x.Programmes).ThenInclude(x => x.Levels)
            .SingleAsync();
        Assert.Equal("100 Level", saved.Faculties.Single().Departments.Single().Programmes.Single().Levels.Single().Name);
    }

    [Fact]
    public async Task ContentAudienceSupportsInstitutionWideTargeting()
    {
        await using var db = CreateContext();
        var institution = new Institution { Name = "Example", Slug = "example" };
        var author = new AppUser
        {
            Email = "admin@example.test",
            PasswordHash = "hash",
            FirstName = "School",
            LastName = "Admin",
            Role = UserRole.SchoolAdmin,
            Institution = institution,
            FeedPreference = new FeedPreference()
        };
        var content = new ContentItem
        {
            Title = "Registration opens",
            Body = "Registration is now open.",
            Type = ContentType.Announcement,
            Urgency = UrgencyLevel.Important,
            SourceName = "Registry",
            Author = author,
            Audiences = [new ContentAudience { Institution = institution }]
        };
        db.ContentItems.Add(content);
        await db.SaveChangesAsync();

        var saved = await db.ContentItems.Include(x => x.Audiences).SingleAsync();
        Assert.Null(saved.Audiences.Single().FacultyId);
        Assert.Equal(UrgencyLevel.Important, saved.Urgency);
    }

    [Fact]
    public async Task LatestOfficialAcademicCalendarCanBeSelected()
    {
        await using var db = CreateContext();
        var institution = new Institution { Name = "Example", Slug = "example" };
        db.AcademicCalendars.AddRange(
            new AcademicCalendar
            {
                Institution = institution,
                Title = "Draft calendar",
                AcademicSession = "2026/2027",
                ImageUrl = "https://example.test/draft.png",
                IsOfficial = false,
                PublishedAt = new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero)
            },
            new AcademicCalendar
            {
                Institution = institution,
                Title = "Official calendar",
                AcademicSession = "2026/2027",
                ImageUrl = "https://example.test/official.png",
                PublishedAt = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)
            });
        await db.SaveChangesAsync();

        var latestOfficial = await db.AcademicCalendars
            .Where(x => x.InstitutionId == institution.Id && x.IsOfficial)
            .OrderByDescending(x => x.PublishedAt)
            .FirstAsync();

        Assert.Equal("https://example.test/official.png", latestOfficial.ImageUrl);
    }

    [Fact]
    public void TokenServiceCreatesSignedAccessAndRotatableRefreshTokens()
    {
        var service = new TokenService(Options.Create(new JwtOptions
        {
            Issuer = "tests",
            Audience = "tests",
            Key = "a-test-key-that-is-at-least-thirty-two-bytes-long"
        }));
        var user = new AppUser
        {
            Email = "student@example.test",
            PasswordHash = "hash",
            FirstName = "Test",
            LastName = "Student",
            InstitutionId = Guid.NewGuid()
        };

        var first = service.Create(user);
        var second = service.Create(user);

        Assert.NotEmpty(first.AccessToken);
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.NotEqual(first.RefreshToken, service.HashRefreshToken(first.RefreshToken));
    }

    private static CampusUpdateDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<CampusUpdateDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new CampusUpdateDbContext(options);
    }
}
