using CampusUpdate.Domain.Content;
using CampusUpdate.Domain.Notifications;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace CampusUpdate.Api.Controllers;

[ApiController, Authorize(Roles = nameof(UserRole.SuperAdmin)), Route("api/v1/admin/analytics")]
public sealed class AnalyticsController(CampusUpdateDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Summary([FromQuery] int days = 30, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365); var since = DateTimeOffset.UtcNow.AddDays(-days);
        var today = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);
        var institutions = await db.Institutions.AsNoTracking().Select(i => new
        {
            i.Id,
            i.Name,
            TotalRegisteredUsers = db.Users.Count(u => u.InstitutionId == i.Id),
            Dau = db.UserActivities.Where(a => a.InstitutionId == i.Id && a.UserId != null && a.OccurredAt >= today).Select(a => a.UserId).Distinct().Count(),
            ActivePosts = db.ContentItems.Count(c => c.Status == ContentStatus.Published && c.Audiences.Any(a => a.InstitutionId == i.Id)),
            Users = db.Users.Count(u => u.InstitutionId == i.Id && u.IsActive),
            NewUsers = db.Users.Count(u => u.InstitutionId == i.Id && u.CreatedAt >= since),
            LoginEvents = db.UserActivities.Count(a => a.InstitutionId == i.Id && a.ActivityType == "login" && a.OccurredAt >= since),
            PublishedContent = db.ContentItems.Count(c => c.Status == ContentStatus.Published && c.PublishedAt >= since && c.Audiences.Any(a => a.InstitutionId == i.Id)),
            UniqueContentViews = db.ContentViews.Count(v => v.InstitutionId == i.Id && v.FirstViewedAt >= since)
        }).ToListAsync(ct);
        var result = new
        {
            Since = since,
            Totals = new { TotalRegisteredUsers = await db.Users.CountAsync(ct), Dau = await db.UserActivities.Where(a => a.UserId != null && a.OccurredAt >= today).Select(a => a.UserId).Distinct().CountAsync(ct), ActivePosts = await db.ContentItems.CountAsync(c => c.Status == ContentStatus.Published, ct), NotificationsDelivered = await db.UserNotifications.CountAsync(n => n.DeliveredAt >= since, ct), NotificationsOpened = await db.UserNotifications.CountAsync(n => n.OpenedAt >= since, ct), ActiveUsers = await db.Users.CountAsync(u => u.IsActive, ct), NewUsers = await db.Users.CountAsync(u => u.CreatedAt >= since, ct), LoginEvents = await db.UserActivities.CountAsync(a => a.ActivityType == "login" && a.OccurredAt >= since, ct), PublishedContent = await db.ContentItems.CountAsync(c => c.Status == ContentStatus.Published && c.PublishedAt >= since, ct), UniqueContentViews = await db.ContentViews.CountAsync(v => v.FirstViewedAt >= since, ct), Notifications = await db.UserNotifications.CountAsync(n => n.CreatedAt >= since, ct), PushFailures = await db.NotificationDeliveries.CountAsync(d => d.Status == PushDeliveryStatus.Failed && d.CreatedAt >= since, ct) },
            UsageCounters = await db.DailyUsageCounters.AsNoTracking().Where(c => c.Day >= since).ToListAsync(ct),
            Institutions = institutions
        };
        return Ok(result);
    }
}

[ApiController, Authorize(Roles = nameof(UserRole.SuperAdmin)), Route("api/v1/admin/notifications")]
public sealed class NotificationOperationsController(CampusUpdate.Api.Notifications.NotificationDispatcher dispatcher, CampusUpdate.Api.Notifications.UsageAggregator usage, IConfiguration configuration) : ControllerBase
{
    [HttpPost("retry")]
    [Authorize(Roles = nameof(UserRole.SuperAdmin))]
    public async Task<IActionResult> Retry([FromQuery] int limit = 100, CancellationToken ct = default) => Ok(new { Processed = await dispatcher.RetryPendingAsync(Math.Clamp(limit, 1, 500), ct) });

    [HttpGet("/api/v1/internal/notifications/retry")]
    [AllowAnonymous]
    public async Task<IActionResult> ScheduledRetry(CancellationToken ct)
    {
        var secret = configuration["CRON_SECRET"];
        var supplied = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(secret) || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(supplied), System.Text.Encoding.UTF8.GetBytes($"Bearer {secret}")))
            return Unauthorized();
        var aggregated = await usage.ProcessAsync(ct);
        var recovered = await dispatcher.ProcessUnqueuedPublicationsAsync(500, ct);
        var retried = await dispatcher.RetryPendingAsync(500, ct);
        return Ok(new { AggregatedActivityEvents = aggregated, RecoveredPublications = recovered, RetriedDeliveries = retried });
    }
}
