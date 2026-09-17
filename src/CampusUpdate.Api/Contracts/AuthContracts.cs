using System.ComponentModel.DataAnnotations;
using CampusUpdate.Domain.Users;

namespace CampusUpdate.Api.Contracts;

public sealed record RegisterRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(8)] string Password,
    [Required, MaxLength(100)] string FirstName,
    [Required, MaxLength(100)] string LastName,
    UserRole Role,
    Guid InstitutionId,
    Guid? FacultyId,
    Guid? DepartmentId,
    Guid? ProgrammeId,
    Guid? AcademicLevelId,
    string? MatriculationOrStaffNumber);

public sealed record LoginRequest([EmailAddress] string Email, string Password);
public sealed record RefreshRequest(string RefreshToken);
public sealed record AuthResponse(Guid UserId, string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt);
public sealed record UpdateProfileRequest(
    [Required, MaxLength(100)] string FirstName,
    [Required, MaxLength(100)] string LastName,
    string? MatriculationOrStaffNumber);
public sealed record AcademicSettingsRequest(
    Guid InstitutionId,
    Guid? FacultyId,
    Guid? DepartmentId,
    Guid? ProgrammeId,
    Guid? AcademicLevelId,
    bool NewsEnabled,
    bool AnnouncementsEnabled,
    bool EventsEnabled,
    bool AdvertisementsEnabled,
    bool PushNotificationsEnabled,
    bool AllCampusFeed = false);
public sealed record UserProfileResponse(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    UserRole Role,
    string? MatriculationOrStaffNumber,
    Guid InstitutionId,
    Guid? FacultyId,
    Guid? DepartmentId,
    Guid? ProgrammeId,
    Guid? AcademicLevelId,
    bool NewsEnabled,
    bool AnnouncementsEnabled,
    bool EventsEnabled,
    bool AdvertisementsEnabled,
    bool PushNotificationsEnabled,
    bool AllCampusFeed);
