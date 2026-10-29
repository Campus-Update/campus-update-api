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

    [Authorize(Roles = "SchoolAdmin,LeadSchoolAdmin,CalendarAdmin,SuperAdmin")]
    [HttpPost]
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
        if (!TryParseStatus(request.Status, out var parsedStatus) || parsedStatus is null)
            return BadRequest(new ProblemDetails { Title = "status must be Draft or Published." });
        if (parsedStatus is AcademicCalendarStatus.PendingApproval or AcademicCalendarStatus.Rejected)
            return BadRequest(new ProblemDetails { Title = "Submit a draft for approval after creating it." });
        if ((user.Role is UserRole.CalendarAdmin or UserRole.LeadSchoolAdmin) &&
            parsedStatus != AcademicCalendarStatus.Draft)
            return Forbid();
        if (parsedStatus == AcademicCalendarStatus.Published && !Uri.TryCreate(request.ImageUrl, UriKind.Absolute, out _))
            return BadRequest(new ProblemDetails { Title = "A valid imageUrl is required to publish a calendar." });
        if (!await db.Institutions.AnyAsync(
                x => x.Id == request.InstitutionId && x.IsActive,
                cancellationToken))
            return BadRequest(new ProblemDetails { Title = "The selected institution is invalid." });

        var isPublished = parsedStatus == AcademicCalendarStatus.Published;
        if (isPublished && request.IsOfficial)
        {
            var existing = await db.AcademicCalendars.Where(x => x.InstitutionId == request.InstitutionId && x.IsOfficial).ToListAsync(cancellationToken);
            foreach (var oldCalendar in existing) oldCalendar.IsOfficial = false;
        }

        var calendar = new AcademicCalendar
        {
            InstitutionId = request.InstitutionId,
            Title = request.Title.Trim(),
            AcademicSession = request.AcademicSession.Trim(),
            ImageUrl = request.ImageUrl.Trim(),
            PublishedAt = request.PublishedAt,
            IsOfficial = isPublished && request.IsOfficial,
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

    [Authorize(Roles = "SchoolAdmin,LeadSchoolAdmin,CalendarAdmin,SuperAdmin")]
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AcademicCalendarResponse>> Replace(Guid id, CreateAcademicCalendarRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        if (!user.Role.CanManageCalendars()) return Forbid();
        var calendar = await db.AcademicCalendars.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (calendar is null) return NotFound();
        if (user.Role != UserRole.SuperAdmin && (calendar.InstitutionId != user.InstitutionId || request.InstitutionId != user.InstitutionId)) return Forbid();
        if (user.Role == UserRole.CalendarAdmin &&
            (calendar.Status is not (AcademicCalendarStatus.Draft or AcademicCalendarStatus.Rejected) ||
             !string.Equals(request.Status, "Draft", StringComparison.OrdinalIgnoreCase)))
            return Forbid();
        if (calendar.Status == AcademicCalendarStatus.PendingApproval &&
            user.Role is not (UserRole.LeadSchoolAdmin or UserRole.SuperAdmin))
            return Forbid();
        if (!await db.Institutions.AnyAsync(x => x.Id == request.InstitutionId && x.IsActive, cancellationToken)) return BadRequest();
        if (!TryParseStatus(request.Status, out var parsedStatus) || parsedStatus is null)
            return BadRequest(new ProblemDetails { Title = "status must be Draft or Published." });
        if (parsedStatus is AcademicCalendarStatus.PendingApproval or AcademicCalendarStatus.Rejected)
            return BadRequest(new ProblemDetails { Title = "Submit a draft for approval after saving it." });
        if (user.Role == UserRole.LeadSchoolAdmin && parsedStatus == AcademicCalendarStatus.Published)
            return Forbid();
        if (parsedStatus == AcademicCalendarStatus.Published &&
            string.IsNullOrWhiteSpace(request.ImageUrl) && string.IsNullOrWhiteSpace(calendar.ImageUrl))
            return BadRequest(new ProblemDetails { Title = "A valid imageUrl is required to publish a calendar." });
        var isPublished = parsedStatus == AcademicCalendarStatus.Published;
        if (isPublished && request.IsOfficial)
        {
            var existing = await db.AcademicCalendars.Where(x => x.InstitutionId == request.InstitutionId && x.IsOfficial && x.Id != id).ToListAsync(cancellationToken);
            foreach (var old in existing) old.IsOfficial = false;
        }
        calendar.InstitutionId = request.InstitutionId; calendar.Title = request.Title.Trim(); calendar.AcademicSession = request.AcademicSession.Trim();
        calendar.ImageUrl = string.IsNullOrWhiteSpace(request.ImageUrl) ? calendar.ImageUrl : request.ImageUrl.Trim(); calendar.PublishedAt = request.PublishedAt; calendar.Status = parsedStatus.Value; calendar.IsOfficial = isPublished && request.IsOfficial;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(await ToSignedResponse(calendar, cancellationToken));
    }

    [Authorize(Roles = "SchoolAdmin,LeadSchoolAdmin,SuperAdmin")]
    [HttpPost("{id:guid}/publish")]
    public async Task<ActionResult<AcademicCalendarResponse>> Publish(Guid id, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        if (!user.Role.CanManageCalendars()) return Forbid();
        var calendar = await db.AcademicCalendars.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (calendar is null) return NotFound();
        if (user.Role != UserRole.SuperAdmin && calendar.InstitutionId != user.InstitutionId) return Forbid();
        if (user.Role == UserRole.LeadSchoolAdmin && calendar.Status != AcademicCalendarStatus.PendingApproval)
            return Conflict(new ProblemDetails { Title = "LeadSchoolAdmins can only publish calendars pending their approval." });
        if (calendar.Status == AcademicCalendarStatus.PendingApproval &&
            user.Role is not (UserRole.LeadSchoolAdmin or UserRole.SuperAdmin))
            return Forbid();
        if (calendar.Status == AcademicCalendarStatus.Rejected)
            return Conflict(new ProblemDetails { Title = "Rejected calendars must be revised and resubmitted before publishing." });
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

    [Authorize(Roles = "CalendarAdmin")]
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

    [Authorize(Roles = "SchoolAdmin,LeadSchoolAdmin,CalendarAdmin,SuperAdmin")]
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

    [Authorize(Roles = "SchoolAdmin,LeadSchoolAdmin,CalendarAdmin,SuperAdmin")]
    [HttpPost("upload")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<ActionResult<AcademicCalendarResponse>> Upload(
        [FromForm] CalendarUploadRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        if (!user.Role.CanManageCalendars()) return Forbid();
        if (user.Role != UserRole.SuperAdmin && request.InstitutionId != user.InstitutionId) return Forbid();
        if (!TryParseStatus(request.Status, out var parsedStatus) || parsedStatus is null)
            return BadRequest(new ProblemDetails { Title = "status must be Draft or Published." });
        if (parsedStatus is AcademicCalendarStatus.PendingApproval or AcademicCalendarStatus.Rejected)
            return BadRequest(new ProblemDetails { Title = "Submit a draft for approval after uploading it." });
        if ((user.Role is UserRole.CalendarAdmin or UserRole.LeadSchoolAdmin) &&
            parsedStatus != AcademicCalendarStatus.Draft)
            return Forbid();
        if (!await db.Institutions.AnyAsync(i => i.Id == request.InstitutionId && i.IsActive, cancellationToken))
            return BadRequest(new ProblemDetails { Title = "The selected institution is invalid." });
        AcademicCalendar? editedCalendar = null;
        if (request.CalendarId.HasValue)
        {
            editedCalendar = await db.AcademicCalendars.SingleOrDefaultAsync(c => c.Id == request.CalendarId.Value, cancellationToken);
            if (editedCalendar is null) return NotFound();
            if (editedCalendar.InstitutionId != request.InstitutionId) return Forbid();
            if (user.Role == UserRole.CalendarAdmin &&
                editedCalendar.Status is not (AcademicCalendarStatus.Draft or AcademicCalendarStatus.Rejected))
                return Forbid();
            if (editedCalendar.Status == AcademicCalendarStatus.PendingApproval &&
                user.Role is not (UserRole.LeadSchoolAdmin or UserRole.SuperAdmin))
                return Forbid();
        }
        var storedImage = await CalendarImageStorage.StoreAsync(request.File, request.InstitutionId, storage, cancellationToken);
        if (storedImage.Error is not null)
            return BadRequest(new ProblemDetails { Title = storedImage.Error });
        var key = storedImage.Key ?? throw new InvalidOperationException("Calendar image storage returned no key.");
        var isPublished = parsedStatus == AcademicCalendarStatus.Published;
        if (isPublished)
        {
            var previous = await db.AcademicCalendars.Where(c => c.InstitutionId == request.InstitutionId && c.IsOfficial && c.Id != request.CalendarId).ToListAsync(cancellationToken);
            foreach (var old in previous) old.IsOfficial = false;
        }
        var calendar = editedCalendar ?? new AcademicCalendar { InstitutionId = request.InstitutionId, Title = request.Title.Trim(), AcademicSession = request.AcademicSession.Trim(), ImageUrl = key, PublishedAt = DateTimeOffset.UtcNow, IsOfficial = isPublished, Status = parsedStatus.Value };
        calendar.Title = request.Title.Trim();
        calendar.AcademicSession = request.AcademicSession.Trim();
        calendar.ImageUrl = key;
        calendar.PublishedAt = request.PublishedAt ?? DateTimeOffset.UtcNow;
        calendar.IsOfficial = isPublished;
        calendar.Status = parsedStatus.Value;
        if (editedCalendar is null) db.AcademicCalendars.Add(calendar);
        await db.SaveChangesAsync(cancellationToken);
        var url = await storage.CreateDownloadUrlAsync(key, TimeSpan.FromMinutes(15), cancellationToken);
        return Created(isPublished ? "/api/v1/calendar/current" : "/api/v1/calendar/manage", ToResponse(calendar, url));
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
            imageUrl = await storage.CreateDownloadUrlAsync(imageUrl, TimeSpan.FromMinutes(15), cancellationToken);
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
