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
public sealed class CalendarController(CampusUpdateDbContext db, CampusUpdate.Infrastructure.Media.IMediaStorage storage) : ControllerBase
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

        if (calendar is null) return NotFound(new ProblemDetails { Title = "No active academic calendar is available.", Detail = "Your institution has not published an academic calendar yet." });
        if (calendar.ImageUrl.StartsWith("calendar/", StringComparison.Ordinal))
            calendar = calendar with { ImageUrl = await storage.CreateDownloadUrlAsync(calendar.ImageUrl, TimeSpan.FromMinutes(15), cancellationToken) };
        if (calendar.ImageUrl.StartsWith("/api/v1/mock/", StringComparison.Ordinal) && HttpContext.RequestServices.GetRequiredService<IWebHostEnvironment>().IsDevelopment())
            calendar = calendar with { ImageUrl = $"{Request.Scheme}://{Request.Host}{Request.PathBase}{calendar.ImageUrl}" };
        return Ok(calendar);
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

        if (request.IsOfficial)
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

    [Authorize(Roles = "SchoolAdmin,SuperAdmin")]
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<AcademicCalendarResponse>> Replace(Guid id, CreateAcademicCalendarRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        var calendar = await db.AcademicCalendars.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (calendar is null) return NotFound();
        if (user.Role != UserRole.SuperAdmin && (calendar.InstitutionId != user.InstitutionId || request.InstitutionId != user.InstitutionId)) return Forbid();
        if (!await db.Institutions.AnyAsync(x => x.Id == request.InstitutionId && x.IsActive, cancellationToken)) return BadRequest();
        if (request.IsOfficial)
        {
            var existing = await db.AcademicCalendars.Where(x => x.InstitutionId == request.InstitutionId && x.IsOfficial && x.Id != id).ToListAsync(cancellationToken);
            foreach (var old in existing) old.IsOfficial = false;
        }
        calendar.InstitutionId = request.InstitutionId; calendar.Title = request.Title.Trim(); calendar.AcademicSession = request.AcademicSession.Trim();
        calendar.ImageUrl = request.ImageUrl.Trim(); calendar.PublishedAt = request.PublishedAt; calendar.IsOfficial = request.IsOfficial;
        await db.SaveChangesAsync(cancellationToken);
        return Ok(new AcademicCalendarResponse(calendar.Id, calendar.InstitutionId, calendar.Title, calendar.AcademicSession, calendar.ImageUrl, calendar.PublishedAt));
    }

    [Authorize(Roles = "SchoolAdmin,SuperAdmin")]
    [HttpPost("upload")]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<ActionResult<AcademicCalendarResponse>> Upload(
        [FromForm] CalendarUploadRequest request, CancellationToken cancellationToken)
    {
        var user = await GetCurrentUser(cancellationToken);
        if (user is null) return Unauthorized();
        if (user.Role != UserRole.SuperAdmin && request.InstitutionId != user.InstitutionId) return Forbid();
        if (!await db.Institutions.AnyAsync(i => i.Id == request.InstitutionId && i.IsActive, cancellationToken))
            return BadRequest(new ProblemDetails { Title = "The selected institution is invalid." });
        if (request.File.Length is <= 0 or > 5 * 1024 * 1024)
            return BadRequest(new ProblemDetails { Title = "Calendar images must be between 1 byte and 5 MB." });
        await using var input = request.File.OpenReadStream();
        var header = new byte[12];
        var headerLength = await input.ReadAsync(header, cancellationToken);
        var isPng = headerLength >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var isJpeg = headerLength >= 3 && header[0] == 255 && header[1] == 216 && header[2] == 255;
        var isWebp = headerLength == 12 && System.Text.Encoding.ASCII.GetString(header, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(header, 8, 4) == "WEBP";
        if (!isPng && !isJpeg && !isWebp)
            return BadRequest(new ProblemDetails { Title = "The uploaded file is not a PNG, JPEG or WebP image." });
        input.Position = 0;
        ImageMagick.MagickImage image;
        try
        {
            var info = new ImageMagick.MagickImageInfo(input);
            if (info.Format is not (ImageMagick.MagickFormat.Png or ImageMagick.MagickFormat.Jpeg or ImageMagick.MagickFormat.WebP) || (long)info.Width * info.Height > 40_000_000)
                return BadRequest(new ProblemDetails { Title = "Use a PNG, JPEG or WebP image up to 40 megapixels." });
            input.Position = 0;
            image = new ImageMagick.MagickImage(input);
        }
        catch (ImageMagick.MagickException)
        { return BadRequest(new ProblemDetails { Title = "The uploaded file is not a valid PNG, JPEG or WebP image." }); }
        using (image)
        {
            await using var output = new MemoryStream();
            image.Quality = 85;
            image.Strip();
            await image.WriteAsync(output, ImageMagick.MagickFormat.WebP, cancellationToken);
            output.Position = 0;
            var key = $"calendar/{request.InstitutionId:N}/{Guid.NewGuid():N}.webp";
            await storage.UploadAsync(output, key, "image/webp", output.Length, cancellationToken);
            var previous = await db.AcademicCalendars.Where(c => c.InstitutionId == request.InstitutionId && c.IsOfficial).ToListAsync(cancellationToken);
            foreach (var old in previous) old.IsOfficial = false;
            var calendar = new AcademicCalendar { InstitutionId = request.InstitutionId, Title = request.Title.Trim(), AcademicSession = request.AcademicSession.Trim(), ImageUrl = key, PublishedAt = DateTimeOffset.UtcNow, IsOfficial = true };
            db.AcademicCalendars.Add(calendar);
            await db.SaveChangesAsync(cancellationToken);
            var url = await storage.CreateDownloadUrlAsync(key, TimeSpan.FromMinutes(15), cancellationToken);
            return Created("/api/v1/calendar/current", new AcademicCalendarResponse(calendar.Id, calendar.InstitutionId, calendar.Title, calendar.AcademicSession, url, calendar.PublishedAt));
        }
    }

    private async Task<AppUser?> GetCurrentUser(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return null;
        return await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == userId && x.IsActive, cancellationToken);
    }
}
