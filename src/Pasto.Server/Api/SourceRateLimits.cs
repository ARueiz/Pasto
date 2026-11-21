using Microsoft.Extensions.Options;

namespace Pasto.Server.Api;

internal enum SourceRateProfile { Create, Join, Database }

internal sealed class SourceRateLimits(IOptions<ApiOptions> options, TimeProvider clock)
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);
    private readonly object gate = new();
    private readonly long origin = clock.GetTimestamp();
    private readonly Dictionary<(SourceRateProfile Profile, string Source), ApiRateBucket> sources = new();
    private readonly BucketOptions[] perSource = [options.Value.CreatePerIp, options.Value.JoinPerIp, options.Value.ReadPerIp];
    private readonly ApiRateBucket[] global =
        [new(options.Value.CreateGlobal), new(options.Value.JoinGlobal), new(options.Value.ReadGlobal)];
    private TimeSpan nextSweep;

    internal int? TryTake(SourceRateProfile profile, string source)
    {
        lock (gate)
        {
            var now = clock.GetElapsedTime(origin);
            if (now >= nextSweep)
            {
                nextSweep = now + TimeSpan.FromMinutes(1);
                foreach (var (key, bucket) in sources)
                    if (now - bucket.LastUsed >= Idle && bucket.IsFull(now)) sources.Remove(key);
            }

            var shared = global[(int)profile];
            if (shared.Check(now) is int globalRetry) return globalRetry;
            var partition = (profile, source);
            if (sources.TryGetValue(partition, out var bucketForSource))
            {
                if (bucketForSource.Check(now) is int sourceRetry) return sourceRetry;
            }
            else if (sources.Count < options.Value.SourceRateCapacity)
                sources.Add(partition, bucketForSource = new ApiRateBucket(perSource[(int)profile]));

            shared.Consume(now);
            bucketForSource?.Consume(now);
            return null;
        }
    }

    internal static Task CheckDatabaseAsync(HttpContext context, RequestDelegate next)
    {
        if (ParticipantCredentialAdmission.Prepared(context) is not null &&
            context.RequestServices.GetRequiredService<SourceRateLimits>()
                .TryTake(SourceRateProfile.Database, CreationRateLimiting.IpKey(context)) is int retry)
            return CreationRateLimiting.RejectAsync(context, retry, "RateLimited");
        return next(context);
    }
}

// Refill in fixed periods, retaining each existing bucket's configured rate and burst.
internal sealed class ApiRateBucket(BucketOptions settings)
{
    private readonly long period = TimeSpan.FromSeconds(settings.PeriodSeconds).Ticks;
    private int tokens = settings.TokenLimit;
    private long? lastRefill;
    internal TimeSpan LastUsed { get; private set; }

    internal bool IsFull(TimeSpan now)
    {
        Refill(now);
        return tokens == settings.TokenLimit;
    }

    internal int? Check(TimeSpan now)
    {
        Refill(now);
        return tokens > 0 ? null : (int)Math.Ceiling(TimeSpan.FromTicks(period - (now.Ticks - lastRefill!.Value)).TotalSeconds);
    }

    internal void Consume(TimeSpan now)
    {
        lastRefill ??= now.Ticks;
        tokens--;
        LastUsed = now;
    }

    private void Refill(TimeSpan now)
    {
        if (lastRefill is not long previous) return;
        var elapsedPeriods = (now.Ticks - previous) / period;
        if (elapsedPeriods == 0) return;
        tokens = (int)Math.Min(settings.TokenLimit, tokens + elapsedPeriods * settings.TokensPerPeriod);
        lastRefill = previous + elapsedPeriods * period;
    }
}
