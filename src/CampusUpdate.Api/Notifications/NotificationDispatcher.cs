using System.Text.Json;
using CampusUpdate.Domain.Content;
using CampusUpdate.Domain.Notifications;
using CampusUpdate.Domain.Users;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Api.Notifications;

public sealed class NotificationDispatcher(CampusUpdateDbContext db, FirebasePushSender pushSender)
{
    public async Task QueuePublishedContentAsync(ContentItem item, CancellationToken ct)
    {
        var users = await db.Users.Include(x => x.FeedPreference).Where(user =>
            user.IsActive &&
            (item.Type == ContentType.News ? user.FeedPreference.NewsEnabled :
             item.Type == ContentType.Announcement ? user.FeedPreference.AnnouncementsEnabled :
             item.Type == ContentType.Event ? user.FeedPreference.EventsEnabled : user.FeedPreference.AdvertisementsEnabled) &&
            db.ContentAudiences.Any(a => a.ContentItemId == item.Id && a.InstitutionId == user.InstitutionId &&
                (a.TargetAudience == TargetAudience.All || (a.TargetAudience == TargetAudience.Students && user.Role == UserRole.Student) || (a.TargetAudience == TargetAudience.Staff && user.Role == UserRole.Staff)) &&
                (a.FacultyId == null || a.FacultyId == user.FacultyId) &&
                (a.DepartmentId == null || a.DepartmentId == user.DepartmentId) &&
                (a.ProgrammeId == null || a.ProgrammeId == user.ProgrammeId) &&
                (a.AcademicLevelId == null || a.AcademicLevelId == user.AcademicLevelId)))
            .ToListAsync(ct);
        foreach (var user in users)
        {
            if (await db.UserNotifications.AnyAsync(n => n.UserId == user.Id && n.ContentItemId == item.Id, ct)) continue;
            db.UserNotifications.Add(new UserNotification
            {
                UserId = user.Id,
                ContentItemId = item.Id,
                Title = item.Title,
                Body = (item.Summary ?? item.Body)[..Math.Min(240, (item.Summary ?? item.Body).Length)],
                DataJson = JsonSerializer.Serialize(new Dictionary<string, string> { ["notificationId"] = "", ["contentId"] = item.Id.ToString(), ["type"] = item.Type.ToString() })
            });
        }
        item.NotificationsEnqueuedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var pending = await db.UserNotifications.Where(n => n.ContentItemId == item.Id && n.DeliveryStatus == PushDeliveryStatus.Pending).ToListAsync(ct);
        foreach (var notification in pending) await DeliverAsync(notification, ct);
    }

    public async Task<int> RetryPendingAsync(int limit, CancellationToken ct)
    {
        var retryAfter = DateTimeOffset.UtcNow.AddMinutes(-5);
        var pending = await db.UserNotifications.Where(n =>
                (n.DeliveryStatus == PushDeliveryStatus.Pending || n.DeliveryStatus == PushDeliveryStatus.Failed) &&
                n.DeliveryAttempts < 10 && (n.LastAttemptAt == null || n.LastAttemptAt <= retryAfter))
            .OrderBy(n => n.LastAttemptAt).Take(limit).ToListAsync(ct);
        foreach (var notification in pending) await DeliverAsync(notification, ct);
        return pending.Count;
    }

    public async Task<int> ProcessUnqueuedPublicationsAsync(int limit, CancellationToken ct)
    {
        var items = await db.ContentItems.Include(x => x.Audiences)
            .Where(x => x.Status == ContentStatus.Published && x.NotificationsEnqueuedAt == null)
            .OrderBy(x => x.PublishedAt).Take(Math.Clamp(limit, 1, 500)).ToListAsync(ct);
        foreach (var item in items) await QueuePublishedContentAsync(item, ct);
        return items.Count;
    }

    private async Task DeliverAsync(UserNotification notification, CancellationToken ct)
    {
        var wantsPush = await db.FeedPreferences.Where(p => p.UserId == notification.UserId)
            .Select(p => p.PushNotificationsEnabled).SingleOrDefaultAsync(ct);
        if (!wantsPush)
        {
            notification.DeliveryStatus = PushDeliveryStatus.NotRequired;
            await db.SaveChangesAsync(ct);
            return;
        }
        if (!pushSender.IsConfigured)
        {
            notification.DeliveryStatus = PushDeliveryStatus.Pending;
            await db.SaveChangesAsync(ct);
            return;
        }

        var devices = await db.DeviceInstallations.Where(d => d.UserId == notification.UserId && d.IsActive).ToListAsync(ct);
        if (devices.Count == 0)
        {
            notification.DeliveryStatus = PushDeliveryStatus.Pending;
            await db.SaveChangesAsync(ct);
            return;
        }

        foreach (var device in devices)
        {
            if (!await db.NotificationDeliveries.AnyAsync(d => d.NotificationId == notification.Id && d.DeviceInstallationId == device.Id, ct))
                db.NotificationDeliveries.Add(new NotificationDelivery { NotificationId = notification.Id, DeviceInstallationId = device.Id });
        }
        await db.SaveChangesAsync(ct);

        var retryAfter = DateTimeOffset.UtcNow.AddMinutes(-5);
        var deliveries = await db.NotificationDeliveries
            .Include(x => x.DeviceInstallation)
            .Where(x => x.NotificationId == notification.Id && x.Status != PushDeliveryStatus.Sent &&
                x.Attempts < 10 && x.DeviceInstallation.IsActive && (x.LastAttemptAt == null || x.LastAttemptAt <= retryAfter))
            .ToListAsync(ct);
        var data = JsonSerializer.Deserialize<Dictionary<string, string>>(notification.DataJson) ?? [];
        data["notificationId"] = notification.Id.ToString();
        foreach (var delivery in deliveries)
        {
            delivery.Attempts++;
            delivery.LastAttemptAt = DateTimeOffset.UtcNow;
            delivery.Status = await pushSender.SendAsync(delivery.DeviceInstallation.Token, notification.Title, notification.Body, data, ct)
                ? PushDeliveryStatus.Sent
                : PushDeliveryStatus.Failed;

        }
        notification.DeliveryAttempts++;
        notification.LastAttemptAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var statuses = await db.NotificationDeliveries.Where(x => x.NotificationId == notification.Id && x.DeviceInstallation.IsActive)
            .Select(x => x.Status).ToListAsync(ct);
        if (statuses.Count > 0 && statuses.All(x => x == PushDeliveryStatus.Sent))
        {
            notification.DeliveryStatus = PushDeliveryStatus.Sent;

        }
        else if (statuses.Any(x => x == PushDeliveryStatus.Failed)) notification.DeliveryStatus = PushDeliveryStatus.Failed;
        else notification.DeliveryStatus = PushDeliveryStatus.Pending;
        await db.SaveChangesAsync(ct);
    }
}
