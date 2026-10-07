using System.ComponentModel.DataAnnotations;
using CampusUpdate.Domain.Notifications;
namespace CampusUpdate.Api.Contracts;

public sealed record RegisterDeviceRequest([Required, MaxLength(4096)] string Token, PushPlatform Platform);
public sealed record NotificationResponse(Guid Id, Guid? ContentItemId, string Title, string Body, bool IsRead, DateTimeOffset CreatedAt, DateTimeOffset? DeliveredAt);
public sealed record NotificationAcknowledgement(string State);
