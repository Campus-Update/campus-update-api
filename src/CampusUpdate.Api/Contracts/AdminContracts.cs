using System.ComponentModel.DataAnnotations;
using CampusUpdate.Domain.Users;

namespace CampusUpdate.Api.Contracts;

public sealed record CreateSchoolRequest(
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(120), RegularExpression("^[a-z0-9]+(?:-[a-z0-9]+)*$")] string Slug,
    [Required, MaxLength(100)] string State,
    bool IsActive = true);

public sealed record AdminSchoolResponse(Guid Id, string Name, string Slug, string? State, bool IsActive);

public sealed record AdminSchoolListItemResponse(
    Guid Id,
    string Name,
    string Slug,
    string? State,
    string? Acronym,
    string? LogoUrl,
    bool IsActive,
    int StudentCount,
    int StaffCount,
    int AdminCount,
    int LiveContentCount,
    int WeeklyActiveCount = 0,
    int ViewCount = 0);

public sealed record AdminSchoolPageResponse(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyCollection<AdminSchoolListItemResponse> Items);

public sealed record AdminSchoolDetailResponse(
    Guid Id,
    string Name,
    string Slug,
    string? State,
    string? Acronym,
    string? LogoUrl,
    bool IsActive,
    int StudentCount,
    int StaffCount,
    int AdminCount,
    int LiveContentCount,
    IReadOnlyCollection<FacultyResponse> Faculties);

public sealed record InstitutionStatsResponse(
    int TotalInstitutions,
    int ActiveInstitutions,
    int PendingVerificationInstitutions,
    int StudentsReached);

public sealed record DepartmentAdminResponse(
    Guid Id,
    string Name,
    string Code,
    Guid FacultyId,
    string FacultyName,
    Guid InstitutionId,
    string InstitutionName,
    IReadOnlyCollection<ProgrammeResponse> Programmes);

public sealed record AdminPlatformSettingsRequest(
    [Required, MaxLength(200)] string PlatformName,
    [EmailAddress, MaxLength(320)] string? SupportEmail,
    [Phone, MaxLength(50)] string? SupportPhone,
    bool MaintenanceMode,
    [Range(typeof(decimal), "0", "9999999999")] decimal ListingFee,
    [Range(1, 365)] int InvoiceDays,
    bool HoldEventsUntilPayment);

public sealed record AdminPlatformSettingsResponse(
    string PlatformName,
    string? SupportEmail,
    string? SupportPhone,
    bool MaintenanceMode,
    decimal ListingFee,
    int InvoiceDays,
    bool HoldEventsUntilPayment,
    DateTimeOffset UpdatedAt);

public sealed record CreateSchoolAdminRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(12)] string Password,
    [Required, MaxLength(100)] string FirstName,
    [Required, MaxLength(100)] string LastName,
    Guid InstitutionId);

public sealed record CreateInstitutionAdministratorRequest(
    [Required, EmailAddress] string Email,
    [Required, MinLength(12)] string Password,
    [Required, MaxLength(100)] string FirstName,
    [Required, MaxLength(100)] string LastName,
    Guid InstitutionId,
    UserRole Role);

public sealed record InstitutionAdministratorResponse(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    UserRole Role,
    Guid InstitutionId,
    bool IsActive);

public sealed record UpdateInstitutionAdministratorRequest(UserRole Role, bool IsActive);

public sealed record UpdateAdminUserRequest(UserRole Role, bool IsActive);
public sealed record AdminUserResponse(Guid Id, string Email, UserRole Role, Guid InstitutionId, bool IsActive);

public sealed record AdminUserListItemResponse(
    Guid Id,
    string Email,
    string FirstName,
    string LastName,
    UserRole Role,
    Guid InstitutionId,
    string InstitutionName,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastActiveAt);

public sealed record AdminUserPageResponse(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyCollection<AdminUserListItemResponse> Items);

public sealed record AdminAuditLogResponse(
    Guid Id,
    Guid ActorId,
    string? ActorName,
    string? ActorEmail,
    UserRole? ActorRole,
    string Action,
    string ActionTag,
    Guid TargetId,
    string TargetType,
    Guid InstitutionId,
    string? InstitutionName,
    string Details,
    DateTimeOffset CreatedAt);

public sealed record AdminAuditLogPageResponse(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyCollection<AdminAuditLogResponse> Items);

public sealed record AdminContentAudienceResponse(
    Guid InstitutionId,
    Guid? FacultyId,
    Guid? DepartmentId,
    Guid? ProgrammeId,
    Guid? AcademicLevelId,
    CampusUpdate.Domain.Content.TargetAudience TargetAudience);

public sealed record AdminModerationContentResponse(
    Guid Id,
    string Title,
    string Body,
    string? Summary,
    string? Category,
    string? ImageUrl,
    CampusUpdate.Domain.Content.ContentType Type,
    CampusUpdate.Domain.Content.ContentStatus Status,
    CampusUpdate.Domain.Content.UrgencyLevel Urgency,
    CampusUpdate.Domain.Content.SourceType SourceType,
    string SourceName,
    DateTimeOffset CreatedAt,
    DateTimeOffset? EventStartsAt,
    DateTimeOffset? EventEndsAt,
    string? EventLocation,
    string? RegistrationUrl,
    string? SponsorName,
    string? TargetUrl,
    Guid AuthorId,
    string AuthorEmail,
    string AuthorName,
    IReadOnlyCollection<AdminContentAudienceResponse> Audiences,
    int UniqueReachCount = 0,
    int NotificationOpenCount = 0,
    int Views = 0,
    int ReachCount = 0);

public sealed record AdminModerationContentPageResponse(
    int Page,
    int PageSize,
    int TotalCount,
    IReadOnlyCollection<AdminModerationContentResponse> Items);
