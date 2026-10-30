using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using CampusUpdate.Domain.Content;

namespace CampusUpdate.Api.Contracts;

public sealed record AudienceRequest(
    Guid InstitutionId,
    Guid? FacultyId,
    Guid? DepartmentId,
    Guid? ProgrammeId,
    Guid? AcademicLevelId,
    TargetAudience TargetAudience = TargetAudience.All);

public sealed record CreateContentRequest(
    [Required, MaxLength(250)] string Title,
    [Required] string Body,
    string? Summary,
    ContentType Type,
    UrgencyLevel Urgency,
    SourceType SourceType,
    [Required, MaxLength(200)] string SourceName,
    DateTimeOffset? EventStartsAt,
    DateTimeOffset? EventEndsAt,
    string? EventLocation,
    string? RegistrationUrl,
    string? SponsorName,
    string? TargetUrl,
    IReadOnlyCollection<AudienceRequest> Audiences,
    [MaxLength(100)] string? Category = null);

/// <summary>Multipart request for creating content and uploading its cover image in one call.</summary>
public sealed class CreateContentWithImageRequest
{
    [Required, MaxLength(250)] public string Title { get; set; } = "";
    [Required] public string Body { get; set; } = "";
    public string? Summary { get; set; }
    [Required] public ContentType? Type { get; set; }
    [Required] public UrgencyLevel? Urgency { get; set; }
    [Required] public SourceType? SourceType { get; set; }
    [Required, MaxLength(200)] public string SourceName { get; set; } = "";
    public DateTimeOffset? EventStartsAt { get; set; }
    public DateTimeOffset? EventEndsAt { get; set; }
    public string? EventLocation { get; set; }
    public string? RegistrationUrl { get; set; }
    public string? SponsorName { get; set; }
    public string? TargetUrl { get; set; }
    [Required] public string AudiencesJson { get; set; } = "";
    [MaxLength(100)] public string? Category { get; set; }
    [Required] public IFormFile Image { get; set; } = null!;
}

public sealed record UpdateContentRequest(
    [Required, MaxLength(250)] string Title,
    [Required] string Body,
    string? Summary,
    UrgencyLevel Urgency,
    [Required, MaxLength(200)] string SourceName,
    DateTimeOffset? EventStartsAt,
    DateTimeOffset? EventEndsAt,
    string? EventLocation,
    string? RegistrationUrl,
    string? SponsorName,
    string? TargetUrl,
    IReadOnlyCollection<AudienceRequest> Audiences,
    [MaxLength(100)] string? Category = null);

public sealed record ChangeContentStatusRequest(ContentStatus Status);

public sealed record CreateEmergencyAlertRequest(
    [Required, MaxLength(250)] string Title,
    [Required] string Body,
    string? Summary = null);

public sealed record ContentResponse(
    Guid Id,
    string Title,
    string Body,
    ContentType Type,
    ContentStatus Status,
    UrgencyLevel Urgency,
    SourceType SourceType,
    string SourceName,
    DateTimeOffset? PublishedAt,
    string? Category = null,
    int UniqueReachCount = 0,
    int NotificationOpenCount = 0,
    int Views = 0,
    DateTimeOffset? EventStartsAt = null,
    DateTimeOffset? EventEndsAt = null,
    string? EventLocation = null,
    string? RegistrationUrl = null,
    string? SponsorName = null,
    string? TargetUrl = null,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    string? ImageUrl = null,
    bool IsEmergencyAlert = false);
