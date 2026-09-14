using CampusUpdate.Domain.Common;
using CampusUpdate.Domain.Schools;
using CampusUpdate.Domain.Users;

namespace CampusUpdate.Domain.Content;

public enum ContentType { News, Announcement, Event, Advertisement }
public enum ContentStatus { Draft, PendingApproval, Published, Rejected, Archived }
public enum UrgencyLevel { Normal, Important, Urgent }
public enum SourceType { Official, External, Sponsored }

public sealed class ContentItem : Entity
{
    public required string Title { get; set; }
    public required string Body { get; set; }
    public string? Summary { get; set; }
    public string? CoverImageUrl { get; set; }
    public ContentType Type { get; set; }
    public ContentStatus Status { get; set; } = ContentStatus.Draft;
    public UrgencyLevel Urgency { get; set; } = UrgencyLevel.Normal;
    public SourceType SourceType { get; set; } = SourceType.Official;
    public required string SourceName { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? EventStartsAt { get; set; }
    public DateTimeOffset? EventEndsAt { get; set; }
    public string? EventLocation { get; set; }
    public string? RegistrationUrl { get; set; }
    public string? SponsorName { get; set; }
    public string? TargetUrl { get; set; }
    public Guid AuthorId { get; set; }
    public AppUser Author { get; set; } = null!;
    public ICollection<ContentAudience> Audiences { get; set; } = [];
    public ICollection<ContentAttachment> Attachments { get; set; } = [];
}

public sealed class ContentAudience : Entity
{
    public Guid ContentItemId { get; set; }
    public ContentItem ContentItem { get; set; } = null!;
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
}

public sealed class ContentAttachment : Entity
{
    public Guid ContentItemId { get; set; }
    public ContentItem ContentItem { get; set; } = null!;
    public required string FileName { get; set; }
    public required string Url { get; set; }
    public required string ContentType { get; set; }
    public long SizeBytes { get; set; }
}
