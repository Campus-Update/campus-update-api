using System.Security.Claims;
using System.Text.Json;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Content;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using CampusUpdate.Api.Notifications;
using CampusUpdate.Infrastructure.Media;

namespace CampusUpdate.Api.Controllers;

[ApiController]
[Route("api/v1/content")]
public sealed class ContentController(CampusUpdateDbContext db, NotificationDispatcher notifications, IMediaStorage mediaStorage) : ControllerBase
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
        [FromQuery] ContentStatus? status = null,
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
        if (user.Role.IsInstitutionAdministrator() || user.Role == UserRole.SuperAdmin)
        {
            if (facultyId.HasValue || departmentId.HasValue || academicLevelId.HasValue)
                return BadRequest(new ProblemDetails { Title = "Academic profile filters are only available on the personalized feed." });

            var managedQuery = db.ContentItems.AsNoTracking().AsQueryable();
            if (user.Role == UserRole.SchoolAdmin)
                managedQuery = managedQuery.Where(x => x.Audiences.Any(a => a.InstitutionId == user.InstitutionId));
            else if (user.Role != UserRole.SuperAdmin)
                managedQuery = managedQuery.Where(x => x.Audiences.Any(a => a.InstitutionId == user.InstitutionId));
            if (user.Role == UserRole.NewsAdmin)
                managedQuery = managedQuery.Where(x => x.Type == ContentType.News || x.Type == ContentType.Announcement);
            else if (user.Role == UserRole.CalendarAdmin)
                managedQuery = managedQuery.Where(x => x.Type == ContentType.Event);
            if (status.HasValue)
                managedQuery = managedQuery.Where(x => x.Status == status.Value);
            if (type.HasValue)
                managedQuery = managedQuery.Where(x => x.Type == type.Value);
            if (!string.IsNullOrWhiteSpace(category))
                managedQuery = managedQuery.Where(x => x.Category == category.Trim());
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                managedQuery = managedQuery.Where(x => x.Title.Contains(term) || x.Body.Contains(term) ||
                    (x.Summary != null && x.Summary.Contains(term)));
            }

            var managedItems = await managedQuery
                .OrderByDescending(x => x.UpdatedAt)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(x => new ContentResponse(x.Id, x.Title, x.Body, x.Type, x.Status, x.Urgency, x.SourceType,
                    x.SourceName, x.PublishedAt, x.Category,
                    db.ContentViews.Count(v => v.ContentItemId == x.Id),
                    db.UserNotifications.Count(n => n.ContentItemId == x.Id && n.OpenedAt != null),
                    db.ContentViews.Count(v => v.ContentItemId == x.Id),
                    x.EventStartsAt, x.EventEndsAt, x.EventLocation, x.RegistrationUrl, x.SponsorName, x.TargetUrl,
                    x.CreatedAt, x.UpdatedAt, x.CoverImageUrl, x.IsEmergencyAlert))
                .ToListAsync(cancellationToken);
            return Ok(await AddSignedImageUrlsAsync(managedItems, cancellationToken));
        }

        if (status.HasValue && status != ContentStatus.Published)
            return Forbid();
        if ((facultyId.HasValue && facultyId != user.FacultyId) ||
            (departmentId.HasValue && departmentId != user.DepartmentId) ||
            (academicLevelId.HasValue && academicLevelId != user.AcademicLevelId))
            return Forbid();

        var query = db.ContentItems.AsNoTracking().Where(x =>
            x.Status == ContentStatus.Published &&
            (x.IsEmergencyAlert ||
             (x.Type == ContentType.News && user.FeedPreference.NewsEnabled) ||
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
            .Select(x => new ContentResponse(x.Id, x.Title, x.Body, x.Type, x.Status, x.Urgency, x.SourceType,
                x.SourceName, x.PublishedAt, x.Category,
                db.ContentViews.Count(v => v.ContentItemId == x.Id),
                db.UserNotifications.Count(n => n.ContentItemId == x.Id && n.OpenedAt != null),
                db.ContentViews.Count(v => v.ContentItemId == x.Id),
                x.EventStartsAt, x.EventEndsAt, x.EventLocation, x.RegistrationUrl, x.SponsorName, x.TargetUrl,
                x.CreatedAt, x.UpdatedAt, x.CoverImageUrl, x.IsEmergencyAlert))
            .ToListAsync(cancellationToken);
        db.UserActivities.Add(new CampusUpdate.Domain.Notifications.UserActivity { UserId = user.Id, InstitutionId = user.InstitutionId, ActivityType = "feed_fetch", OccurredAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(cancellationToken);
        return Ok(await AddSignedImageUrlsAsync(items, cancellationToken));
    }

    [Authorize(Roles = "LeadSchoolAdmin,SuperAdmin")]
    [HttpGet("pending-approval")]
    public async Task<ActionResult<IReadOnlyCollection<ContentResponse>>> GetPendingApproval(
        [FromQuery] Guid? institutionId,
        [FromQuery] ContentType? type,
        CancellationToken cancellationToken)
    {
        var user = await CurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        if (user.Role == UserRole.LeadSchoolAdmin)
        {
            if (institutionId.HasValue && institutionId != user.InstitutionId) return Forbid();
            institutionId = user.InstitutionId;
        }

        var query = db.ContentItems.AsNoTracking()
            .Where(x => x.Status == ContentStatus.PendingApproval);
        if (institutionId.HasValue)
            query = query.Where(x => x.Audiences.Any(a => a.InstitutionId == institutionId.Value));
        if (type.HasValue)
            query = query.Where(x => x.Type == type.Value);

        var items = await query.OrderByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Select(x => new ContentResponse(
                x.Id, x.Title, x.Body, x.Type, x.Status, x.Urgency, x.SourceType, x.SourceName,
                x.PublishedAt, x.Category,
                db.ContentViews.Count(v => v.ContentItemId == x.Id),
                db.UserNotifications.Count(n => n.ContentItemId == x.Id && n.OpenedAt != null),
                db.ContentViews.Count(v => v.ContentItemId == x.Id),
                x.EventStartsAt, x.EventEndsAt, x.EventLocation, x.RegistrationUrl, x.SponsorName,
                x.TargetUrl, x.CreatedAt, x.UpdatedAt, x.CoverImageUrl, x.IsEmergencyAlert))
            .ToListAsync(cancellationToken);
        return Ok(await AddSignedImageUrlsAsync(items, cancellationToken));
    }

    [Authorize(Roles = "SchoolAdmin,LeadSchoolAdmin,NewsAdmin,CalendarAdmin,SuperAdmin")]
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<ContentResponse>> Update(Guid id, UpdateContentRequest request, CancellationToken cancellationToken)
    {
        var user = await CurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        var item = await db.ContentItems.Include(x => x.Audiences).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (item is null) return NotFound();
        if (!user.Role.CanManageContent(item.Type)) return Forbid();
        if (user.Role != UserRole.SuperAdmin && item.Audiences.Any(a => a.InstitutionId != user.InstitutionId)) return Forbid();
        if (item.Status is ContentStatus.Published or ContentStatus.Archived) return Conflict(new ProblemDetails { Title = "Published content must be archived before editing." });
        var previousStatus = item.Status;
        if (request.Audiences.Count == 0 || (user.Role != UserRole.SuperAdmin && request.Audiences.Any(a => a.InstitutionId != user.InstitutionId))) return Forbid();
        foreach (var audience in request.Audiences)
            if (!await AudienceIsValid(audience, cancellationToken)) return BadRequest(new ProblemDetails { Title = "The selected audience hierarchy is invalid." });
        if (request.EventEndsAt < request.EventStartsAt) return BadRequest(new ProblemDetails { Title = "EventEndsAt cannot be before EventStartsAt." });
        item.Title = request.Title.Trim(); item.Body = request.Body.Trim(); item.Summary = request.Summary?.Trim(); item.Category = request.Category?.Trim();
        item.Urgency = request.Urgency; item.SourceName = request.SourceName.Trim(); item.EventStartsAt = request.EventStartsAt;
        item.EventEndsAt = request.EventEndsAt; item.EventLocation = request.EventLocation?.Trim(); item.RegistrationUrl = request.RegistrationUrl;
        item.SponsorName = request.SponsorName?.Trim(); item.TargetUrl = request.TargetUrl;
        ReplaceAudiences(item, request.Audiences);
        if (item.Status == ContentStatus.Rejected)
            item.Status = ContentStatus.Draft;
        Audit("ContentUpdated", item.Id, request.Audiences.First().InstitutionId,
            new { targetType = "Content", actionTag = ContentActionTag(item.Type), item.Title, previousStatus, currentStatus = item.Status, audienceInstitutionIds = request.Audiences.Select(a => a.InstitutionId).Distinct().ToArray() });
        await db.SaveChangesAsync(cancellationToken);
        return Ok(await ToResponseAsync(item, cancellationToken));
    }

    [Authorize(Roles = "SchoolAdmin,LeadSchoolAdmin,NewsAdmin,CalendarAdmin,SuperAdmin")]
    [HttpPatch("{id:guid}/status")]
    public async Task<ActionResult<ContentResponse>> ChangeStatus(Guid id, ChangeContentStatusRequest request, CancellationToken cancellationToken)
    {
        var user = await CurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        var item = await db.ContentItems.Include(x => x.Audiences).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (item is null) return NotFound();
        if (!user.Role.CanManageContent(item.Type)) return Forbid();
        if (user.Role != UserRole.SuperAdmin && item.Audiences.Any(a => a.InstitutionId != user.InstitutionId)) return Forbid();
        var allowed = request.Status switch
        {
            ContentStatus.PendingApproval => item.Status == ContentStatus.Draft,
            ContentStatus.Published => (user.Role is UserRole.SuperAdmin or UserRole.LeadSchoolAdmin) && item.Status == ContentStatus.PendingApproval,
            ContentStatus.Rejected => (user.Role is UserRole.SuperAdmin or UserRole.LeadSchoolAdmin) && item.Status == ContentStatus.PendingApproval,
            ContentStatus.Archived => item.Status == ContentStatus.Published,
            _ => false
        };
        if (!allowed) return BadRequest(new ProblemDetails { Title = "Invalid content status transition." });
        var previousStatus = item.Status;
        item.Status = request.Status; item.PublishedAt = request.Status == ContentStatus.Published ? DateTimeOffset.UtcNow : item.PublishedAt;
        Audit("ContentStatusChanged", item.Id, item.Audiences.First().InstitutionId,
            new { targetType = "Content", actionTag = ContentActionTag(item.Type), item.Title, previousStatus, currentStatus = request.Status });
        await db.SaveChangesAsync(cancellationToken);
        if (request.Status == ContentStatus.Published)
            await notifications.QueuePublishedContentAsync(item, cancellationToken);
        return Ok(await ToResponseAsync(item, cancellationToken));
    }

    [Authorize(Roles = "SchoolAdmin,LeadSchoolAdmin,NewsAdmin,CalendarAdmin,SuperAdmin")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var user = await CurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        var item = await db.ContentItems.Include(x => x.Audiences).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (item is null) return NotFound();
        if (!user.Role.CanManageContent(item.Type)) return Forbid();
        if (user.Role != UserRole.SuperAdmin && item.Audiences.Any(a => a.InstitutionId != user.InstitutionId)) return Forbid();
        var institutionId = item.Audiences.First().InstitutionId;
        item.Status = ContentStatus.Archived;
        Audit("ContentArchived", item.Id, institutionId, new { targetType = "Content", actionTag = ContentActionTag(item.Type), item.Title });
        await db.SaveChangesAsync(cancellationToken); return NoContent();
    }

    [Authorize(Roles = "SchoolAdmin,LeadSchoolAdmin,NewsAdmin,CalendarAdmin,SuperAdmin")]
    [HttpPost]
    public async Task<ActionResult<ContentResponse>> Create(CreateContentRequest request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var authorId))
            return Unauthorized();
        var author = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == authorId, cancellationToken);
        if (author is null)
            return Unauthorized();
        if (!author.Role.CanManageContent(request.Type))
            return Forbid();
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
        Audit("ContentCreated", item.Id, request.Audiences.First().InstitutionId,
            new { targetType = "Content", actionTag = ContentActionTag(item.Type), item.Title, audienceInstitutionIds = request.Audiences.Select(a => a.InstitutionId).Distinct().ToArray() });
        await db.SaveChangesAsync(cancellationToken);
        var response = await ToResponseAsync(item, cancellationToken);
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
        var isInstitutionAdministrator = user.Role.IsInstitutionAdministrator();
        var managesAllContent = user.Role is UserRole.SchoolAdmin or UserRole.LeadSchoolAdmin or UserRole.SuperAdmin;
        var manageableContentTypes = user.Role switch
        {
            UserRole.NewsAdmin => new[] { ContentType.News, ContentType.Announcement },
            UserRole.CalendarAdmin => new[] { ContentType.Event },
            _ => Array.Empty<ContentType>()
        };

        var item = await db.ContentItems.AsNoTracking()
            .Where(x => x.Id == id && (user.Role == UserRole.SuperAdmin ||
                (isInstitutionAdministrator && (managesAllContent || manageableContentTypes.Contains(x.Type)) &&
                 x.Audiences.Any(a => a.InstitutionId == user.InstitutionId)) ||
                (x.Status == ContentStatus.Published && x.Audiences.Any(a =>
                    a.InstitutionId == user.InstitutionId &&
                (a.TargetAudience == TargetAudience.All || (a.TargetAudience == TargetAudience.Students && user.Role == UserRole.Student) || (a.TargetAudience == TargetAudience.Staff && user.Role == UserRole.Staff)) &&
                    (x.IsEmergencyAlert || user.FeedPreference.AllCampusFeed || ((a.FacultyId == null || a.FacultyId == user.FacultyId) &&
                    (a.DepartmentId == null || a.DepartmentId == user.DepartmentId) &&
                    (a.ProgrammeId == null || a.ProgrammeId == user.ProgrammeId) &&
                    (a.AcademicLevelId == null || a.AcademicLevelId == user.AcademicLevelId)))))))
            .Select(x => new ContentResponse(x.Id, x.Title, x.Body, x.Type, x.Status, x.Urgency, x.SourceType,
                x.SourceName, x.PublishedAt, x.Category,
                db.ContentViews.Count(v => v.ContentItemId == x.Id),
                db.UserNotifications.Count(n => n.ContentItemId == x.Id && n.OpenedAt != null),
                db.ContentViews.Count(v => v.ContentItemId == x.Id),
                x.EventStartsAt, x.EventEndsAt, x.EventLocation, x.RegistrationUrl, x.SponsorName, x.TargetUrl,
                x.CreatedAt, x.UpdatedAt, x.CoverImageUrl, x.IsEmergencyAlert))
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
        return Ok(item with { ImageUrl = await SignImageUrlAsync(item.ImageUrl, cancellationToken) });
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
    private void ReplaceAudiences(ContentItem item, IReadOnlyCollection<AudienceRequest> requested)
    {
        var unmatched = item.Audiences.ToList();
        foreach (var audience in requested)
        {
            var existing = unmatched.FirstOrDefault(x =>
                x.InstitutionId == audience.InstitutionId &&
                x.FacultyId == audience.FacultyId &&
                x.DepartmentId == audience.DepartmentId &&
                x.ProgrammeId == audience.ProgrammeId &&
                x.AcademicLevelId == audience.AcademicLevelId);
            if (existing is null)
            {
                item.Audiences.Add(ToAudience(audience));
                continue;
            }

            existing.TargetAudience = audience.TargetAudience;
            unmatched.Remove(existing);
        }

        foreach (var removed in unmatched)
        {
            item.Audiences.Remove(removed);
            db.ContentAudiences.Remove(removed);
        }
    }
    private static string ContentActionTag(ContentType type) => type switch
    {
        ContentType.Advertisement => "Advertisement",
        ContentType.Event => "External event",
        _ => "Access"
    };
    private async Task<ContentResponse> ToResponseAsync(ContentItem x, CancellationToken cancellationToken)
    {
        var uniqueReach = await db.ContentViews.CountAsync(v => v.ContentItemId == x.Id, cancellationToken);
        var notificationOpens = await db.UserNotifications.CountAsync(n => n.ContentItemId == x.Id && n.OpenedAt != null, cancellationToken);
        var response = new ContentResponse(x.Id, x.Title, x.Body, x.Type, x.Status, x.Urgency, x.SourceType,
            x.SourceName, x.PublishedAt, x.Category, uniqueReach, notificationOpens, uniqueReach,
            x.EventStartsAt, x.EventEndsAt, x.EventLocation, x.RegistrationUrl, x.SponsorName, x.TargetUrl,
            x.CreatedAt, x.UpdatedAt, x.CoverImageUrl, x.IsEmergencyAlert);
        return response with { ImageUrl = await SignImageUrlAsync(response.ImageUrl, cancellationToken) };
    }

    private async Task<IReadOnlyCollection<ContentResponse>> AddSignedImageUrlsAsync(
        IReadOnlyCollection<ContentResponse> items,
        CancellationToken cancellationToken) =>
        await Task.WhenAll(items.Select(async item =>
            item with { ImageUrl = await SignImageUrlAsync(item.ImageUrl, cancellationToken) }));

    private Task<string?> SignImageUrlAsync(string? imageUrl, CancellationToken cancellationToken) =>
        !string.IsNullOrWhiteSpace(imageUrl) && imageUrl.StartsWith("content/", StringComparison.Ordinal)
            ? SignStoredImageAsync(imageUrl, cancellationToken)
            : Task.FromResult(imageUrl);

    private async Task<string?> SignStoredImageAsync(string objectKey, CancellationToken cancellationToken) =>
        await mediaStorage.CreateDownloadUrlAsync(objectKey, TimeSpan.FromMinutes(15), cancellationToken);

    private void Audit(string action, Guid targetId, Guid institutionId, object details) => db.AuditLogs.Add(new AuditLog
    {
        ActorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
        Action = action,
        TargetId = targetId,
        InstitutionId = institutionId,
        Details = JsonSerializer.Serialize(details)
    });
}
