using System.ComponentModel.DataAnnotations;
using CampusUpdate.Domain.Schools;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Api.Seeding;

public static class AdminBootstrap
{
    // Explicit operator command, never an HTTP endpoint or automatic startup action.
    public static async Task Run(IServiceProvider services, IConfiguration configuration)
    {
        string Required(string name) => !string.IsNullOrWhiteSpace(configuration[$"Bootstrap:{name}"])
            ? configuration[$"Bootstrap:{name}"]!.Trim()
            : throw new InvalidOperationException($"Bootstrap:{name} is required.");
        var email = Required("Email").ToLowerInvariant();
        var password = configuration["Bootstrap:Password"];
        var schoolName = Required("SchoolName");
        var schoolSlug = Required("SchoolSlug");
        var schoolState = Required("SchoolState");
        if (!new EmailAddressAttribute().IsValid(email) || email.Length > 320 ||
            string.IsNullOrWhiteSpace(password) || password.Length < 12 ||
            schoolName.Length > 200 || schoolSlug.Length > 120 || schoolState.Length > 100 ||
            !System.Text.RegularExpressions.Regex.IsMatch(schoolSlug, "^[a-z0-9]+(?:-[a-z0-9]+)*$"))
            throw new InvalidOperationException("Invalid bootstrap email, password (minimum 12 characters), or school fields.");
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CampusUpdateDbContext>();
        // Migrations are an independent operator step. Serializes concurrent bootstrap attempts on PostgreSQL.
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(729104821)");
        if (await db.Users.AnyAsync(x => x.Role == UserRole.SuperAdmin))
            throw new InvalidOperationException("A super administrator already exists. Bootstrap is only for the first account.");
        if (await db.Users.AnyAsync(x => x.Email == email))
            throw new InvalidOperationException("Bootstrap email already belongs to an account.");
        var school = await db.Institutions.SingleOrDefaultAsync(x => x.Slug == schoolSlug);
        if (school is null)
        {
            school = new Institution { Name = schoolName, Slug = schoolSlug, State = schoolState };
            db.Institutions.Add(school);
        }
        else if (!school.IsActive)
            throw new InvalidOperationException("Bootstrap institution must be active.");
        var user = new AppUser
        {
            Email = email,
            PasswordHash = string.Empty,
            FirstName = "Platform",
            LastName = "Administrator",
            Role = UserRole.SuperAdmin,
            Institution = school,
            FeedPreference = new FeedPreference()
        };
        user.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AppUser>>().HashPassword(user, password);
        db.Users.Add(user);
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = user.Id,
            Action = "InitialSuperAdminBootstrapped",
            TargetId = user.Id,
            InstitutionId = school.Id,
            Details = "Explicit operator bootstrap."
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}
