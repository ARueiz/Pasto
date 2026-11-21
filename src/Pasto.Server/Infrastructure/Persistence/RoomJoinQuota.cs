using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Infrastructure.Persistence;

internal sealed class RoomJoinQuota(TimeProvider clock) : RoomMutationQuota(clock, 30, TimeSpan.FromSeconds(2))
{
    internal void EnsureAvailable(Guid roomId)
    {
        if (RetryAfter(roomId) is int seconds) throw new RoomJoinRateLimitedException(seconds);
    }
}

internal sealed class RoomAddQuota(TimeProvider clock) : RoomMutationQuota(clock, 20, TimeSpan.FromMilliseconds(500));

internal abstract class RoomMutationQuota(TimeProvider clock, int burst, TimeSpan tokenInterval)
{
    private readonly TimeSpan burstAllowance = tokenInterval * (burst - 1);
    private readonly TimeSpan sweepInterval = tokenInterval * burst;
    private readonly long origin = clock.GetTimestamp();
    private readonly object gate = new();
    private readonly Dictionary<Guid, TimeSpan> refilledAt = new();
    private TimeSpan nextSweep;

    // 檢查與扣額均在同一個 PostgreSQL 房間列鎖內。
    internal int? RetryAfter(Guid roomId)
    {
        lock (gate)
        {
            var now = clock.GetElapsedTime(origin);
            if (!refilledAt.TryGetValue(roomId, out var deadline)) return null;
            var wait = deadline - burstAllowance - now;
            return wait > TimeSpan.Zero ? (int)Math.Ceiling(wait.TotalSeconds) : null;
        }
    }

    internal void Charge(Guid roomId)
    {
        lock (gate)
        {
            var now = clock.GetElapsedTime(origin);
            if (now >= nextSweep)
            {
                nextSweep = now + sweepInterval;
                foreach (var (id, deadline) in refilledAt)
                    if (deadline <= now) refilledAt.Remove(id);
            }
            // 每次寫入延後桶重新回滿的時間。
            refilledAt.TryGetValue(roomId, out var previous);
            refilledAt[roomId] = (previous > now ? previous : now) + tokenInterval;
        }
    }
}
