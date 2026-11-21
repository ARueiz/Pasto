using System.Globalization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

namespace Pasto.Server.Api;

internal enum HttpWork { Join, Add, ReadDelete }

internal sealed class HttpWorkOptions
{
    public int GlobalLimit { get; set; } = 96;
    public int JoinLimit { get; set; } = 16;
    public int AddLimit { get; set; } = 32;
    public int ReadDeleteLimit { get; set; } = 48;
    public int JoinPerSource { get; set; } = 8;
    public int AddPerSource { get; set; } = 16;
    public int ReadDeletePerSource { get; set; } = 24;
    public int SourceCapacity { get; set; } = 96;
    public int HighWatermark { get; set; } = 80;
    public int RecoveryBelow { get; set; } = 64;
    public int HighSeconds { get; set; } = 2;
    public int RecoverySeconds { get; set; } = 10;

    internal bool IsValid() => GlobalLimit > 0 && JoinLimit > 0 && AddLimit > 0 && ReadDeleteLimit > 0 &&
        JoinPerSource > 0 && JoinPerSource <= JoinLimit &&
        AddPerSource > 0 && AddPerSource <= AddLimit &&
        ReadDeletePerSource > 0 && ReadDeletePerSource <= ReadDeleteLimit &&
        SourceCapacity > 0 && RecoveryBelow > 0 && RecoveryBelow < HighWatermark &&
        HighWatermark <= GlobalLimit && HighSeconds > 0 && RecoverySeconds > 0;
}

internal sealed class HttpWorkAdmission(IOptions<HttpWorkOptions> options, TimeProvider clock)
{
    private readonly HttpWorkOptions limits = options.Value;
    private readonly int[] classLimits = [options.Value.JoinLimit, options.Value.AddLimit, options.Value.ReadDeleteLimit];
    private readonly int[] sourceLimits = [options.Value.JoinPerSource, options.Value.AddPerSource, options.Value.ReadDeletePerSource];
    private readonly object gate = new();
    private readonly int[] counts = new int[3];
    private readonly Dictionary<string, Source> sources = new();
    private readonly long origin = clock.GetTimestamp();
    private TimeSpan? thresholdSince;
    private int total;
    private bool overloaded;

    internal Lease? TryAcquire(HttpWork work, string source, out int retryAfter)
    {
        lock (gate)
        {
            var now = clock.GetElapsedTime(origin);
            UpdateState(now);
            var index = (int)work;
            sources.TryGetValue(source, out var tracked);
            if (total >= limits.GlobalLimit || counts[index] >= classLimits[index] ||
                tracked?.Counts[index] >= sourceLimits[index])
            {
                retryAfter = 1;
                return null;
            }
            if (overloaded && work == HttpWork.Join)
            {
                retryAfter = 5;
                return null;
            }
            if (tracked is null && sources.Count < limits.SourceCapacity)
                sources.Add(source, tracked = new Source());
            if (tracked is not null)
            {
                tracked.Counts[index]++;
                tracked.Total++;
            }
            total++;
            counts[index]++;
            UpdateState(now);
            retryAfter = 0;
            return new Lease(this, index, tracked is not null ? source : null);
        }
    }

    private void Release(int index, string? source)
    {
        lock (gate)
        {
            var now = clock.GetElapsedTime(origin);
            UpdateState(now);
            total--;
            counts[index]--;
            if (source is not null)
            {
                var tracked = sources[source];
                tracked.Counts[index]--;
                if (--tracked.Total == 0) sources.Remove(source);
            }
            UpdateState(now);
        }
    }

    private void UpdateState(TimeSpan now)
    {
        var inRange = overloaded ? total < limits.RecoveryBelow : total >= limits.HighWatermark;
        if (!inRange)
        {
            thresholdSince = null;
            return;
        }
        thresholdSince ??= now;
        var hold = overloaded ? limits.RecoverySeconds : limits.HighSeconds;
        if (now - thresholdSince.Value >= TimeSpan.FromSeconds(hold))
        {
            overloaded = !overloaded;
            thresholdSince = null;
        }
    }

    internal static IResult Reject(HttpContext context, int seconds)
    {
        context.Features.Get<IHttpMetricsTagsFeature>()?.Tags.Add(new("pasto.admission.reason", seconds == 1 ? "in_flight" : "high_watermark"));
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return ApiProblem.Create(context, 503, "Overloaded", seconds);
    }

    private sealed class Source
    {
        internal int[] Counts { get; } = new int[3];
        internal int Total { get; set; }
    }

    internal sealed class Lease(HttpWorkAdmission owner, int index, string? source) : IDisposable
    {
        private HttpWorkAdmission? admission = owner;
        public void Dispose() => Interlocked.Exchange(ref admission, null)?.Release(index, source);
    }
}

internal static class HttpWorkEndpoints
{
    internal static RouteHandlerBuilder LimitWork(this RouteHandlerBuilder endpoint, HttpWork work)
        => endpoint.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (http.Response.StatusCode >= 400) return await next(context);

            var services = http.RequestServices;
            var source = CreationRateLimiting.IpKey(http);
            using var lease = services.GetRequiredService<HttpWorkAdmission>().TryAcquire(work, source, out var retry);
            if (lease is null) return HttpWorkAdmission.Reject(http, retry);

            if (work == HttpWork.Join)
            {
                var isJoin = http.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == ParticipantEndpoints.JoinName;
                var profile = isJoin ? SourceRateProfile.Join : SourceRateProfile.Create;
                if (services.GetRequiredService<SourceRateLimits>().TryTake(profile, source) is int seconds)
                    return CreationRateLimiting.Rejection(http, seconds, "RateLimited", "source_rate");
                if (isJoin && services.GetRequiredService<JoinPressureLimiter>().Check() is int pressureRetry)
                    return PublicJoinAdmission.Rejection(http, pressureRetry, "join_pressure");
            }

            return await next(context);
        });

    internal static async Task LimitPublicBodyAsync(HttpContext context, RequestDelegate next)
    {
        var name = context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName;
        if (name is RoomCreationEndpoint.Name or ParticipantEndpoints.JoinName)
        {
            const int maximum = 1024;
            if (context.Request.ContentLength > maximum)
            {
                await ApiProblem.WriteAsync(context, 413, "RequestBodyTooLarge");
                return;
            }
            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } feature)
                feature.MaxRequestBodySize = maximum;
            // 建房 handler 不讀取 body，先確認可能分塊傳送的內容不會超額。
            if (name == RoomCreationEndpoint.Name &&
                context.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody == true)
            {
                var buffer = new byte[maximum + 1];
                var received = await context.Request.Body.ReadAtLeastAsync(buffer, buffer.Length, false, context.RequestAborted);
                if (received > maximum)
                {
                    await ApiProblem.WriteAsync(context, 413, "RequestBodyTooLarge");
                    return;
                }
            }
        }
        await next(context);
    }
}
