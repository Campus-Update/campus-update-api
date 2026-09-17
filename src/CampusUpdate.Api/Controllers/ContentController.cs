using System.Security.Claims;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Content;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Api.Controllers;

[ApiController]
[Route("api/v1/content")]
public sealed class ContentController(CampusUpdateDbContext db) : ControllerBase
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
                (user.FeedPreference.AllCampusFeed || ((a.FacultyId == null || a.FacultyId == user.FacultyId) &&
                (a.DepartmentId == null || a.DepartmentId == user.DepartmentId) &&
                (a.ProgrammeId == null || a.ProgrammeId == user.ProgrammeId) &&
                (a.AcademicLevelId == null || a.AcademicLevelId == user.AcademicLevelId)))));
        if (type.HasValue)
            query = query.Where(x => x.Type == type.Value);
        if (urgency.HasValue)
            query = query.Where(x => x.Urgency == urgency.Value);

        var items = await query
            .OrderByDescending(x => x.Urgency)
            .ThenByDescending(x => x.PublishedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(x => new ContentResponse(x.Id, x.Title, x.Body, x.Type, x.Status, x.Urgency, x.SourceType, x.SourceName, x.PublishedAt))
            .ToListAsync(cancellationToken);
        return Ok(items);
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
                InstitutionId = a.InstitutionId,
                FacultyId = a.FacultyId,
                DepartmentId = a.DepartmentId,
                ProgrammeId = a.ProgrammeId,
                AcademicLevelId = a.AcademicLevelId
            }).ToList()
        };
        db.ContentItems.Add(item);
        await db.SaveChangesAsync(cancellationToken);
        var response = new ContentResponse(item.Id, item.Title, item.Body, item.Type, item.Status, item.Urgency, item.SourceType, item.SourceName, item.PublishedAt);
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
                    (user.FeedPreference.AllCampusFeed || ((a.FacultyId == null || a.FacultyId == user.FacultyId) &&
                    (a.DepartmentId == null || a.DepartmentId == user.DepartmentId) &&
                    (a.ProgrammeId == null || a.ProgrammeId == user.ProgrammeId) &&
                    (a.AcademicLevelId == null || a.AcademicLevelId == user.AcademicLevelId)))))))
            .Select(x => new ContentResponse(x.Id, x.Title, x.Body, x.Type, x.Status, x.Urgency, x.SourceType, x.SourceName, x.PublishedAt))
            .SingleOrDefaultAsync(cancellationToken);
        return item is null ? NotFound() : Ok(item);
    }

    private async Task<bool> AudienceIsValid(AudienceRequest audience, CancellationToken cancellationToken)
    {
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
}
