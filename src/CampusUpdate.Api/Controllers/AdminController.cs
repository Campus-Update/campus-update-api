using System.Security.Claims;
using System.Text.Json;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Schools;
using CampusUpdate.Domain.Users;
using CampusUpdate.Domain.Content;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Api.Controllers;

[ApiController]
[Authorize(Roles = nameof(UserRole.SuperAdmin))]
[Route("api/v1/admin")]
public sealed class AdminController(CampusUpdateDbContext db, IPasswordHasher<AppUser> hasher) : ControllerBase
{
    [HttpGet("schools")]
    public async Task<ActionResult<AdminSchoolPageResponse>> GetSchools(
        [FromQuery] string? search,
        [FromQuery] bool? isActive,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Institutions.AsNoTracking();
        if (isActive.HasValue) query = query.Where(x => x.IsActive == isActive.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Name.Contains(term) || x.Slug.Contains(term) ||
                (x.State != null && x.State.Contains(term)) || (x.Acronym != null && x.Acronym.Contains(term)));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var activeSince = DateTimeOffset.UtcNow.AddDays(-7);
        var items = await query.OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new AdminSchoolListItemResponse(
                x.Id, x.Name, x.Slug, x.State, x.Acronym, x.LogoUrl, x.IsActive,
                db.Users.Count(u => u.InstitutionId == x.Id && u.Role == UserRole.Student),
                db.Users.Count(u => u.InstitutionId == x.Id && u.Role == UserRole.Staff),
                db.Users.Count(u => u.InstitutionId == x.Id && u.Role == UserRole.SchoolAdmin),
                db.ContentItems.Count(c => c.Status == ContentStatus.Published &&
                    c.Audiences.Any(a => a.InstitutionId == x.Id)),
                db.UserActivities.Where(a => a.InstitutionId == x.Id && a.UserId != null && a.OccurredAt >= activeSince)
                    .Select(a => a.UserId).Distinct().Count(),
                db.ContentViews.Count(v => v.InstitutionId == x.Id)))
            .ToListAsync(cancellationToken);
        return Ok(new AdminSchoolPageResponse(page, pageSize, totalCount, items));
    }

    [HttpGet("schools/{id:guid}")]
    public async Task<ActionResult<AdminSchoolDetailResponse>> GetSchool(Guid id, CancellationToken cancellationToken)
    {
        var school = await db.Institutions.AsNoTracking()
            .Include(x => x.Faculties).ThenInclude(x => x.Departments).ThenInclude(x => x.Programmes).ThenInclude(x => x.Levels)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (school is null) return NotFound();

        var studentCount = await db.Users.CountAsync(x => x.InstitutionId == id && x.Role == UserRole.Student, cancellationToken);
        var staffCount = await db.Users.CountAsync(x => x.InstitutionId == id && x.Role == UserRole.Staff, cancellationToken);
        var adminCount = await db.Users.CountAsync(x => x.InstitutionId == id && x.Role == UserRole.SchoolAdmin, cancellationToken);
        var liveContentCount = await db.ContentItems.CountAsync(x => x.Status == ContentStatus.Published &&
            x.Audiences.Any(a => a.InstitutionId == id), cancellationToken);
        var faculties = school.Faculties.OrderBy(x => x.Name).Select(f => new FacultyResponse(
            f.Id, f.Name, f.Code, f.Departments.OrderBy(d => d.Name).Select(d => new DepartmentResponse(
                d.Id, d.Name, d.Code, d.Programmes.OrderBy(p => p.Name).Select(p => new ProgrammeResponse(
                    p.Id, p.Name, p.Code, p.Levels.OrderBy(l => l.SortOrder)
                        .Select(l => new AcademicLevelResponse(l.Id, l.Name, l.SortOrder)).ToArray())).ToArray())).ToArray())).ToArray();

        return Ok(new AdminSchoolDetailResponse(school.Id, school.Name, school.Slug, school.State, school.Acronym,
            school.LogoUrl, school.IsActive, studentCount, staffCount, adminCount, liveContentCount, faculties));
    }

    [HttpGet("users")]
    public async Task<ActionResult<AdminUserPageResponse>> GetUsers(
        [FromQuery] Guid? institutionId,
        [FromQuery] UserRole? role,
        [FromQuery] bool? isActive,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Users.AsNoTracking();
        if (institutionId.HasValue) query = query.Where(x => x.InstitutionId == institutionId.Value);
        if (role.HasValue) query = query.Where(x => x.Role == role.Value);
        if (isActive.HasValue) query = query.Where(x => x.IsActive == isActive.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Email.Contains(term) || x.FirstName.Contains(term) || x.LastName.Contains(term));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(x => x.Email).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.Id,
                x.Email,
                x.FirstName,
                x.LastName,
                x.Role,
                x.InstitutionId,
                InstitutionName = x.Institution.Name,
                x.IsActive,
                x.CreatedAt,
                LastActivityAt = db.UserActivities.Where(a => a.UserId == x.Id)
                    .Select(a => (DateTimeOffset?)a.OccurredAt).Max(),
                LastContentViewAt = db.ContentViews.Where(v => v.UserId == x.Id)
                    .Select(v => (DateTimeOffset?)v.FirstViewedAt).Max()
            }).ToListAsync(cancellationToken);
        var items = rows.Select(x => new AdminUserListItemResponse(x.Id, x.Email, x.FirstName, x.LastName,
            x.Role, x.InstitutionId, x.InstitutionName, x.IsActive, x.CreatedAt,
            Max(x.LastActivityAt, x.LastContentViewAt))).ToArray();
        return Ok(new AdminUserPageResponse(page, pageSize, totalCount, items));
    }

    [HttpGet("audit")]
    public async Task<ActionResult<AdminAuditLogPageResponse>> GetAuditLog(
        [FromQuery] Guid? institutionId,
        [FromQuery] Guid? actorId,
        [FromQuery] Guid? administratorId,
        [FromQuery] Guid? targetId,
        [FromQuery] string? action,
        [FromQuery] string? actionTag,
        [FromQuery] string? search,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? to,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.AuditLogs.AsNoTracking();
        if (institutionId.HasValue) query = query.Where(x => x.InstitutionId == institutionId.Value);
        if (administratorId.HasValue) actorId = administratorId;
        if (actorId.HasValue) query = query.Where(x => x.ActorId == actorId.Value);
        if (targetId.HasValue) query = query.Where(x => x.TargetId == targetId.Value);
        if (!string.IsNullOrWhiteSpace(action)) query = query.Where(x => x.Action.Contains(action.Trim()));
        if (!string.IsNullOrWhiteSpace(actionTag))
        {
            var tag = actionTag.Trim();
            query = tag.ToLowerInvariant() switch
            {
                "school" => query.Where(x => x.Action == "SchoolCreated"),
                "user" => query.Where(x => x.Action == "SchoolAdminCreated"),
                "access" => query.Where(x => x.Action == "UserAccessUpdated" || x.Details.Contains("\"actionTag\":\"Access\"")),
                "advertisement" => query.Where(x => x.Details.Contains("\"actionTag\":\"Advertisement\"")),
                "external event" => query.Where(x => x.Details.Contains("\"actionTag\":\"External event\"")),
                _ => null!
            };
            if (query is null) return BadRequest(new ProblemDetails { Title = "Unknown audit action tag." });
        }
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Action.Contains(term) || x.Details.Contains(term) ||
                db.Users.Any(u => u.Id == x.ActorId &&
                    (u.Email.Contains(term) || u.FirstName.Contains(term) || u.LastName.Contains(term))) ||
                db.Institutions.Any(i => i.Id == x.InstitutionId && i.Name.Contains(term)));
        }
        if (from.HasValue) query = query.Where(x => x.CreatedAt >= from.Value);
        if (to.HasValue) query = query.Where(x => x.CreatedAt < to.Value);

        var totalCount = await query.CountAsync(cancellationToken);
        var rows = await (from entry in query
                          join user in db.Users.AsNoTracking() on entry.ActorId equals user.Id into actors
                          from actor in actors.DefaultIfEmpty()
                          orderby entry.CreatedAt descending, entry.Id
                          select new
                          {
                              entry.Id,
                              entry.ActorId,
                              ActorName = actor == null ? null : actor.FirstName + " " + actor.LastName,
                              ActorEmail = actor == null ? null : actor.Email,
                              ActorRole = actor == null ? (UserRole?)null : actor.Role,
                              entry.Action,
                              entry.TargetId,
                              entry.InstitutionId,
                              InstitutionName = db.Institutions.Where(i => i.Id == entry.InstitutionId).Select(i => i.Name).FirstOrDefault(),
                              entry.Details,
                              entry.CreatedAt
                          })
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var items = rows.Select(x => new AdminAuditLogResponse(x.Id, x.ActorId, x.ActorName, x.ActorEmail,
            x.ActorRole, x.Action, GetActionTag(x.Action, x.Details), x.TargetId, GetTargetType(x.Action), x.InstitutionId,
            x.InstitutionName, x.Details, x.CreatedAt)).ToArray();
        return Ok(new AdminAuditLogPageResponse(page, pageSize, totalCount, items));
    }

    [HttpGet("content/pending")]
    public async Task<ActionResult<AdminModerationContentPageResponse>> GetPendingContent(
        [FromQuery] Guid? institutionId,
        [FromQuery] ContentType? type,
        [FromQuery] string? search,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.ContentItems.AsNoTracking()
            .Where(x => x.Status == ContentStatus.PendingApproval);
        if (institutionId.HasValue)
            query = query.Where(x => x.Audiences.Any(a => a.InstitutionId == institutionId.Value));
        if (type.HasValue)
            query = query.Where(x => x.Type == type.Value);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            query = query.Where(x => x.Title.Contains(term) || x.Body.Contains(term) ||
                (x.Summary != null && x.Summary.Contains(term)));
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .Include(x => x.Author)
            .Include(x => x.Audiences)
            .OrderByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        var contentIds = items.Select(x => x.Id).ToArray();
        var uniqueReach = await db.ContentViews.AsNoTracking().Where(x => contentIds.Contains(x.ContentItemId))
            .GroupBy(x => x.ContentItemId).Select(x => new { ContentId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.ContentId, x => x.Count, cancellationToken);
        var notificationOpens = await db.UserNotifications.AsNoTracking()
            .Where(x => x.ContentItemId.HasValue && contentIds.Contains(x.ContentItemId.Value) && x.OpenedAt != null)
            .GroupBy(x => x.ContentItemId!.Value).Select(x => new { ContentId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.ContentId, x => x.Count, cancellationToken);
        return Ok(new AdminModerationContentPageResponse(
            page,
            pageSize,
            totalCount,
            items.Select(x => new AdminModerationContentResponse(
                x.Id,
                x.Title,
                x.Body,
                x.Summary,
                x.Category,
                x.CoverImageUrl,
                x.Type,
                x.Status,
                x.Urgency,
                x.SourceType,
                x.SourceName,
                x.CreatedAt,
                x.EventStartsAt,
                x.EventEndsAt,
                x.EventLocation,
                x.RegistrationUrl,
                x.SponsorName,
                x.TargetUrl,
                x.AuthorId,
                x.Author.Email,
                $"{x.Author.FirstName} {x.Author.LastName}".Trim(),
                x.Audiences.Select(a => new AdminContentAudienceResponse(
                    a.InstitutionId,
                    a.FacultyId,
                    a.DepartmentId,
                    a.ProgrammeId,
                    a.AcademicLevelId,
                    a.TargetAudience)).ToArray(),
                uniqueReach.GetValueOrDefault(x.Id), notificationOpens.GetValueOrDefault(x.Id),
                uniqueReach.GetValueOrDefault(x.Id))).ToArray()));
    }

    [HttpPost("schools")]
    public async Task<ActionResult<AdminSchoolResponse>> CreateSchool(CreateSchoolRequest request, CancellationToken cancellationToken)
    {
        if (await db.Institutions.AnyAsync(x => x.Slug == request.Slug, cancellationToken))
            return Conflict(new ProblemDetails { Title = "A school with this slug already exists." });
        var school = new Institution
        {
            Name = request.Name.Trim(),
            Slug = request.Slug,
            State = request.State.Trim(),
            IsActive = request.IsActive
        };
        db.Institutions.Add(school);
        Audit("SchoolCreated", school.Id, school.Id, new { school.Name, school.Slug, school.State, school.IsActive });
        await db.SaveChangesAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created,
            new AdminSchoolResponse(school.Id, school.Name, school.Slug, school.State, school.IsActive));
    }

    [HttpPost("users")]
    public async Task<ActionResult<AdminUserResponse>> CreateSchoolAdmin(CreateSchoolAdminRequest request, CancellationToken cancellationToken)
    {
        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(x => x.Email == email, cancellationToken))
            return Conflict(new ProblemDetails { Title = "An account with this email already exists." });
        if (!await db.Institutions.AnyAsync(x => x.Id == request.InstitutionId && x.IsActive, cancellationToken))
            return BadRequest(new ProblemDetails { Title = "An active institution is required." });
        var user = new AppUser
        {
            Email = email,
            PasswordHash = string.Empty,
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            Role = UserRole.SchoolAdmin,
            InstitutionId = request.InstitutionId,
            FeedPreference = new FeedPreference()
        };
        user.PasswordHash = hasher.HashPassword(user, request.Password);
        db.Users.Add(user);
        Audit("SchoolAdminCreated", user.Id, user.InstitutionId, new { user.Role, user.IsActive });
        await db.SaveChangesAsync(cancellationToken);
        return StatusCode(StatusCodes.Status201Created, ToResponse(user));
    }

    [HttpPatch("users/{id:guid}")]
    public async Task<ActionResult<AdminUserResponse>> UpdateUser(Guid id, UpdateAdminUserRequest request, CancellationToken cancellationToken)
    {
        if (request.Role is not (UserRole.Student or UserRole.Staff or UserRole.SchoolAdmin))
            return BadRequest(new ProblemDetails { Title = "Only Student, Staff, and SchoolAdmin roles can be assigned here." });
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null)
            return NotFound();
        if (user.Role == UserRole.SuperAdmin)
            return Forbid();
        var previousRole = user.Role;
        var previousActive = user.IsActive;
        user.Role = request.Role;
        user.IsActive = request.IsActive;
        user.RefreshTokenHash = null;
        user.RefreshTokenExpiresAt = null;
        Audit("UserAccessUpdated", user.Id, user.InstitutionId,
            new { PreviousRole = previousRole, PreviousActive = previousActive, user.Role, user.IsActive });
        await db.SaveChangesAsync(cancellationToken);
        return Ok(ToResponse(user));
    }

    private static AdminUserResponse ToResponse(AppUser user) => new(user.Id, user.Email, user.Role, user.InstitutionId, user.IsActive);

    private static DateTimeOffset? Max(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null ? second : second is null ? first : first > second ? first : second;

    private static string GetTargetType(string action) => action switch
    {
        "SchoolCreated" => "Institution",
        "SchoolAdminCreated" or "UserAccessUpdated" => "User",
        "ContentCreated" or "ContentUpdated" or "ContentStatusChanged" or "ContentArchived" => "Content",
        _ => "Unknown"
    };

    private static string GetActionTag(string action, string details)
    {
        try
        {
            using var document = JsonDocument.Parse(details);
            if (document.RootElement.TryGetProperty("actionTag", out var tag)) return tag.GetString() ?? "Access";
        }
        catch (JsonException) { }
        return action switch
        {
            "SchoolCreated" => "School",
            "SchoolAdminCreated" => "User",
            "UserAccessUpdated" => "Access",
            _ => "Access"
        };
    }

    private void Audit(string action, Guid targetId, Guid institutionId, object details) => db.AuditLogs.Add(new AuditLog
    {
        ActorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
        Action = action,
        TargetId = targetId,
        InstitutionId = institutionId,
        Details = JsonSerializer.Serialize(details)
    });
}
