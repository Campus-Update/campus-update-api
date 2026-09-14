using System.Security.Claims;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Schools;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/calendar")]
public sealed class CalendarController(CampusUpdateDbContext db) : ControllerBase
{
    [HttpGet]
    [HttpGet("latest")]
    public async Task<ActionResult<AcademicCalendarResponse>> GetLatest(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null)
            return Unauthorized();

        var calendar = await db.AcademicCalendars.AsNoTracking()
            .Where(x => x.InstitutionId == user.InstitutionId && x.IsOfficial)
            .OrderByDescending(x => x.PublishedAt)
            .ThenByDescending(x => x.CreatedAt)
            .Select(x => new AcademicCalendarResponse(
                x.Id,
                x.InstitutionId,
                x.Title,
                x.AcademicSession,
                x.ImageUrl,
                x.PublishedAt))
            .FirstOrDefaultAsync(cancellationToken);

        return calendar is null ? NotFound() : Ok(calendar);
    }

    [Authorize(Roles = "SchoolAdmin,SuperAdmin")]
    [HttpPost]
    public async Task<ActionResult<AcademicCalendarResponse>> Create(
        CreateAcademicCalendarRequest request,
        CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null)
            return Unauthorized();
        if (user.Role != UserRole.SuperAdmin && user.InstitutionId != request.InstitutionId)
            return Forbid();
        if (!await db.Institutions.AnyAsync(
                x => x.Id == request.InstitutionId && x.IsActive,
                cancellationToken))
            return BadRequest(new ProblemDetails { Title = "The selected institution is invalid." });

        var calendar = new AcademicCalendar
        {
            InstitutionId = request.InstitutionId,
            Title = request.Title.Trim(),
            AcademicSession = request.AcademicSession.Trim(),
            ImageUrl = request.ImageUrl.Trim(),
            PublishedAt = request.PublishedAt,
            IsOfficial = request.IsOfficial
        };
        db.AcademicCalendars.Add(calendar);
        await db.SaveChangesAsync(cancellationToken);

        var response = new AcademicCalendarResponse(
            calendar.Id,
            calendar.InstitutionId,
            calendar.Title,
            calendar.AcademicSession,
            calendar.ImageUrl,
            calendar.PublishedAt);
        return Created("/api/v1/calendar/latest", response);
    }

    private async Task<AppUser?> GetCurrentUser(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return null;
        return await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == userId && x.IsActive, cancellationToken);
    }
}
