using System.Security.Claims;
using System.Text.Json;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Api.Controllers;

[ApiController]
[Authorize(Roles = "LeadSchoolAdmin,SuperAdmin")]
[Route("api/v1/admin/administrators")]
public sealed class InstitutionAdministratorsController(
    CampusUpdateDbContext db,
    IPasswordHasher<AppUser> hasher) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<IReadOnlyCollection<InstitutionAdministratorResponse>>> List(
        [FromQuery] Guid? institutionId,
        CancellationToken cancellationToken)
    {
        var caller = await GetCurrentUser(cancellationToken);
        if (caller is null) return Unauthorized();
        if (caller.Role == UserRole.LeadSchoolAdmin)
        {
            if (institutionId.HasValue && institutionId != caller.InstitutionId) return Forbid();
            institutionId = caller.InstitutionId;
        }

        var users = db.Users.AsNoTracking()
            .Where(x => x.Role == UserRole.NewsAdmin || x.Role == UserRole.CalendarAdmin ||
                (caller.Role == UserRole.SuperAdmin && x.Role == UserRole.LeadSchoolAdmin));
        if (institutionId.HasValue)
            users = users.Where(x => x.InstitutionId == institutionId.Value);

        var results = await users.OrderBy(x => x.Email)
            .Select(x => new InstitutionAdministratorResponse(
                x.Id, x.Email, x.FirstName, x.LastName, x.Role, x.InstitutionId, x.IsActive))
            .ToListAsync(cancellationToken);
        return Ok(results);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<InstitutionAdministratorResponse>> Get(
        Guid id,
        CancellationToken cancellationToken)
    {
        var caller = await GetCurrentUser(cancellationToken);
        if (caller is null) return Unauthorized();
        var user = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id &&
                (x.Role == UserRole.NewsAdmin || x.Role == UserRole.CalendarAdmin ||
                 (caller.Role == UserRole.SuperAdmin && x.Role == UserRole.LeadSchoolAdmin)), cancellationToken);
        if (user is null) return NotFound();
        if (caller.Role == UserRole.LeadSchoolAdmin && user.InstitutionId != caller.InstitutionId)
            return NotFound();
        return Ok(ToResponse(user));
    }

    [HttpPost]
    public async Task<ActionResult<InstitutionAdministratorResponse>> Create(
        CreateInstitutionAdministratorRequest request,
        CancellationToken cancellationToken)
    {
        var caller = await GetCurrentUser(cancellationToken);
        if (caller is null) return Unauthorized();
        if (request.Role is not (UserRole.NewsAdmin or UserRole.CalendarAdmin) &&
            !(caller.Role == UserRole.SuperAdmin && request.Role == UserRole.LeadSchoolAdmin))
            return BadRequest(new ProblemDetails { Title = "LeadSchoolAdmin can only be assigned by a SuperAdmin; other institution administrators can be assigned by a LeadSchoolAdmin." });
        if (caller.Role == UserRole.LeadSchoolAdmin && request.InstitutionId != caller.InstitutionId)
            return Forbid();
        if (!await db.Institutions.AnyAsync(
                x => x.Id == request.InstitutionId && x.IsActive, cancellationToken))
            return BadRequest(new ProblemDetails { Title = "An active institution is required." });

        var email = request.Email.Trim().ToLowerInvariant();
        if (await db.Users.AnyAsync(x => x.Email == email, cancellationToken))
            return Conflict(new ProblemDetails { Title = "An account with this email already exists." });

        var user = new AppUser
        {
            Email = email,
            PasswordHash = string.Empty,
            FirstName = request.FirstName.Trim(),
            LastName = request.LastName.Trim(),
            Role = request.Role,
            InstitutionId = request.InstitutionId,
            FeedPreference = new FeedPreference()
        };
        user.PasswordHash = hasher.HashPassword(user, request.Password);
        db.Users.Add(user);
        Audit("InstitutionAdministratorCreated", user.Id, user.InstitutionId,
            new { user.Role, user.IsActive });
        await db.SaveChangesAsync(cancellationToken);

        var response = ToResponse(user);
        return CreatedAtAction(nameof(Get), new { id = user.Id }, response);
    }

    [HttpPatch("{id:guid}")]
    public async Task<ActionResult<InstitutionAdministratorResponse>> Update(
        Guid id,
        UpdateInstitutionAdministratorRequest request,
        CancellationToken cancellationToken)
    {
        var caller = await GetCurrentUser(cancellationToken);
        if (caller is null) return Unauthorized();
        if (request.Role is not (UserRole.NewsAdmin or UserRole.CalendarAdmin) &&
            !(caller.Role == UserRole.SuperAdmin && request.Role == UserRole.LeadSchoolAdmin))
            return BadRequest(new ProblemDetails { Title = "LeadSchoolAdmin can only be assigned by a SuperAdmin; other institution administrators can be assigned by a LeadSchoolAdmin." });

        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (user is null) return NotFound();
        if (user.Role is not (UserRole.NewsAdmin or UserRole.CalendarAdmin) &&
            !(caller.Role == UserRole.SuperAdmin && user.Role == UserRole.LeadSchoolAdmin))
            return Forbid();
        if (caller.Role == UserRole.LeadSchoolAdmin && user.InstitutionId != caller.InstitutionId)
            return Forbid();

        var previousRole = user.Role;
        var previousActive = user.IsActive;
        user.Role = request.Role;
        user.IsActive = request.IsActive;
        user.RefreshTokenHash = null;
        user.RefreshTokenExpiresAt = null;
        Audit("InstitutionAdministratorUpdated", user.Id, user.InstitutionId,
            new { PreviousRole = previousRole, PreviousActive = previousActive, user.Role, user.IsActive });
        await db.SaveChangesAsync(cancellationToken);
        return Ok(ToResponse(user));
    }

    private async Task<AppUser?> GetCurrentUser(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return null;
        return await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == userId && x.IsActive, cancellationToken);
    }

    private void Audit(string action, Guid targetId, Guid institutionId, object details) =>
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!),
            Action = action,
            TargetId = targetId,
            InstitutionId = institutionId,
            Details = JsonSerializer.Serialize(details)
        });

    private static InstitutionAdministratorResponse ToResponse(AppUser user) =>
        new(user.Id, user.Email, user.FirstName, user.LastName, user.Role, user.InstitutionId, user.IsActive);
}
