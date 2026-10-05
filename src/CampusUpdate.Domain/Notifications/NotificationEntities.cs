using CampusUpdate.Domain.Common;
using CampusUpdate.Domain.Users;

namespace CampusUpdate.Domain.Notifications;

public enum PushPlatform { Android, Ios, Web }
public enum PushDeliveryStatus { Pending, Sent, Failed, NotRequired }

public sealed class DeviceInstallation : Entity
{
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public required string Token { get; set; }
    public PushPlatform Platform { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class UserNotification : Entity
{
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public Guid? ContentItemId { get; set; }
    public string Title { get; set; } = "";
    public string Body { get; set; } = "";
    public string DataJson { get; set; } = "{}";
    public bool IsRead { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset? OpenedAt { get; set; }
    public PushDeliveryStatus DeliveryStatus { get; set; } = PushDeliveryStatus.Pending;
    public int DeliveryAttempts { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
}

public sealed class NotificationDelivery : Entity
{
    public Guid NotificationId { get; set; }
    public UserNotification Notification { get; set; } = null!;
    public Guid DeviceInstallationId { get; set; }
    public DeviceInstallation DeviceInstallation { get; set; } = null!;
    public PushDeliveryStatus Status { get; set; } = PushDeliveryStatus.Pending;
    public int Attempts { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
}

public sealed class ContentView : Entity
{
    public Guid UserId { get; set; }
    public Guid ContentItemId { get; set; }
    public Guid InstitutionId { get; set; }
    public DateTimeOffset FirstViewedAt { get; set; }
}

public sealed class UserActivity : Entity
{
    public bool Aggregated { get; set; }
    public Guid? UserId { get; set; }
    public Guid? InstitutionId { get; set; }
    public required string ActivityType { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class DailyUsageCounter : Entity
{
    public Guid InstitutionId { get; set; }
    public DateTimeOffset Day { get; set; }
    public long Registrations { get; set; }
    public long Sessions { get; set; }
    public long FeedFetches { get; set; }
}
