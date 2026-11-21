using Microsoft.Extensions.Options;

namespace Pasto.Server.Api;

internal enum ParticipantRateProfile { Read, Add, Delete }

internal sealed class ParticipantRateLimits(IOptions<ApiOptions> options, TimeProvider clock, ILogger<ParticipantRateLimits> logger)
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(1);
    private static readonly EventId CapacityReached = new(6104, "ParticipantRateCapacityReached");
    private readonly object gate = new();
    private readonly long origin = clock.GetTimestamp();
    private readonly Dictionary<Guid, Entry> participants = new();
    private TimeSpan nextSweep;
    private TimeSpan nextWarning;
    private long skippedSinceWarning;

    internal int? TryTake(Guid participant, ParticipantRateProfile profile)
    {
        long skipped = 0;
        lock (gate)
        {
            var now = clock.GetElapsedTime(origin);
            if (now >= nextSweep)
            {
                nextSweep = now + MaintenanceInterval;
                foreach (var (id, entry) in participants)
                    if (now - entry.LastUsed >= Idle && entry.Buckets.All(bucket => bucket.IsFull(now))) participants.Remove(id);
            }

            if (!participants.TryGetValue(participant, out var tracked) && participants.Count < options.Value.ParticipantRateCapacity)
                participants.Add(participant, tracked = new Entry(options.Value));
            if (tracked is not null)
            {
                tracked.LastUsed = now;
                var bucket = tracked.Buckets[(int)profile];
                if (bucket.Check(now) is int retry) return retry;
                bucket.Consume(now);
                return null;
            }

            skippedSinceWarning++;
            if (now >= nextWarning)
            {
                nextWarning = now + MaintenanceInterval;
                skipped = skippedSinceWarning;
                skippedSinceWarning = 0;
            }
        }
        if (skipped > 0)
            logger.LogWarning(CapacityReached, "Participant rate table was full. Skipped {SkippedRequestCount} requests since the previous report.", skipped);
        return null;
    }

    private sealed class Entry(ApiOptions settings)
    {
        internal ApiRateBucket[] Buckets { get; } =
            [new(settings.ReadPerParticipant), new(settings.AddPerParticipant), new(settings.DeletePerParticipant)];
        internal TimeSpan LastUsed { get; set; }
    }
}
