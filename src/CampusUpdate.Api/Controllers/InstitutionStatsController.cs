using System.Security.Claims;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Api.Controllers;

[ApiController]
[Authorize(Roles = nameof(UserRole.SchoolAdmin) + "," + nameof(UserRole.SuperAdmin))]
[Route("api/v1/institution")]
public sealed class InstitutionStatsController(CampusUpdateDbContext db) : ControllerBase
{
    [HttpGet("stats")]
    public async Task<ActionResult<InstitutionStatsResponse>> GetStats(CancellationToken cancellationToken)
    {
        Guid? institutionId = null;
        if (User.IsInRole(nameof(UserRole.SchoolAdmin)))
        {
            if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
                return Unauthorized();
            institutionId = await db.Users.AsNoTracking()
                .Where(user => user.Id == userId && user.IsActive && user.Role == UserRole.SchoolAdmin)
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
}
