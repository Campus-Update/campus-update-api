using System.ComponentModel.DataAnnotations;
using CampusUpdate.Domain.Content;

namespace CampusUpdate.Api.Contracts;

public sealed record AudienceRequest(
    Guid InstitutionId,
    Guid? FacultyId,
    Guid? DepartmentId,
    Guid? ProgrammeId,
    Guid? AcademicLevelId);

public sealed record CreateContentRequest(
    [property: Required, MaxLength(250)] string Title,
    [property: Required] string Body,
    string? Summary,
    ContentType Type,
    UrgencyLevel Urgency,
    SourceType SourceType,
    [property: Required, MaxLength(200)] string SourceName,
    DateTimeOffset? EventStartsAt,
    DateTimeOffset? EventEndsAt,
    string? EventLocation,
    string? RegistrationUrl,
    string? SponsorName,
    string? TargetUrl,
    IReadOnlyCollection<AudienceRequest> Audiences);

public sealed record ContentResponse(
    Guid Id,
    string Title,
    string Body,
    ContentType Type,
    ContentStatus Status,
    UrgencyLevel Urgency,
    SourceType SourceType,
    string SourceName,
    DateTimeOffset? PublishedAt);
