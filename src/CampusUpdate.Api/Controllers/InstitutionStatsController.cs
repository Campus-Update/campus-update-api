using System.Security.Claims;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Api.Controllers;

[ApiController]
[Authorize(Roles = nameof(UserRole.LeadSchoolAdmin) + "," +
                    nameof(UserRole.NewsAdmin) + "," + nameof(UserRole.CalendarAdmin) + "," +
                    nameof(UserRole.SuperAdmin))]
[Route("api/v1/institution")]
public sealed class InstitutionStatsController(CampusUpdateDbContext db) : ControllerBase
{
    [HttpGet("stats")]
    public async Task<ActionResult<InstitutionStatsResponse>> GetStats(CancellationToken cancellationToken)
    {
        Guid? institutionId = null;
        if (!User.IsInRole(nameof(UserRole.SuperAdmin)))
        {
            if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                return Unauthorized();
            institutionId = await db.Users.AsNoTracking()
                .Where(user => user.Id == userId && user.IsActive && user.Role != UserRole.SuperAdmin)
                .Select(user => (Guid?)user.InstitutionId)
                .SingleOrDefaultAsync(cancellationToken);
            if (institutionId is null)
                return Unauthorized();
        }

        var institutions = db.Institutions.AsNoTracking();
        var contentViews = db.ContentViews.AsNoTracking()
            .Where(view => db.Users.Any(user => user.Id == view.UserId && user.Role == UserRole.Student));
        if (institutionId is not null)
        {
            institutions = institutions.Where(institution => institution.Id == institutionId.Value);
            contentViews = contentViews.Where(view => view.InstitutionId == institutionId.Value);
        }

        var total = await institutions.CountAsync(cancellationToken);
        var active = await institutions.CountAsync(institution => institution.IsActive, cancellationToken);
        var pendingVerification = await institutions.CountAsync(institution => !institution.IsActive, cancellationToken);
        var studentsReached = await contentViews
            .Select(view => view.UserId).Distinct().CountAsync(cancellationToken);
        return Ok(new InstitutionStatsResponse(total, active, pendingVerification, studentsReached));
    }

    [HttpGet("stats/registrations-by-department")]
    public async Task<ActionResult<IReadOnlyCollection<DepartmentRegistrationStatsResponse>>> GetRegistrationsByDepartment(
        [FromQuery] Guid? institutionId,
        CancellationToken cancellationToken)
    {
        var scope = await ResolveInstitutionScope(institutionId, cancellationToken);
        if (scope.Error is not null) return scope.Error;

        var result = await db.Departments.AsNoTracking()
            .Where(department => department.Faculty.InstitutionId == scope.InstitutionId)
            .OrderBy(department => department.Faculty.Name)
            .ThenBy(department => department.Name)
            .Select(department => new DepartmentRegistrationStatsResponse(
                department.Faculty.InstitutionId,
                department.Faculty.Institution.Name,
                department.FacultyId,
                department.Faculty.Name,
                department.Id,
                department.Name,
                db.Users.Count(user => user.DepartmentId == department.Id && user.Role == UserRole.Student),
                db.Users.Count(user => user.DepartmentId == department.Id && user.Role == UserRole.Staff),
                db.Users.Count(user => user.DepartmentId == department.Id &&
                    (user.Role == UserRole.Student || user.Role == UserRole.Staff))))
            .ToListAsync(cancellationToken);

        return Ok(result);
    }

    [HttpGet("stats/weekly-active-students")]
    public async Task<ActionResult<WeeklyActiveStudentsResponse>> GetWeeklyActiveStudents(
        [FromQuery] Guid? institutionId,
        CancellationToken cancellationToken)
    {
        var scope = await ResolveInstitutionScope(institutionId, cancellationToken);
        if (scope.Error is not null) return scope.Error;

        var since = DateTimeOffset.UtcNow.AddDays(-7);
        var activityTypes = new[] { "login", "session_refresh", "feed_fetch" };
        var activeStudentCount = await db.UserActivities.AsNoTracking()
            .Where(activity => activity.InstitutionId == scope.InstitutionId &&
                activity.UserId != null && activity.OccurredAt >= since &&
                activityTypes.Contains(activity.ActivityType) &&
                db.Users.Any(user => user.Id == activity.UserId && user.IsActive &&
                    user.Role == UserRole.Student && user.InstitutionId == scope.InstitutionId))
            .Select(activity => activity.UserId)
            .Distinct()
            .CountAsync(cancellationToken);

        return Ok(new WeeklyActiveStudentsResponse(scope.InstitutionId!.Value, since, activeStudentCount));
    }

    private async Task<(Guid? InstitutionId, ActionResult? Error)> ResolveInstitutionScope(
        Guid? requestedInstitutionId,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return (null, Unauthorized());

        var caller = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(user => user.Id == userId && user.IsActive, cancellationToken);
        if (caller is null) return (null, Unauthorized());

        if (caller.Role == UserRole.SuperAdmin)
        {
            if (!requestedInstitutionId.HasValue)
                return (null, BadRequest(new ProblemDetails { Title = "institutionId is required for SuperAdmin." }));
            if (!await db.Institutions.AnyAsync(x => x.Id == requestedInstitutionId.Value, cancellationToken))
                return (null, NotFound());
            return (requestedInstitutionId.Value, null);
        }

        if (requestedInstitutionId.HasValue && requestedInstitutionId.Value != caller.InstitutionId)
            return (null, Forbid());
        return (caller.InstitutionId, null);
    }
}
