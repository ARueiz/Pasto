namespace Pasto.Server.Api;

internal sealed class JoinPressureLimiter(TimeProvider clock)
{
    private const int Threshold = 300;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly long origin = clock.GetTimestamp();
    private readonly object gate = new();
    // The most recent 300 timestamps are sufficient to test the rolling threshold.
    private readonly Queue<TimeSpan> misses = new(Threshold);
    private TimeSpan pressureUntil;
    private TimeSpan? recoveryStarted;
    private TimeSpan lastRefill;
    private double tokens;

    internal void RecordLookupMiss()
    {
        lock (gate)
        {
            var now = clock.GetElapsedTime(origin);
            while (misses.TryPeek(out var oldest) && now - oldest >= Window)
                misses.Dequeue();
            if (misses.Count == Threshold) misses.Dequeue();
            misses.Enqueue(now);
            if (misses.Count != Threshold) return;

            if (now >= pressureUntil)
            {
                tokens = 5;
                lastRefill = now;
                recoveryStarted = null;
                pressureUntil = now + Window;
            }
            // While the window is still above threshold, defer recovery until a miss ages out.
            var belowThresholdAt = misses.Peek() + Window;
            if (belowThresholdAt > pressureUntil) pressureUntil = belowThresholdAt;
        }
    }

    internal int? Check()
    {
        lock (gate)
        {
            var now = clock.GetElapsedTime(origin);
            if (pressureUntil == TimeSpan.Zero || now >= pressureUntil + Window) return null;

            var recovering = now >= pressureUntil;
            var rate = recovering ? 10 : 2;
            var burst = recovering ? 20 : 5;
            if (recovering && recoveryStarted != pressureUntil)
            {
                recoveryStarted = pressureUntil;
                tokens = burst;
                lastRefill = now;
            }

            tokens = Math.Min(burst, tokens + (now - lastRefill).TotalSeconds * rate);
            lastRefill = now;
            if (tokens < 1) return 1;
            tokens--;
            return null;
        }
    }
}
