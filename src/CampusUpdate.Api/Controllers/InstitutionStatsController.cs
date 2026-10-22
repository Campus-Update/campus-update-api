using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Api.Controllers;

[ApiController]
[Authorize(Roles = nameof(UserRole.SuperAdmin))]
[Route("api/v1/institution")]
public sealed class InstitutionStatsController(CampusUpdateDbContext db) : ControllerBase
{
    [HttpGet("stats")]
    public async Task<ActionResult<InstitutionStatsResponse>> GetStats(CancellationToken cancellationToken)
    {
        var total = await db.Institutions.CountAsync(cancellationToken);
        var active = await db.Institutions.CountAsync(x => x.IsActive, cancellationToken);
        var pendingVerification = await db.Institutions.CountAsync(x => !x.IsActive, cancellationToken);
        var studentsReached = await db.ContentViews.AsNoTracking()
            .Where(view => db.Users.Any(user => user.Id == view.UserId && user.Role == UserRole.Student))
            .Select(view => view.UserId).Distinct().CountAsync(cancellationToken);
        return Ok(new InstitutionStatsResponse(total, active, pendingVerification, studentsReached));
    }
}
