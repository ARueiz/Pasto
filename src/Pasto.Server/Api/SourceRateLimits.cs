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

internal sealed class ApiRateBucket
{
    // 一格以週期的 tick 數表示，每個經過的 tick 補 TokensPerPeriod 個單位。
    // 用整數保留未滿一格的餘額，避免在補額邊界受到浮點誤差影響。
    private readonly long unitsPerToken;
    private readonly long capacity;
    private readonly int unitsPerTick;
    private long balance;
    private long lastRefill;
    internal TimeSpan LastUsed { get; private set; }

    internal ApiRateBucket(BucketOptions settings)
    {
        unitsPerToken = TimeSpan.FromSeconds(settings.PeriodSeconds).Ticks;
        unitsPerTick = settings.TokensPerPeriod;
        capacity = unitsPerToken * settings.TokenLimit;
        balance = capacity;
    }

    internal bool IsFull(TimeSpan now)
    {
        Refill(now);
        return balance == capacity;
    }

    internal int? Check(TimeSpan now)
    {
        Refill(now);
        if (balance >= unitsPerToken) return null;
        var deficit = unitsPerToken - balance;
        var unitsPerSecond = unitsPerTick * TimeSpan.TicksPerSecond;
        return (int)(1 + (deficit - 1) / unitsPerSecond);
    }

    internal void Consume(TimeSpan now)
    {
        Refill(now);
        balance -= unitsPerToken;
        LastUsed = now;
    }

    private void Refill(TimeSpan now)
    {
        var elapsed = now.Ticks - lastRefill;
        if (elapsed <= 0) return;
        lastRefill = now.Ticks;
        if (balance == capacity) return;

        // 先判斷是否回滿，避免程序長時間閒置後乘法溢位。
        var missing = capacity - balance;
        balance = elapsed > (missing - 1) / unitsPerTick
            ? capacity
            : balance + elapsed * unitsPerTick;
    }
}
