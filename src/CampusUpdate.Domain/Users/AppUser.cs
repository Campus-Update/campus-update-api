using CampusUpdate.Domain.Common;
using CampusUpdate.Domain.Schools;

namespace CampusUpdate.Domain.Users;

public enum UserRole
{
    Student,
    Staff,
    SchoolAdmin,
    SuperAdmin
}

public sealed class AppUser : Entity
{
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public UserRole Role { get; set; }
    public string? MatriculationOrStaffNumber { get; set; }
    public bool IsActive { get; set; } = true;
    public Guid InstitutionId { get; set; }
    public Institution Institution { get; set; } = null!;
    public Guid? FacultyId { get; set; }
    public Faculty? Faculty { get; set; }
    public Guid? DepartmentId { get; set; }
    public Department? Department { get; set; }
    public Guid? ProgrammeId { get; set; }
    public Programme? Programme { get; set; }
    public Guid? AcademicLevelId { get; set; }
    public AcademicLevel? AcademicLevel { get; set; }
    public FeedPreference FeedPreference { get; set; } = null!;
    public string? RefreshTokenHash { get; set; }
    public DateTimeOffset? RefreshTokenExpiresAt { get; set; }
}

public sealed class FeedPreference : Entity
{
    public bool AllCampusFeed { get; set; }
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public bool NewsEnabled { get; set; } = true;
    public bool AnnouncementsEnabled { get; set; } = true;
    public bool EventsEnabled { get; set; } = true;
    public bool AdvertisementsEnabled { get; set; } = true;
    public bool PushNotificationsEnabled { get; set; } = true;
}
