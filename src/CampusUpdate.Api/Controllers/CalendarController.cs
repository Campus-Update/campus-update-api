using System.Security.Claims;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Api.Media;
using CampusUpdate.Domain.Schools;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Media;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/v1/calendar")]
public sealed class CalendarController(CampusUpdateDbContext db, IMediaStorage storage) : ControllerBase
{
    [HttpGet]
    [HttpGet("latest")]
    [HttpGet("current")]
    public async Task<ActionResult<AcademicCalendarResponse>> GetLatest(CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null)
            return Unauthorized();

        var calendar = await db.AcademicCalendars.AsNoTracking()
            .Where(x => x.InstitutionId == user.InstitutionId && x.IsOfficial && x.Status == AcademicCalendarStatus.Published)
            .OrderByDescending(x => x.PublishedAt)
            .ThenByDescending(x => x.CreatedAt)
            .Select(x => new AcademicCalendarResponse(
                x.Id,
                x.InstitutionId,
                x.Title,
                x.AcademicSession,
                x.ImageUrl,
                x.PublishedAt,
                x.Status.ToString(),
                x.IsOfficial))
            .FirstOrDefaultAsync(cancellationToken);

        if (calendar is null) return NotFound(new ProblemDetails { Title = "No active academic calendar is available.", Detail = "Your institution has not published an academic calendar yet." });
        if (calendar.ImageUrl.StartsWith("calendar/", StringComparison.Ordinal))
        {
            calendar = calendar with { ImageUrl = await storage.CreateDownloadUrlAsync(calendar.ImageUrl, TimeSpan.FromMinutes(15), cancellationToken) };
        }
        if (calendar.ImageUrl.StartsWith("/api/v1/mock/", StringComparison.Ordinal) && HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment())
            calendar = calendar with { ImageUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}{calendar.ImageUrl}" };
        return Ok(calendar);
    }

    [Authorize(Roles = "SchoolAdmin,LeadSchoolAdmin,CalendarAdmin,SuperAdmin")]
    [HttpGet("manage")]
    public async Task<ActionResult<IReadOnlyList<AcademicCalendarResponse>>> ListForManagement(
        [FromQuery] Guid? institutionId,
        [FromQuery] string? status,
        CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        if (!user.Role.CanManageCalendars()) return Forbid();
        var selectedInstitutionId = user.Role == UserRole.SuperAdmin ? institutionId : user.InstitutionId;
        if (selectedInstitutionId is null) return BadRequest(new ProblemDetails { Title = "institutionId is required for SuperAdmin." });
        if (user.Role != UserRole.SuperAdmin && institutionId.HasValue && institutionId != user.InstitutionId) return Forbid();
        if (!TryParseStatus(status, out var parsedStatus)) return BadRequest(new ProblemDetails { Title = "status must be Draft, PendingApproval, Published or Rejected." });

        var query = db.AcademicCalendars.AsNoTracking().Where(x => x.InstitutionId == selectedInstitutionId.Value);
        if (parsedStatus.HasValue) query = query.Where(x => x.Status == parsedStatus.Value);
        var calendars = await query.OrderByDescending(x => x.CreatedAt).ToListAsync(cancellationToken);
        var response = new List<AcademicCalendarResponse>(calendars.Count);
        foreach (var calendar in calendars)
        {
            var imageUrl = calendar.ImageUrl;
            if (imageUrl.StartsWith("calendar/", StringComparison.Ordinal))
                imageUrl = await storage.CreateDownloadUrlAsync(imageUrl, TimeSpan.FromMinutes(15), cancellationToken);
            response.Add(ToResponse(calendar, imageUrl));
        }
        return Ok(response);
    }

    [Authorize(Roles = "SchoolAdmin,CalendarAdmin,SuperAdmin")]
    [HttpPost("json")]
    public async Task<ActionResult<AcademicCalendarResponse>> Create(
        CreateAcademicCalendarRequest request,
        CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null)
            return Unauthorized();
        if (!user.Role.CanManageCalendars()) return Forbid();
        if (user.Role != UserRole.SuperAdmin && user.InstitutionId != request.InstitutionId)
            return Forbid();
        if (!TryParseStatus(request.Status, out var parsedStatus) || parsedStatus != AcademicCalendarStatus.Draft)
            return BadRequest(new ProblemDetails { Title = "Calendars must be created as Draft and submitted for approval before publication." });
        if (!await db.Institutions.AnyAsync(
                x => x.Id == request.InstitutionId && x.IsActive,
                cancellationToken))
            return BadRequest(new ProblemDetails { Title = "The selected institution is invalid." });

        var calendar = new AcademicCalendar
        {
            InstitutionId = request.InstitutionId,
            Title = request.Title.Trim(),
            AcademicSession = request.AcademicSession.Trim(),
            ImageUrl = request.ImageUrl?.Trim() ?? string.Empty,
            PublishedAt = request.PublishedAt,
            IsOfficial = false,
            Status = parsedStatus.Value
        };
        db.AcademicCalendars.Add(calendar);
        await db.SaveChangesAsync(cancellationToken);

        var response = new AcademicCalendarResponse(
            calendar.Id,
            calendar.InstitutionId,
            calendar.Title,
            calendar.AcademicSession,
            calendar.ImageUrl,
            calendar.PublishedAt,
            calendar.Status.ToString(),
            calendar.IsOfficial);
        return Created("/api/v1/calendar/latest", response);
    }

    [Authorize(Roles = "SchoolAdmin,CalendarAdmin,SuperAdmin")]
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AcademicCalendarResponse>> Replace(Guid id, CreateAcademicCalendarRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        if (!user.Role.CanManageCalendars()) return Forbid();
        var calendar = await db.AcademicCalendars.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (calendar is null) return NotFound();
        if (user.Role != UserRole.SuperAdmin && (calendar.InstitutionId != user.InstitutionId || request.InstitutionId != user.InstitutionId)) return Forbid();
        if (calendar.Status is not (AcademicCalendarStatus.Draft or AcademicCalendarStatus.Rejected))
            return Forbid();
        if (!await db.Institutions.AnyAsync(x => x.Id == request.InstitutionId && x.IsActive, cancellationToken)) return BadRequest();
        if (!TryParseStatus(request.Status, out var parsedStatus) || parsedStatus != AcademicCalendarStatus.Draft)
            return BadRequest(new ProblemDetails { Title = "Calendar edits remain Draft; submit them for approval before publication." });
        calendar.InstitutionId = request.InstitutionId; calendar.Title = request.Title.Trim(); calendar.AcademicSession = request.AcademicSession.Trim();
        calendar.ImageUrl = string.IsNullOrWhiteSpace(request.ImageUrl) ? calendar.ImageUrl : request.ImageUrl.Trim(); calendar.PublishedAt = request.PublishedAt; calendar.Status = AcademicCalendarStatus.Draft; calendar.IsOfficial = false;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(await ToSignedResponse(calendar, cancellationToken));
    }

    [Authorize(Roles = "LeadSchoolAdmin,SuperAdmin")]
    [HttpPost("{id:guid}/publish")]
    public async Task<ActionResult<AcademicCalendarResponse>> Publish(Guid id, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        if (!user.Role.CanManageCalendars()) return Forbid();
        var calendar = await db.AcademicCalendars.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (calendar is null) return NotFound();
        if (user.Role != UserRole.SuperAdmin && calendar.InstitutionId != user.InstitutionId) return Forbid();
        if (calendar.Status != AcademicCalendarStatus.PendingApproval)
            return Conflict(new ProblemDetails { Title = "Only calendars pending approval can be published." });
        if (string.IsNullOrWhiteSpace(calendar.ImageUrl))
            return BadRequest(new ProblemDetails { Title = "Add a calendar image before publishing." });
        var previousStatus = calendar.Status;
        var previous = await db.AcademicCalendars.Where(x => x.InstitutionId == calendar.InstitutionId && x.IsOfficial && x.Id != id).ToListAsync(cancellationToken);
        foreach (var old in previous) old.IsOfficial = false;
        calendar.Status = AcademicCalendarStatus.Published;
        calendar.IsOfficial = true;
        calendar.PublishedAt = DateTimeOffset.UtcNow;
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = user.Id,
            Action = "CalendarPublished",
            TargetId = calendar.Id,
            InstitutionId = calendar.InstitutionId,
            Details = System.Text.Json.JsonSerializer.Serialize(new
            {
                targetType = "AcademicCalendar",
                calendar.Title,
                previousStatus,
                currentStatus = AcademicCalendarStatus.Published
            })
        });
        await db.SaveChangesAsync(cancellationToken);
        return Ok(await ToSignedResponse(calendar, cancellationToken));
    }

    [Authorize(Roles = "LeadSchoolAdmin,SuperAdmin")]
    [HttpPost("{id:guid}/reject")]
    public async Task<ActionResult<AcademicCalendarResponse>> Reject(
        Guid id,
        RejectAcademicCalendarRequest request,
        CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        var calendar = await db.AcademicCalendars.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (calendar is null) return NotFound();
        if (user.Role != UserRole.SuperAdmin && calendar.InstitutionId != user.InstitutionId) return Forbid();
        if (calendar.Status != AcademicCalendarStatus.PendingApproval)
            return Conflict(new ProblemDetails { Title = "Only pending calendars can be rejected." });
        if (string.IsNullOrWhiteSpace(request.Reason))
            return BadRequest(new ProblemDetails { Title = "A rejection reason is required." });

        calendar.Status = AcademicCalendarStatus.Rejected;
        calendar.IsOfficial = false;
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = user.Id,
            Action = "CalendarRejected",
            TargetId = calendar.Id,
            InstitutionId = calendar.InstitutionId,
            Details = System.Text.Json.JsonSerializer.Serialize(new
            {
                targetType = "AcademicCalendar",
                calendar.Title,
                reason = request.Reason.Trim()
            })
        });
        await db.SaveChangesAsync(cancellationToken);
        return Ok(await ToSignedResponse(calendar, cancellationToken));
    }

    [Authorize(Roles = "SchoolAdmin,CalendarAdmin,SuperAdmin")]
    [HttpPost("{id:guid}/submit")]
    public async Task<ActionResult<AcademicCalendarResponse>> SubmitForApproval(
        Guid id,
        CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        var calendar = await db.AcademicCalendars.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (calendar is null) return NotFound();
        if (calendar.InstitutionId != user.InstitutionId) return Forbid();
        if (calendar.Status != AcademicCalendarStatus.Draft)
            return Conflict(new ProblemDetails { Title = "Only calendar drafts can be submitted for approval." });
        if (string.IsNullOrWhiteSpace(calendar.ImageUrl))
            return BadRequest(new ProblemDetails { Title = "Add a calendar image before submitting it." });

        calendar.Status = AcademicCalendarStatus.PendingApproval;
        calendar.IsOfficial = false;
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = user.Id,
            Action = "CalendarSubmittedForApproval",
            TargetId = calendar.Id,
            InstitutionId = calendar.InstitutionId,
            Details = System.Text.Json.JsonSerializer.Serialize(new
            {
                targetType = "AcademicCalendar",
                calendar.Title,
                previousStatus = AcademicCalendarStatus.Draft,
                currentStatus = calendar.Status
            })
        });
        await db.SaveChangesAsync(cancellationToken);
        return Ok(await ToSignedResponse(calendar, cancellationToken));
    }

    [Authorize(Roles = "SchoolAdmin,CalendarAdmin,SuperAdmin")]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteDraft(Guid id, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        if (!user.Role.CanManageCalendars()) return Forbid();
        var calendar = await db.AcademicCalendars.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (calendar is null) return NotFound();
        if (user.Role != UserRole.SuperAdmin && calendar.InstitutionId != user.InstitutionId) return Forbid();
        if (calendar.Status != AcademicCalendarStatus.Draft)
            return Conflict(new ProblemDetails { Title = "Only calendar drafts can be deleted." });
        db.AcademicCalendars.Remove(calendar);
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    [Authorize(Roles = "SchoolAdmin,CalendarAdmin,SuperAdmin")]
    [HttpPost]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<ActionResult<AcademicCalendarResponse>> Upload(
        [FromForm] CalendarUploadRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        if (!user.Role.CanManageCalendars()) return Forbid();
        if (user.Role != UserRole.SuperAdmin && request.InstitutionId != user.InstitutionId) return Forbid();
        if (!TryParseStatus(request.Status, out var parsedStatus) || parsedStatus != AcademicCalendarStatus.Draft)
            return BadRequest(new ProblemDetails { Title = "Calendar uploads create Drafts; submit them for approval before publication." });
        if (!await db.Institutions.AnyAsync(i => i.Id == request.InstitutionId && i.IsActive, cancellationToken))
            return BadRequest(new ProblemDetails { Title = "The selected institution is invalid." });
        AcademicCalendar? editedCalendar = null;
        if (request.CalendarId.HasValue)
        {
            editedCalendar = await db.AcademicCalendars.SingleOrDefaultAsync(c => c.Id == request.CalendarId.Value, cancellationToken);
            if (editedCalendar is null) return NotFound();
            if (editedCalendar.InstitutionId != request.InstitutionId) return Forbid();
            if (editedCalendar.Status is not (AcademicCalendarStatus.Draft or AcademicCalendarStatus.Rejected))
                return Forbid();
        }
        var storedImage = await CalendarImageStorage.StoreAsync(request.File, request.InstitutionId, storage, cancellationToken);
        if (storedImage.Error is not null)
            return BadRequest(new ProblemDetails { Title = storedImage.Error });
        var key = storedImage.Key ?? throw new InvalidOperationException("Calendar image storage returned no key.");
        var calendar = editedCalendar ?? new AcademicCalendar { InstitutionId = request.InstitutionId, Title = request.Title.Trim(), AcademicSession = request.AcademicSession.Trim(), ImageUrl = key, PublishedAt = DateTimeOffset.UtcNow, IsOfficial = false, Status = AcademicCalendarStatus.Draft };
        calendar.Title = request.Title.Trim();
        calendar.AcademicSession = request.AcademicSession.Trim();
        calendar.ImageUrl = key;
        calendar.PublishedAt = request.PublishedAt ?? DateTimeOffset.UtcNow;
        calendar.IsOfficial = false;
        calendar.Status = AcademicCalendarStatus.Draft;
        if (editedCalendar is null) db.AcademicCalendars.Add(calendar);
        await db.SaveChangesAsync(cancellationToken);
        var url = await storage.CreateDownloadUrlAsync(key, TimeSpan.FromMinutes(15), cancellationToken);
        return Created("/api/v1/calendar/manage", ToResponse(calendar, url));
    }

    [Authorize(Roles = "SchoolAdmin,CalendarAdmin,SuperAdmin")]
    [HttpPost("{id:guid}/media")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<ActionResult<AcademicCalendarResponse>> UploadCalendarMedia(
        Guid id, IFormFile file, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        if (!user.Role.CanManageCalendars()) return Forbid();

        var calendar = await db.AcademicCalendars.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (calendar is null) return NotFound();
        if (user.Role != UserRole.SuperAdmin && calendar.InstitutionId != user.InstitutionId) return Forbid();
        if (calendar.Status is not (AcademicCalendarStatus.Draft or AcademicCalendarStatus.Rejected))
            return Forbid();

        var storedImage = await CalendarImageStorage.StoreAsync(file, calendar.InstitutionId, storage, cancellationToken);
        if (storedImage.Error is not null)
            return BadRequest(new ProblemDetails { Title = storedImage.Error });
        calendar.ImageUrl = storedImage.Key!;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(await ToSignedResponse(calendar, cancellationToken));
    }

    [HttpGet("{id:guid}/media")]
    public async Task<ActionResult<CalendarMediaResponse>> GetCalendarMedia(
        Guid id, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        var calendar = await db.AcademicCalendars.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (calendar is null) return NotFound();

        if (user.Role != UserRole.SuperAdmin)
        {
            if (calendar.InstitutionId != user.InstitutionId)
                return user.Role.IsInstitutionAdministrator() ? Forbid() : NotFound();
            if (!user.Role.CanManageCalendars() &&
                (calendar.Status != AcademicCalendarStatus.Published || !calendar.IsOfficial))
                return NotFound();
        }

        var downloadUrl = calendar.ImageUrl;
        if (downloadUrl.StartsWith("calendar/", StringComparison.Ordinal))
        {
            downloadUrl = await storage.CreateDownloadUrlAsync(downloadUrl, TimeSpan.FromMinutes(15), cancellationToken);
        }
        else if (downloadUrl.StartsWith("/api/v1/mock/", StringComparison.Ordinal) &&
                 HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment())
            downloadUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}{downloadUrl}";

        return Ok(new CalendarMediaResponse(calendar.Id, downloadUrl));
    }

    private static bool TryParseStatus(string? value, out AcademicCalendarStatus? status)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            status = null;
            return true;
        }
        if (Enum.TryParse<AcademicCalendarStatus>(value, true, out var parsed) && Enum.IsDefined(parsed))
        {
            status = parsed;
            return true;
        }
        status = null;
        return false;
    }

    private static AcademicCalendarResponse ToResponse(AcademicCalendar calendar, string? imageUrl = null) =>
        new(calendar.Id, calendar.InstitutionId, calendar.Title, calendar.AcademicSession,
            imageUrl ?? calendar.ImageUrl, calendar.PublishedAt, calendar.Status.ToString(), calendar.IsOfficial);

    private async Task<AcademicCalendarResponse> ToSignedResponse(AcademicCalendar calendar, CancellationToken cancellationToken)
    {
        var imageUrl = calendar.ImageUrl;
        if (imageUrl.StartsWith("calendar/", StringComparison.Ordinal))
        {
            imageUrl = await storage.CreateDownloadUrlAsync(imageUrl, TimeSpan.FromMinutes(15), cancellationToken);
        }
        return ToResponse(calendar, imageUrl);
    }

    private async Task<AppUser?> GetCurrentUser(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return null;
        return await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == userId && x.IsActive, cancellationToken);
    }
}
