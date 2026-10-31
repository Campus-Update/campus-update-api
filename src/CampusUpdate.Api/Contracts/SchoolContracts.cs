using System.ComponentModel.DataAnnotations;

namespace CampusUpdate.Api.Contracts;

public sealed record AcademicLevelResponse(Guid Id, string Name, int SortOrder);
public sealed record ProgrammeResponse(Guid Id, string Name, string Code, IReadOnlyCollection<AcademicLevelResponse> Levels);
public sealed record DepartmentResponse(Guid Id, string Name, string Code, IReadOnlyCollection<ProgrammeResponse> Programmes);
public sealed record FacultyResponse(Guid Id, string Name, string Code, IReadOnlyCollection<DepartmentResponse> Departments);
public sealed record InstitutionResponse(Guid Id, string Name, string Slug, string? Acronym, string? LogoUrl, IReadOnlyCollection<FacultyResponse> Faculties);
public sealed record InstitutionAdminResponse(Guid Id, string Name, string Slug, string? Acronym, string? LogoUrl, bool IsActive, IReadOnlyCollection<FacultyResponse> Faculties);
public sealed record CreateAcademicCalendarRequest(
    Guid InstitutionId,
    [Required, MaxLength(250)] string Title,
    [Required, MaxLength(50)] string AcademicSession,
    [MaxLength(2048)] string? ImageUrl,
    DateTimeOffset PublishedAt,
    bool IsOfficial = true,
    string Status = "Draft");
public sealed record AcademicCalendarResponse(
    Guid Id,
    Guid InstitutionId,
    string Title,
    string AcademicSession,
    string ImageUrl,
    DateTimeOffset PublishedAt,
    string Status = "Draft",
    bool IsOfficial = true);
public sealed record RejectAcademicCalendarRequest(
    [Required, MaxLength(1000)] string Reason);

public sealed class CalendarUploadRequest
{
    public Guid? CalendarId { get; set; }
    public Guid InstitutionId { get; set; }
    [Required, MaxLength(250)] public string Title { get; set; } = "";
    [Required, MaxLength(50)] public string AcademicSession { get; set; } = "";
    public string Status { get; set; } = "Draft";
    public DateTimeOffset? PublishedAt { get; set; }
    [Required] public Microsoft.AspNetCore.Http.IFormFile File { get; set; } = null!;
}
