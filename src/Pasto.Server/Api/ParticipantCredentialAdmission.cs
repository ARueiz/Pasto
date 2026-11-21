using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;
using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Api;

internal sealed class ParticipantCredentialAdmission(RequestDelegate next, ParticipantCredentialFailures failures)
{
    private static readonly object PreparedKey = new();

    internal static PreparedCredential? Prepared(HttpContext context)
        => (PreparedCredential?)context.Items[PreparedKey];

    public async Task InvokeAsync(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata;
        if (metadata?.GetMetadata<IAuthorizeData>() is not null && metadata.GetMetadata<IAllowAnonymous>() is null &&
            ParticipantAuthenticationHandler.ReadCredential(context) is { } value && ParticipantCredential.HasValidFormat(value))
        {
            var hash = ParticipantCredential.HashValue(value);
            var credential = new PreparedCredential(hash, Convert.ToHexString(hash), CreationRateLimiting.IpKey(context));
            var retryAfter = failures.Check(credential, out var cachedMiss);
            if (retryAfter is int seconds)
            {
                context.Features.Get<IHttpMetricsTagsFeature>()?.Tags.Add(new("pasto.rate_limit.reason", "credential_failure"));
                failures.ReportRejection();
                await CreationRateLimiting.RejectAsync(context, seconds, "CredentialFailureRateLimited");
                return;
            }
            // Cache hits reach the normal 401 challenge without using the DB floor or verifier.
            if (!cachedMiss) context.Items[PreparedKey] = credential;
        }
        await next(context);
    }
}

// Holds only the digest and source for the duration of an admitted request.
internal sealed class PreparedCredential(byte[] hash, string cacheKey, string source)
{
    internal byte[] Hash { get; } = hash;
    internal string CacheKey { get; } = cacheKey;
    internal string Source { get; } = source;
}

internal sealed class ParticipantCredentialFailures(IOptions<ApiOptions> options, TimeProvider clock,
    ILogger<ParticipantCredentialFailures> logger, int cacheCapacity = 10_000, int sourceCapacity = 20_000)
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SourceIdle = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(1);
    private static readonly EventId Throttled = new(6100, "ParticipantCredentialFailuresThrottled");
    private static readonly EventId SourceCapacityReached = new(6102, "ParticipantCredentialFailureSourceCapacityReached");
    private readonly object gate = new();
    private readonly HashSet<string> misses = [];
    private readonly Queue<(string Key, long Time)> missOrder = new();
    private readonly Dictionary<string, FailureBucket> sources = new();
    private long? lastSweep;
    private long? lastRejectionReport;
    private long rejectedSinceReport;
    private long? lastCapacityReport;
    private long skippedMissesSinceReport;

    private BucketOptions Settings => options.Value.CredentialFailuresPerSource;

    internal int? Check(PreparedCredential credential, out bool cachedMiss)
    {
        lock (gate)
        {
            var now = Maintain();
            cachedMiss = misses.Contains(credential.CacheKey);
            if (cachedMiss) return null;

            // Unknown sources proceed to the DB floor, including when the source table is full.
            if (!sources.TryGetValue(credential.Source, out var bucket)) return null;
            bucket.Refill(now, clock, Settings);
            return bucket.Tokens < 1 ? Math.Max(1, (int)Math.Ceiling(
                (1 - bucket.Tokens) * Settings.PeriodSeconds / Settings.TokensPerPeriod)) : null;
        }
    }

    internal void RecordMiss(PreparedCredential credential)
    {
        long skippedCount = 0;
        lock (gate)
        {
            var now = Maintain();
            // Concurrent queries can race, but a cached digest is only charged once.
            if (misses.Contains(credential.CacheKey)) return;
            if (missOrder.Count == cacheCapacity)
                misses.Remove(missOrder.Dequeue().Key);
            missOrder.Enqueue((credential.CacheKey, now));
            misses.Add(credential.CacheKey);

            if (!sources.TryGetValue(credential.Source, out var bucket) && sources.Count < sourceCapacity)
                sources.Add(credential.Source, bucket = new FailureBucket(Settings.TokenLimit, now));
            if (bucket is not null)
            {
                bucket.Refill(now, clock, Settings);
                bucket.Charge(now);
            }
            else
                skippedCount = CountForReport(now, ref lastCapacityReport, ref skippedMissesSinceReport);
        }
        if (skippedCount > 0)
            logger.LogWarning(SourceCapacityReached,
                "Credential failure source table was full. Skipped recording {SkippedMissCount} misses since the previous report.", skippedCount);
    }

    internal void ReportRejection()
    {
        long count;
        lock (gate)
            count = CountForReport(clock.GetTimestamp(), ref lastRejectionReport, ref rejectedSinceReport);
        if (count > 0)
            logger.LogWarning(Throttled, "Credential failure admission rejected {RejectedCount} requests since the previous report.", count);
    }

    // Call under gate so events of the same kind share one reporting interval per process.
    private long CountForReport(long now, ref long? lastReport, ref long pendingCount)
    {
        pendingCount++;
        if (lastReport is long timestamp && clock.GetElapsedTime(timestamp, now) < MaintenanceInterval) return 0;
        lastReport = now;
        var count = pendingCount;
        pendingCount = 0;
        return count;
    }

    private long Maintain()
    {
        var now = clock.GetTimestamp();
        while (missOrder.TryPeek(out var miss) && clock.GetElapsedTime(miss.Time, now) >= CacheLifetime)
            misses.Remove(missOrder.Dequeue().Key);
        if (lastSweep is long timestamp && clock.GetElapsedTime(timestamp, now) < MaintenanceInterval) return now;
        lastSweep = now;
        // A sweep only evicts refilled buckets, so eviction cannot grant extra failure quota.
        foreach (var (source, bucket) in sources)
        {
            if (clock.GetElapsedTime(bucket.LastMiss, now) < SourceIdle) continue;
            bucket.Refill(now, clock, Settings);
            if (bucket.Tokens == Settings.TokenLimit) sources.Remove(source);
        }
        return now;
    }

    // QueueLimit = 0: the precheck returns immediately and a completed miss consumes one available token.
    private sealed class FailureBucket(int capacity, long createdAt)
    {
        private long updatedAt = createdAt;
        internal long LastMiss { get; private set; } = createdAt;
        internal double Tokens { get; private set; } = capacity;

        internal void Refill(long now, TimeProvider clock, BucketOptions settings)
        {
            var elapsed = clock.GetElapsedTime(updatedAt, now).TotalSeconds;
            Tokens = Math.Min(settings.TokenLimit, Tokens + elapsed * settings.TokensPerPeriod / settings.PeriodSeconds);
            updatedAt = now;
        }

        internal void Charge(long now)
        {
            if (Tokens >= 1) Tokens--;
            LastMiss = now;
        }
    }
}
