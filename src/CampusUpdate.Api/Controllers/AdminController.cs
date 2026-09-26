using System.Security.Claims;
using System.Text.Json;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Schools;
using CampusUpdate.Domain.Users;
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
