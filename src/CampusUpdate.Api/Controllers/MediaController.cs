using System.Security.Claims;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Content;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Media;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Api.Controllers;

[ApiController]
[Authorize(Roles = "SchoolAdmin,SuperAdmin")]
[Route("api/v1/media")]
public sealed class MediaController(CampusUpdateDbContext db, IMediaStorage storage) : ControllerBase
{
    private static readonly HashSet<string> AllowedTypes = new(StringComparer.OrdinalIgnoreCase)
        { "image/jpeg", "image/png", "image/webp", "application/pdf" };
    private const long MaxImageBytes = 5 * 1024 * 1024;
    private const long MaxPdfBytes = 10 * 1024 * 1024;

    [HttpPost("content/{contentId:guid}")]
    [RequestSizeLimit(MaxPdfBytes)]
    public async Task<ActionResult<MediaUploadResponse>> Upload(Guid contentId, IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length == 0 || !AllowedTypes.Contains(file.ContentType))
            return BadRequest(new ProblemDetails { Title = "Only JPEG, PNG, WebP, and PDF files are allowed." });
        var max = file.ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ? MaxPdfBytes : MaxImageBytes;
        if (file.Length > max) return BadRequest(new ProblemDetails { Title = $"The file exceeds the {max / (1024 * 1024)} MB limit." });
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId && x.IsActive, cancellationToken);
        var content = await db.ContentItems.Include(x => x.Audiences).SingleOrDefaultAsync(x => x.Id == contentId, cancellationToken);
        if (user is null) return Unauthorized();
        if (content is null) return NotFound();
        if (user.Role != UserRole.SuperAdmin && content.Audiences.Any(x => x.InstitutionId != user.InstitutionId)) return Forbid();
        var safeName = Path.GetFileName(file.FileName).Replace(" ", "-", StringComparison.Ordinal);
        var key = $"content/{contentId:N}/{Guid.NewGuid():N}-{safeName}";
        await using var stream = file.OpenReadStream();
        await storage.UploadAsync(stream, key, file.ContentType, file.Length, cancellationToken);
        var attachment = new ContentAttachment { ContentItemId = contentId, FileName = safeName, Url = key, ContentType = file.ContentType, SizeBytes = file.Length };
        db.ContentAttachments.Add(attachment);
        await db.SaveChangesAsync(cancellationToken);
        var url = await storage.CreateDownloadUrlAsync(key, TimeSpan.FromMinutes(15), cancellationToken);
        return Created($"/api/v1/media/{attachment.Id}", new MediaUploadResponse(attachment.Id, safeName, file.ContentType, file.Length, url));
    }

    [Authorize]
    [HttpGet("{attachmentId:guid}")]
    public async Task<ActionResult<MediaUploadResponse>> Download(Guid attachmentId, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        var user = await db.Users.AsNoTracking().Include(x => x.FeedPreference).SingleOrDefaultAsync(x => x.Id == userId && x.IsActive, cancellationToken);
        var attachment = await db.ContentAttachments.AsNoTracking().Include(x => x.ContentItem).ThenInclude(x => x.Audiences).SingleOrDefaultAsync(x => x.Id == attachmentId, cancellationToken);
        if (user is null) return Unauthorized();
        if (attachment is null) return NotFound();
        var visible = user.Role == UserRole.SuperAdmin || (attachment.ContentItem.Status == ContentStatus.Published && attachment.ContentItem.Audiences.Any(a => a.InstitutionId == user.InstitutionId && (user.FeedPreference.AllCampusFeed || (a.FacultyId == null || a.FacultyId == user.FacultyId))));
        if (!visible) return NotFound();
        var url = await storage.CreateDownloadUrlAsync(attachment.Url, TimeSpan.FromMinutes(15), cancellationToken);
        return Ok(new MediaUploadResponse(attachment.Id, attachment.FileName, attachment.ContentType, attachment.SizeBytes, url));
    }
}
