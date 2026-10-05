using CampusUpdate.Domain.Notifications;
using CampusUpdate.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace CampusUpdate.Api.Notifications;

// Events are persisted with the originating operation; aggregation can safely resume after a restart.
public sealed class UsageAggregator(CampusUpdateDbContext db)
{
    public async Task<int> ProcessAsync(CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
        var events = await db.UserActivities.Where(a => !a.Aggregated && a.InstitutionId != null)
            .OrderBy(a => a.OccurredAt).Take(2000).ToListAsync(ct);
        foreach (var group in events.GroupBy(a => new { InstitutionId = a.InstitutionId!.Value, Day = new DateTimeOffset(a.OccurredAt.UtcDateTime.Date, TimeSpan.Zero) }))
        {
            var counter = await db.DailyUsageCounters.SingleOrDefaultAsync(c => c.InstitutionId == group.Key.InstitutionId && c.Day == group.Key.Day, ct);
            if (counter is null)
            {
                counter = new DailyUsageCounter { InstitutionId = group.Key.InstitutionId, Day = group.Key.Day };
                db.DailyUsageCounters.Add(counter);
            }
            counter.Registrations += group.Count(a => a.ActivityType == "registration");
            counter.Sessions += group.Count(a => a.ActivityType is "login" or "session_refresh" or "feed_fetch");
            counter.FeedFetches += group.Count(a => a.ActivityType == "feed_fetch");
            foreach (var activity in group) activity.Aggregated = true;
        }
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return events.Count;
    }
}
