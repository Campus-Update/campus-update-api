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
                    a.TargetAudience)).ToArray())).ToArray()));
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

    private void Audit(string action, Guid targetId, Guid institutionId, object details) => db.AuditLogs.Add(new AuditLog
    {
        ActorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
        Action = action,
        TargetId = targetId,
        InstitutionId = institutionId,
        Details = JsonSerializer.Serialize(details)
    });
}
