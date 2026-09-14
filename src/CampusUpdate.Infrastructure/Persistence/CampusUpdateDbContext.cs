using CampusUpdate.Domain.Common;
using CampusUpdate.Domain.Content;
using CampusUpdate.Domain.Schools;
using CampusUpdate.Domain.Users;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Infrastructure.Persistence;

public sealed class CampusUpdateDbContext(DbContextOptions<CampusUpdateDbContext> options)
    : DbContext(options)
{
    public DbSet<Institution> Institutions => Set<Institution>();
    public DbSet<Faculty> Faculties => Set<Faculty>();
    public DbSet<Department> Departments => Set<Department>();
    public DbSet<Programme> Programmes => Set<Programme>();
    public DbSet<AcademicLevel> AcademicLevels => Set<AcademicLevel>();
    public DbSet<AcademicCalendar> AcademicCalendars => Set<AcademicCalendar>();
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<FeedPreference> FeedPreferences => Set<FeedPreference>();
    public DbSet<ContentItem> ContentItems => Set<ContentItem>();
    public DbSet<ContentAudience> ContentAudiences => Set<ContentAudience>();
    public DbSet<ContentAttachment> ContentAttachments => Set<ContentAttachment>();

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries<Entity>())
        {
            if (entry.State == EntityState.Added)
                entry.Entity.CreatedAt = now;
            if (entry.State is EntityState.Added or EntityState.Modified)
                entry.Entity.UpdatedAt = now;
        }

        return base.SaveChangesAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresEnum<UserRole>();
        modelBuilder.HasPostgresEnum<ContentType>();
        modelBuilder.HasPostgresEnum<ContentStatus>();
        modelBuilder.HasPostgresEnum<UrgencyLevel>();
        modelBuilder.HasPostgresEnum<SourceType>();

        ConfigureSchools(modelBuilder);
        ConfigureUsers(modelBuilder);
        ConfigureContent(modelBuilder);
    }

    private static void ConfigureSchools(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Institution>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Slug).HasMaxLength(120);
            entity.HasIndex(x => x.Slug).IsUnique();
        });

        modelBuilder.Entity<Faculty>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Code).HasMaxLength(30);
            entity.HasIndex(x => new { x.InstitutionId, x.Code }).IsUnique();
        });

        modelBuilder.Entity<Department>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Code).HasMaxLength(30);
            entity.HasIndex(x => new { x.FacultyId, x.Code }).IsUnique();
        });

        modelBuilder.Entity<Programme>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(200);
            entity.Property(x => x.Code).HasMaxLength(30);
            entity.HasIndex(x => new { x.DepartmentId, x.Code }).IsUnique();
        });

        modelBuilder.Entity<AcademicLevel>(entity =>
        {
            entity.Property(x => x.Name).HasMaxLength(50);
            entity.HasIndex(x => new { x.ProgrammeId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<AcademicCalendar>(entity =>
        {
            entity.Property(x => x.Title).HasMaxLength(250);
            entity.Property(x => x.AcademicSession).HasMaxLength(50);
            entity.Property(x => x.ImageUrl).HasMaxLength(2048);
            entity.HasIndex(x => new { x.InstitutionId, x.IsOfficial, x.PublishedAt });
            entity.HasOne(x => x.Institution)
                .WithMany(x => x.AcademicCalendars)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureUsers(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.Property(x => x.Email).HasMaxLength(320);
            entity.Property(x => x.FirstName).HasMaxLength(100);
            entity.Property(x => x.LastName).HasMaxLength(100);
            entity.HasIndex(x => x.Email).IsUnique();
            entity.HasOne(x => x.Institution).WithMany().OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Faculty).WithMany().OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Department).WithMany().OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Programme).WithMany().OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.AcademicLevel).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FeedPreference>()
            .HasOne(x => x.User)
            .WithOne(x => x.FeedPreference)
            .HasForeignKey<FeedPreference>(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }

    private static void ConfigureContent(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ContentItem>(entity =>
        {
            entity.Property(x => x.Title).HasMaxLength(250);
            entity.Property(x => x.SourceName).HasMaxLength(200);
            entity.HasIndex(x => new { x.Status, x.PublishedAt });
            entity.HasIndex(x => new { x.Type, x.Status });
            entity.HasOne(x => x.Author).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ContentAudience>(entity =>
        {
            entity.HasIndex(x => new
            {
                x.ContentItemId,
                x.InstitutionId,
                x.FacultyId,
                x.DepartmentId,
                x.ProgrammeId,
                x.AcademicLevelId
            }).IsUnique();
            entity.HasOne(x => x.Institution).WithMany().OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Faculty).WithMany().OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Department).WithMany().OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Programme).WithMany().OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.AcademicLevel).WithMany().OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ContentAttachment>(entity =>
        {
            entity.Property(x => x.FileName).HasMaxLength(255);
            entity.Property(x => x.Url).HasMaxLength(2048);
            entity.Property(x => x.ContentType).HasMaxLength(150);
        });
    }
}
