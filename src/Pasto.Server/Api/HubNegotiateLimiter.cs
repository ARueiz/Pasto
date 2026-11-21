using Microsoft.Extensions.Options;

namespace Pasto.Server.Api;

internal sealed class HubNegotiateLimiter(TimeProvider clock, IOptions<HubAdmissionOptions> options)
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);
    private readonly long origin = clock.GetTimestamp();
    private readonly object gate = new();
    private readonly HubTokenBucket global = new(200, 100);
    private readonly Dictionary<string, HubTokenBucket> sources = new();
    private TimeSpan nextSweep;

    internal int? Check(string source)
    {
        lock (gate)
        {
            var now = clock.GetElapsedTime(origin);
            if (now >= nextSweep)
            {
                nextSweep = now + SweepInterval;
                foreach (var (key, bucket) in sources)
                    if (now - bucket.LastUsed >= Idle) sources.Remove(key);
            }

            if (!global.CanConsume(now)) return 1;
            if (sources.TryGetValue(source, out var perSource))
            {
                if (!perSource.CanConsume(now)) return 1;
            }
            else if (sources.Count < options.Value.NegotiateSourceCapacity)
                sources.Add(source, perSource = new HubTokenBucket(100, 50));

            global.Consume(now);
            perSource?.Consume(now);
            return null;
        }
    }
}

// 呼叫端在單一鎖內檢查並扣額，避免部分扣費。
internal sealed class HubTokenBucket(int burst, double tokensPerSecond)
{
    private double tokens = burst;
    internal TimeSpan LastUsed { get; private set; }

    internal bool CanConsume(TimeSpan now) => Available(now) >= 1;

    internal void Consume(TimeSpan now)
    {
        tokens = Available(now) - 1;
        LastUsed = now;
    }

    private double Available(TimeSpan now) => Math.Min(burst, tokens + (now - LastUsed).TotalSeconds * tokensPerSecond);
}
