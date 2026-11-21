using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Infrastructure.Persistence;

internal sealed class RoomJoinQuota(TimeProvider clock)
{
    private const int Burst = 30;
    private static readonly TimeSpan TokenInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BurstAllowance = TokenInterval * (Burst - 1);
    private static readonly TimeSpan SweepInterval = TokenInterval * Burst;
    private readonly long origin = clock.GetTimestamp();
    private readonly object gate = new();
    private readonly Dictionary<Guid, TimeSpan> refilledAt = new();
    private TimeSpan nextSweep;

    // Both calls for a room run under its PostgreSQL row lock, across the participant insert.
    internal void EnsureAvailable(Guid roomId)
    {
        lock (gate)
        {
            var now = clock.GetElapsedTime(origin);
            if (!refilledAt.TryGetValue(roomId, out var deadline)) return;
            var wait = deadline - BurstAllowance - now;
            if (wait > TimeSpan.Zero) throw new RoomJoinRateLimitedException((int)Math.Ceiling(wait.TotalSeconds));
        }
    }

    internal void Charge(Guid roomId)
    {
        lock (gate)
        {
            var now = clock.GetElapsedTime(origin);
            if (now >= nextSweep)
            {
                nextSweep = now + SweepInterval;
                foreach (var (id, deadline) in refilledAt)
                    if (deadline <= now) refilledAt.Remove(id);
            }
            // Each join adds two seconds to the time needed to return the bucket to full.
            refilledAt.TryGetValue(roomId, out var previous);
            refilledAt[roomId] = (previous > now ? previous : now) + TokenInterval;
        }
    }
}
