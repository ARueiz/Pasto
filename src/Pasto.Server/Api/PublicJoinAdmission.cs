using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Http.Features;
using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Api;

internal sealed class PublicJoinAdmission(RequestDelegate next, JoinSourceCooldown cooldown)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (IsJoin(context) && cooldown.Check(CreationRateLimiting.IpKey(context)) is int seconds)
        {
            await Rejection(context, seconds, "join_source_cooldown").ExecuteAsync(context);
            return;
        }
        await next(context);
    }

    private static bool IsJoin(HttpContext context)
        => context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == ParticipantEndpoints.JoinName;

    internal static IResult Rejection(HttpContext context, int seconds, string reason)
    {
        context.Features.Get<IHttpMetricsTagsFeature>()?.Tags.Add(new("pasto.rate_limit.reason", reason));
        return CreationRateLimiting.Rejection(context, seconds, "RateLimited");
    }
}

internal sealed class JoinSourceCooldown(TimeProvider clock, ILogger<JoinSourceCooldown> logger, int capacity = 20_000)
{
    private const int Limit = 20;
    private const int InitialCooldownSeconds = 60;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromMinutes(1);
    private static readonly EventId CapacityReached = new(6103, "JoinCooldownSourceCapacityReached");
    private readonly long origin = clock.GetTimestamp();
    private readonly object gate = new();
    private readonly Dictionary<string, Source> sources = new();
    private TimeSpan nextSweep;
    private TimeSpan nextWarning;
    private long skippedSinceWarning;

    internal int? Check(string source)
    {
        lock (gate)
        {
            var now = Maintain();
            sources.TryGetValue(source, out var entry);
            var remaining = entry?.CooldownUntil - now;
            return remaining > TimeSpan.Zero ? (int)Math.Ceiling(remaining.Value.TotalSeconds) : null;
        }
    }

    internal int? RecordFailure(string source, string? code)
    {
        var normalized = JoinRoom.NormalizeCode(code) ?? string.Empty;
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(MemoryMarshal.AsBytes(normalized.AsSpan()), hash);
        var digest = new Guid(hash[..16]);
        long skipped = 0;
        lock (gate)
        {
            var now = Maintain();
            if (!sources.TryGetValue(source, out var entry) && sources.Count < capacity)
                sources.Add(source, entry = new Source());

            if (entry is not null) return entry.Record(digest, now);

            skippedSinceWarning++;
            if (now >= nextWarning)
            {
                skipped = skippedSinceWarning;
                skippedSinceWarning = 0;
                nextWarning = now + MaintenanceInterval;
            }
        }
        if (skipped > 0)
            logger.LogWarning(CapacityReached,
                "Join cooldown source table was full. Skipped recording {SkippedMissCount} misses since the previous report.", skipped);
        return null;
    }

    private TimeSpan Maintain()
    {
        var now = clock.GetElapsedTime(origin);
        if (now < nextSweep) return now;
        nextSweep = now + MaintenanceInterval;
        foreach (var (key, entry) in sources)
            if (now >= entry.CooldownUntil && now - entry.LastMiss >= Window)
                sources.Remove(key);
        return now;
    }

    private sealed class Source
    {
        private readonly (Guid Digest, TimeSpan FirstSeen)[] misses = new (Guid, TimeSpan)[Limit];
        private int count;
        private int nextCooldownSeconds = InitialCooldownSeconds;

        internal TimeSpan LastMiss { get; private set; }
        internal TimeSpan CooldownUntil { get; private set; }

        internal int? Record(Guid digest, TimeSpan now)
        {
            // Already-admitted requests may complete after another request starts the cooldown.
            if (now < CooldownUntil) return null;
            if (now - LastMiss >= Window) nextCooldownSeconds = InitialCooldownSeconds;

            var validCount = 0;
            var duplicate = false;
            for (var index = 0; index < count; index++)
            {
                var miss = misses[index];
                if (now - miss.FirstSeen >= Window) continue;
                misses[validCount++] = miss;
                duplicate |= miss.Digest == digest;
            }
            Array.Clear(misses, validCount, count - validCount);
            count = validCount;
            if (duplicate) return null;
            if (count == Limit)
            {
                // 滿額的新錯碼會取代最舊指紋，讓重犯立即重新進入冷卻。
                Array.Copy(misses, 1, misses, 0, Limit - 1);
                count--;
            }

            LastMiss = now;
            misses[count++] = (digest, now);
            if (count < Limit) return null;

            var cooldownSeconds = nextCooldownSeconds;
            CooldownUntil = now + TimeSpan.FromSeconds(cooldownSeconds);
            nextCooldownSeconds = Math.Min(300, cooldownSeconds * 2);
            // 首次滿額維持原錯誤回應，重犯從觸發的這筆請求開始回 429。
            return cooldownSeconds > InitialCooldownSeconds ? cooldownSeconds : null;
        }
    }
}
