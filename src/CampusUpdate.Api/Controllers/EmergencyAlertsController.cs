using System.Security.Claims;
using System.Text.Json;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Api.Notifications;
using CampusUpdate.Domain.Content;
using CampusUpdate.Domain.Notifications;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Api.Controllers;

[ApiController]
[Authorize(Roles = "LeadSchoolAdmin,SuperAdmin")]
[Route("api/v1/emergency-alerts")]
public sealed class EmergencyAlertsController(
    CampusUpdateDbContext db,
    NotificationDispatcher notifications) : ControllerBase
{
    [HttpPost]
    public async Task<ActionResult<ContentResponse>> Create(
        CreateEmergencyAlertRequest request,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Unauthorized();
        var sender = await db.Users.AsNoTracking().Include(x => x.Institution)
            .SingleOrDefaultAsync(x => x.Id == userId && x.IsActive, cancellationToken);
        if (sender is null) return Unauthorized();
        if (!sender.Institution.IsActive)
            return BadRequest(new ProblemDetails { Title = "The institution is not active." });
        if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Body))
            return BadRequest(new ProblemDetails { Title = "An alert title and message are required." });

        var now = DateTimeOffset.UtcNow;
        var item = new ContentItem
        {
            Title = request.Title.Trim(),
            Body = request.Body.Trim(),
            Summary = request.Summary?.Trim(),
            Category = "Emergency Alert",
            Type = ContentType.Announcement,
            Status = ContentStatus.Published,
            Urgency = UrgencyLevel.Urgent,
            SourceType = SourceType.Official,
            SourceName = sender.Institution.Name,
            AuthorId = sender.Id,
            IsEmergencyAlert = true,
            PublishedAt = now,
            Audiences =
            [
                new ContentAudience
                {
                    InstitutionId = sender.InstitutionId,
                    TargetAudience = TargetAudience.Students
                },
                new ContentAudience
                {
                    InstitutionId = sender.InstitutionId,
                    TargetAudience = TargetAudience.Staff
                }
            ]
        };
        db.ContentItems.Add(item);
        db.AuditLogs.Add(new AuditLog
        {
            ActorId = sender.Id,
            Action = "EmergencyAlertPublished",
            TargetId = item.Id,
            InstitutionId = sender.InstitutionId,
            Details = JsonSerializer.Serialize(new
            {
                targetType = "Content",
                actionTag = "Emergency alert",
                item.Title,
                audience = "Active students and staff in the sender's institution"
            })
        });
        await db.SaveChangesAsync(cancellationToken);

        await notifications.QueuePublishedContentAsync(item, cancellationToken);

        var response = new ContentResponse(
            item.Id, item.Title, item.Body, item.Type, item.Status, item.Urgency, item.SourceType,
            item.SourceName, item.PublishedAt, item.Category, CreatedAt: item.CreatedAt, UpdatedAt: item.UpdatedAt,
            IsEmergencyAlert: true);
        return Created($"/api/v1/content/{item.Id}", response);
    }
}
