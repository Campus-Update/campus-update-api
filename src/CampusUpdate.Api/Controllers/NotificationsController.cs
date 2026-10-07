using System.Security.Claims;
using CampusUpdate.Api.Contracts;
using CampusUpdate.Domain.Notifications;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
namespace CampusUpdate.Api.Controllers;

[ApiController, Authorize]
public sealed class NotificationsController(CampusUpdateDbContext db) : ControllerBase
{
    private Guid UserId => Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : Guid.Empty;

    [HttpPost("api/v1/devices")]
    public async Task<IActionResult> RegisterDevice(RegisterDeviceRequest request, CancellationToken ct)
    {
        var token = request.Token.Trim();
        if (token.Length == 0 || !Enum.IsDefined(request.Platform)) return BadRequest(new ProblemDetails { Title = "A non-empty token and valid platform are required." });
        var device = await db.DeviceInstallations.SingleOrDefaultAsync(x => x.Token == token, ct);
        if (device is null) { device = new DeviceInstallation { UserId = UserId, Token = token, Platform = request.Platform, LastSeenAt = DateTimeOffset.UtcNow }; db.DeviceInstallations.Add(device); }
        else { device.UserId = UserId; device.Platform = request.Platform; device.IsActive = true; device.LastSeenAt = DateTimeOffset.UtcNow; }
        await db.SaveChangesAsync(ct); return Ok(new { device.Id, device.Platform, device.IsActive });
    }

    [HttpDelete("api/v1/devices/{id:guid}")]
    public async Task<IActionResult> RemoveDevice(Guid id, CancellationToken ct)
    {
        var device = await db.DeviceInstallations.SingleOrDefaultAsync(x => x.Id == id && x.UserId == UserId, ct);
        if (device is null) return NotFound(); device.IsActive = false; await db.SaveChangesAsync(ct); return NoContent();
    }

    [HttpGet("api/v1/notifications")]
    public async Task<ActionResult<IReadOnlyCollection<NotificationResponse>>> GetNotifications([FromQuery] int page = 1, [FromQuery] int pageSize = 30, CancellationToken ct = default)
    {
        page = Math.Max(page, 1); pageSize = Math.Clamp(pageSize, 1, 100);
        return Ok(await db.UserNotifications.AsNoTracking().Where(x => x.UserId == UserId).OrderByDescending(x => x.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new NotificationResponse(x.Id, x.ContentItemId, x.Title, x.Body, x.IsRead, x.CreatedAt, x.DeliveredAt)).ToListAsync(ct));
    }

    [HttpPatch("api/v1/notifications/{id:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid id, CancellationToken ct)
    {
        var notification = await db.UserNotifications.SingleOrDefaultAsync(x => x.Id == id && x.UserId == UserId, ct);
        if (notification is null) return NotFound(); notification.IsRead = true; notification.ReadAt ??= DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); return NoContent();
    }

    [HttpPost("api/v1/notifications/{id:guid}/ack")]
    public async Task<IActionResult> Acknowledge(Guid id, NotificationAcknowledgement request, CancellationToken ct)
    {
        var notification = await db.UserNotifications.SingleOrDefaultAsync(x => x.Id == id && x.UserId == UserId, ct);
        if (notification is null) return NotFound();
        if (string.Equals(request.State, "delivered", StringComparison.OrdinalIgnoreCase)) notification.DeliveredAt ??= DateTimeOffset.UtcNow;
        else if (string.Equals(request.State, "opened", StringComparison.OrdinalIgnoreCase)) { notification.OpenedAt ??= DateTimeOffset.UtcNow; notification.DeliveredAt ??= notification.OpenedAt; notification.ReadAt ??= notification.OpenedAt; notification.IsRead = true; }
        else return BadRequest(new ProblemDetails { Title = "State must be delivered or opened." });
        await db.SaveChangesAsync(ct); return NoContent();
    }
}
