using System.Security.Claims;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Content;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CampusUpdate.Api.Notifications;

namespace CampusUpdate.Api.Controllers;

[ApiController]
[Route("api/v1/content")]
public sealed class ContentController(CampusUpdateDbContext db, NotificationDispatcher notifications) : ControllerBase
{
    [Authorize]
    [HttpGet]
    [HttpGet("/api/v1/feed")]
    public async Task<ActionResult<IReadOnlyCollection<ContentResponse>>> GetFeed(
        [FromQuery] ContentType? type,
        [FromQuery] UrgencyLevel? urgency,
        [FromQuery] Guid? facultyId,
        [FromQuery] Guid? departmentId,
        [FromQuery] Guid? academicLevelId,
        [FromQuery] string? search,
        [FromQuery] string? category = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();

        var user = await db.Users.AsNoTracking()
            .Include(x => x.FeedPreference)
            .SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
        if (user is null)
            return Unauthorized();
        if ((facultyId.HasValue && facultyId != user.FacultyId) ||
            (departmentId.HasValue && departmentId != user.DepartmentId) ||
            (academicLevelId.HasValue && academicLevelId != user.AcademicLevelId))
            return Forbid();

        var query = db.ContentItems.AsNoTracking().Where(x =>
            x.Status == ContentStatus.Published &&
            ((x.Type == ContentType.News && user.FeedPreference.NewsEnabled) ||
             (x.Type == ContentType.Announcement && user.FeedPreference.AnnouncementsEnabled) ||
             (x.Type == ContentType.Event && user.FeedPreference.EventsEnabled) ||
             (x.Type == ContentType.Advertisement && user.FeedPreference.AdvertisementsEnabled)) &&
            x.Audiences.Any(a =>
                a.InstitutionId == user.InstitutionId &&
                (a.TargetAudience == TargetAudience.All || (a.TargetAudience == TargetAudience.Students && user.Role == UserRole.Student) || (a.TargetAudience == TargetAudience.Staff && user.Role == UserRole.Staff)) &&
                (user.FeedPreference.AllCampusFeed || ((a.FacultyId == null || a.FacultyId == user.FacultyId) &&
                (a.DepartmentId == null || a.DepartmentId == user.DepartmentId) &&
                (a.ProgrammeId == null || a.ProgrammeId == user.ProgrammeId) &&
                (a.AcademicLevelId == null || a.AcademicLevelId == user.AcademicLevelId)))));
        if (!string.IsNullOrWhiteSpace(category)) query = query.Where(x => x.Category == category.Trim());
        if (type.HasValue)
            query = query.Where(x => x.Type == type.Value);
        if (urgency.HasValue)
            query = query.Where(x => x.Urgency == urgency.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Title.Contains(term) || x.Body.Contains(term) ||
                (x.Summary != null && x.Summary.Contains(term)));
        }

        var items = await query
            .OrderByDescending(x => x.Urgency)
            .ThenByDescending(x => x.PublishedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ContentResponse(x.Id, x.Title, x.Body, x.Type, x.Status, x.Urgency, x.SourceType, x.SourceName, x.PublishedAt, x.Category))
            .ToListAsync(cancellationToken);
        db.UserActivities.Add(new CampusUpdate.Domain.Notifications.UserActivity { UserId = user.Id, InstitutionId = user.InstitutionId, ActivityType = "feed_fetch", OccurredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        return Ok(items);
    }

    [Authorize(Roles = "SchoolAdmin,SuperAdmin")]
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ContentResponse>> Update(Guid id, UpdateContentRequest request, CancellationToken cancellationToken)
    {
        var user = await CurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        var item = await db.ContentItems.Include(x => x.Audiences).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (item is null) return NotFound();
        if (user.Role != UserRole.SuperAdmin && item.Audiences.Any(a => a.InstitutionId != user.InstitutionId)) return Forbid();
        if (item.Status is ContentStatus.Published or ContentStatus.Archived) return Conflict(new ProblemDetails { Title = "Published content must be archived before editing." });
        if (request.Audiences.Count == 0 || (user.Role != UserRole.SuperAdmin && request.Audiences.Any(a => a.InstitutionId != user.InstitutionId))) return Forbid();
        foreach (var audience in request.Audiences)
            if (!await AudienceIsValid(audience, cancellationToken)) return BadRequest(new ProblemDetails { Title = "The selected audience hierarchy is invalid." });
        if (request.EventEndsAt < request.EventStartsAt) return BadRequest(new ProblemDetails { Title = "EventEndsAt cannot be before EventStartsAt." });
        item.Title = request.Title.Trim(); item.Body = request.Body.Trim(); item.Summary = request.Summary?.Trim(); item.Category = request.Category?.Trim();
        item.Urgency = request.Urgency; item.SourceName = request.SourceName.Trim(); item.EventStartsAt = request.EventStartsAt;
        item.EventEndsAt = request.EventEndsAt; item.EventLocation = request.EventLocation?.Trim(); item.RegistrationUrl = request.RegistrationUrl;
        item.SponsorName = request.SponsorName?.Trim(); item.TargetUrl = request.TargetUrl;
        db.ContentAudiences.RemoveRange(item.Audiences); item.Audiences = request.Audiences.Select(ToAudience).ToList();
        await db.SaveChangesAsync(cancellationToken);
        return Ok(ToResponse(item));
    }

    [Authorize(Roles = "SchoolAdmin,SuperAdmin")]
    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult<ContentResponse>> ChangeStatus(Guid id, ChangeContentStatusRequest request, CancellationToken cancellationToken)
    {
        var user = await CurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        var item = await db.ContentItems.Include(x => x.Audiences).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (item is null) return NotFound();
        if (user.Role != UserRole.SuperAdmin && item.Audiences.Any(a => a.InstitutionId != user.InstitutionId)) return Forbid();
        var allowed = request.Status switch
        {
            ContentStatus.PendingApproval => item.Status == ContentStatus.Draft,
            ContentStatus.Published => user.Role == UserRole.SuperAdmin && item.Status == ContentStatus.PendingApproval,
            ContentStatus.Rejected => user.Role == UserRole.SuperAdmin && item.Status == ContentStatus.PendingApproval,
            ContentStatus.Archived => item.Status == ContentStatus.Published,
            _ => false
        };
        if (!allowed) return BadRequest(new ProblemDetails { Title = "Invalid content status transition." });
        item.Status = request.Status; item.PublishedAt = request.Status == ContentStatus.Published ? DateTimeOffset.UtcNow : item.PublishedAt;
        await db.SaveChangesAsync(cancellationToken);
        if (request.Status == ContentStatus.Published)
            await notifications.QueuePublishedContentAsync(item, cancellationToken);
        return Ok(ToResponse(item));
    }

    [Authorize(Roles = "SchoolAdmin,SuperAdmin")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var user = await CurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        var item = await db.ContentItems.Include(x => x.Audiences).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (item is null) return NotFound();
        if (user.Role != UserRole.SuperAdmin && item.Audiences.Any(a => a.InstitutionId != user.InstitutionId)) return Forbid();
        item.Status = ContentStatus.Archived; await db.SaveChangesAsync(cancellationToken); return NoContent();
    }

    [Authorize(Roles = "SchoolAdmin,SuperAdmin")]
    [HttpPost]
    public async Task<ActionResult<ContentResponse>> Create(CreateContentRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var authorId))
            return Unauthorized();
        var author = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == authorId, cancellationToken);
        if (author is null)
            return Unauthorized();
        if (request.Audiences.Count == 0)
            return BadRequest(new ProblemDetails { Title = "At least one audience is required." });
        if (author.Role != UserRole.SuperAdmin && request.Audiences.Any(x => x.InstitutionId != author.InstitutionId))
            return Forbid();
        foreach (var audience in request.Audiences)
        {
            if (!await AudienceIsValid(audience, cancellationToken))
                return BadRequest(new ProblemDetails { Title = "The selected audience hierarchy is invalid." });
        }
        if (request.Type == ContentType.Event && request.EventStartsAt is null)
            return BadRequest(new ProblemDetails { Title = "EventStartsAt is required for events." });
        if (request.EventEndsAt < request.EventStartsAt)
            return BadRequest(new ProblemDetails { Title = "EventEndsAt cannot be before EventStartsAt." });
        if (request.Type == ContentType.Advertisement && string.IsNullOrWhiteSpace(request.TargetUrl))
            return BadRequest(new ProblemDetails { Title = "TargetUrl is required for advertisements." });

        var item = new ContentItem
        {
            Title = request.Title.Trim(),
            Body = request.Body.Trim(),
            Summary = request.Summary?.Trim(),
            Category = request.Category?.Trim(),
            Type = request.Type,
            Urgency = request.Urgency,
            SourceType = request.SourceType,
            SourceName = request.SourceName.Trim(),
            EventStartsAt = request.EventStartsAt,
            EventEndsAt = request.EventEndsAt,
            EventLocation = request.EventLocation?.Trim(),
            RegistrationUrl = request.RegistrationUrl,
            SponsorName = request.SponsorName?.Trim(),
            TargetUrl = request.TargetUrl,
            AuthorId = authorId,
            Audiences = request.Audiences.Select(a => new ContentAudience
            {
                TargetAudience = a.TargetAudience,
                InstitutionId = a.InstitutionId,
                FacultyId = a.FacultyId,
                DepartmentId = a.DepartmentId,
                ProgrammeId = a.ProgrammeId,
                AcademicLevelId = a.AcademicLevelId
            }).ToList()
        };
        db.ContentItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        var response = new ContentResponse(item.Id, item.Title, item.Body, item.Type, item.Status, item.Urgency, item.SourceType, item.SourceName, item.PublishedAt, item.Category);
        return CreatedAtAction(nameof(GetById), new { id = item.Id }, response);
    }

    [Authorize]
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ContentResponse>> GetById(Guid id, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        var user = await db.Users.AsNoTracking().Include(x => x.FeedPreference)
            .SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
        if (user is null)
            return Unauthorized();

        var item = await db.ContentItems.AsNoTracking()
            .Where(x => x.Id == id && (user.Role == UserRole.SuperAdmin ||
                (user.Role == UserRole.SchoolAdmin && x.Audiences.Any(a => a.InstitutionId == user.InstitutionId)) ||
                (x.Status == ContentStatus.Published && x.Audiences.Any(a =>
                    a.InstitutionId == user.InstitutionId &&
                (a.TargetAudience == TargetAudience.All || (a.TargetAudience == TargetAudience.Students && user.Role == UserRole.Student) || (a.TargetAudience == TargetAudience.Staff && user.Role == UserRole.Staff)) &&
                    (user.FeedPreference.AllCampusFeed || ((a.FacultyId == null || a.FacultyId == user.FacultyId) &&
                    (a.DepartmentId == null || a.DepartmentId == user.DepartmentId) &&
                    (a.ProgrammeId == null || a.ProgrammeId == user.ProgrammeId) &&
                    (a.AcademicLevelId == null || a.AcademicLevelId == user.AcademicLevelId)))))))
            .Select(x => new ContentResponse(x.Id, x.Title, x.Body, x.Type, x.Status, x.Urgency, x.SourceType, x.SourceName, x.PublishedAt, x.Category))
            .SingleOrDefaultAsync(cancellationToken);
        if (item is null) return NotFound();
        if (user.Role is UserRole.Student or UserRole.Staff)
        {
            var viewId = Guid.NewGuid();
            var viewedAt = DateTimeOffset.UtcNow;
            if (db.Database.IsNpgsql())
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"ContentViews\" (\"Id\", \"CreatedAt\", \"UpdatedAt\", \"UserId\", \"ContentItemId\", \"InstitutionId\", \"FirstViewedAt\") VALUES ({viewId}, {viewedAt}, {viewedAt}, {user.Id}, {item.Id}, {user.InstitutionId}, {viewedAt}) ON CONFLICT (\"UserId\", \"ContentItemId\") DO NOTHING", cancellationToken);
            }
            else if (!await db.ContentViews.AnyAsync(v => v.UserId == user.Id && v.ContentItemId == item.Id, cancellationToken))
            {
                db.ContentViews.Add(new CampusUpdate.Domain.Notifications.ContentView { UserId = user.Id, ContentItemId = item.Id, InstitutionId = user.InstitutionId, FirstViewedAt = viewedAt });
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        return Ok(item);
    }

    private async Task<bool> AudienceIsValid(AudienceRequest audience, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(audience.TargetAudience)) return false;
        if (!await db.Institutions.AnyAsync(x => x.Id == audience.InstitutionId && x.IsActive, cancellationToken))
            return false;
        if (audience.FacultyId is not null && !await db.Faculties.AnyAsync(
                x => x.Id == audience.FacultyId && x.InstitutionId == audience.InstitutionId, cancellationToken))
            return false;
        if (audience.DepartmentId is not null && (audience.FacultyId is null || !await db.Departments.AnyAsync(
                x => x.Id == audience.DepartmentId && x.FacultyId == audience.FacultyId, cancellationToken)))
            return false;
        if (audience.ProgrammeId is not null && (audience.DepartmentId is null || !await db.Programmes.AnyAsync(
                x => x.Id == audience.ProgrammeId && x.DepartmentId == audience.DepartmentId, cancellationToken)))
            return false;
        if (audience.AcademicLevelId is not null && (audience.ProgrammeId is null || !await db.AcademicLevels.AnyAsync(
                x => x.Id == audience.AcademicLevelId && x.ProgrammeId == audience.ProgrammeId, cancellationToken)))
            return false;
        return true;
    }

    private async Task<AppUser?> CurrentUser(CancellationToken cancellationToken) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.IsActive, cancellationToken) : null;

    private static ContentAudience ToAudience(AudienceRequest a) => new() { TargetAudience = a.TargetAudience, InstitutionId = a.InstitutionId, FacultyId = a.FacultyId, DepartmentId = a.DepartmentId, ProgrammeId = a.ProgrammeId, AcademicLevelId = a.AcademicLevelId };
    private static ContentResponse ToResponse(ContentItem x) => new(x.Id, x.Title, x.Body, x.Type, x.Status, x.Urgency, x.SourceType, x.SourceName, x.PublishedAt, x.Category);
}
