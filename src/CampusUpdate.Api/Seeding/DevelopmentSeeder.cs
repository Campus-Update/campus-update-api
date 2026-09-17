using CampusUpdate.Domain.Content;
using CampusUpdate.Domain.Schools;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Api.Seeding;

public static class DevelopmentSeeder
{
    public static async Task Run(IServiceProvider services, IConfiguration configuration)
    {
        var password = configuration["Seed:Password"];
        if (string.IsNullOrWhiteSpace(password) || password.Length < 12)
            throw new InvalidOperationException("Set Seed__Password to a development-only password of at least 12 characters.");
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
        await db.Database.MigrateAsync();
        // One transaction makes retries safe if the seed fails partway through.
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (await db.Institutions.AnyAsync(x => x.Slug == "focit-demo"))
            return;
        var level = new AcademicLevel { Name = "100 Level", SortOrder = 100 };
        var programme = new Programme
        {
            Name = "Computer Science",
            Code = "BSC-CS",
            Levels = [level, new AcademicLevel { Name = "200 Level", SortOrder = 200 }]
        };
        var department = new Department { Name = "Computer Science", Code = "CSC", Programmes = [programme] };
        var faculty = new Faculty { Name = "Computing", Code = "COMP", Departments = [department] };
        var institution = new Institution
        {
            Name = "FOCIT Demo (synthetic fixture)",
            Slug = "focit-demo",
            State = "Demo",
            Faculties = [faculty]
        };
        db.Institutions.Add(institution);
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>();
        AppUser CreateUser(string email, UserRole role)
        {
            var user = new AppUser
            {
                Email = email,
                FirstName = "Demo",
                LastName = role.ToString(),
                PasswordHash = string.Empty,
                Role = role,
                Institution = institution,
                Faculty = faculty,
                Department = department,
                Programme = programme,
                AcademicLevel = level,
                FeedPreference = new FeedPreference()
            };
            user.PasswordHash = hasher.HashPassword(user, password);
            db.Users.Add(user);
            return user;
        }
        CreateUser("superadmin@campus-update.test", UserRole.SuperAdmin);
        var admin = CreateUser("admin@campus-update.test", UserRole.SchoolAdmin);
        CreateUser("student@campus-update.test", UserRole.Student);
        CreateUser("staff@campus-update.test", UserRole.Staff);
        var publishedAt = DateTimeOffset.UtcNow;
        foreach (var (type, title, source, sourceName) in new[]
        {
            (ContentType.News, "Welcome to the pilot", SourceType.Official, "Official School"),
            (ContentType.Announcement, "Registration deadline", SourceType.Official, "CAMPUS UPDATE"),
            (ContentType.Event, "Orientation day", SourceType.External, "CAMPUS UPDATE"),
            (ContentType.Advertisement, "Demo bookstore offer", SourceType.Sponsored, "Sponsored")
        })
        {
            db.ContentItems.Add(new ContentItem
            {
                Title = title,
                Body = "Synthetic development content; not an actual school announcement.",
                Type = type,
                Status = ContentStatus.Published,
                PublishedAt = publishedAt,
                SourceType = source,
                SourceName = sourceName,
                Author = admin,
                Urgency = type == ContentType.Announcement ? UrgencyLevel.Urgent : UrgencyLevel.Normal,
                EventStartsAt = type == ContentType.Event ? publishedAt.AddDays(7) : null,
                TargetUrl = type == ContentType.Advertisement ? "https://example.com" : null,
                SponsorName = type == ContentType.Advertisement ? "Demo Bookstore" : null,
                Audiences = [new ContentAudience { Institution = institution }]
            });
        }
        db.AcademicCalendars.Add(new AcademicCalendar
        {
            Institution = institution,
            Title = "Demo academic calendar",
            AcademicSession = "2026/2027",
            ImageUrl = "https://example.com/demo-calendar.png",
            PublishedAt = publishedAt
        });
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = admin.Id,
            Action = "DevelopmentSeedCreated",
            TargetId = institution.Id,
            InstitutionId = institution.Id,
            Details = "Synthetic development fixtures."
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}
