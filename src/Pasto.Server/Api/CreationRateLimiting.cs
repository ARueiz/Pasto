using System.Globalization;
using System.Net.Sockets;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Pasto.Server.Api;

internal static class CreationRateLimiting
{
    public static void Configure(RateLimiterOptions options, ApiOptions settings)
    {
        options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context => Profile(context, settings) is { } profile
                ? RateLimitPartition.GetTokenBucketLimiter($"{profile.Key}:{IpKey(context)}", _ => Bucket(profile.PerIp))
                : RateLimitPartition.GetNoLimiter("other")),
            PartitionedRateLimiter.Create<HttpContext, string>(context => Profile(context, settings) is { } profile
                ? RateLimitPartition.GetTokenBucketLimiter(profile.Key, _ => Bucket(profile.Global))
                : RateLimitPartition.GetNoLimiter("other")));
        options.OnRejected = async (rejection, _) =>
        {
            var profile = Profile(rejection.HttpContext, settings);
            await RejectAsync(rejection.HttpContext, rejection.Lease,
                profile is null ? 60 : Math.Max(profile.Value.PerIp.PeriodSeconds, profile.Value.Global.PeriodSeconds));
        };
    }

    private static (string Key, BucketOptions PerIp, BucketOptions Global)? Profile(HttpContext context, ApiOptions settings)
        => context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName switch
        {
            RoomCreationEndpoint.Name => ("create", settings.CreatePerIp, settings.CreateGlobal),
            ParticipantEndpoints.JoinName => ("join", settings.JoinPerIp, settings.JoinGlobal),
            // 受保護 REST 與 Hub 共用 DB 保護底線。只計入可能執行憑證查詢的請求。
            _ when ParticipantCredentialAdmission.Prepared(context) is not null
                => ("database", settings.ReadPerIp, settings.ReadGlobal),
            _ => null
        };

    internal static Task RejectAsync(HttpContext context, RateLimitLease lease, int fallbackSeconds, string code = "RateLimited")
    {
        var seconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds)) : fallbackSeconds;
        return RejectAsync(context, (int)seconds, code);
    }

    internal static Task RejectAsync(HttpContext context, int retryAfterSeconds, string code)
    {
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return ApiProblem.WriteAsync(context, 429, code);
    }

    internal static string IpKey(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
            return "unknown";
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4().ToString();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Span<byte> bytes = stackalloc byte[16];
            address.TryWriteBytes(bytes, out _);
            return Convert.ToHexString(bytes[..8]);
        }
        return address.ToString();
    }

    internal static TokenBucketRateLimiterOptions Bucket(BucketOptions settings) => new()
    {
        TokenLimit = settings.TokenLimit,
        TokensPerPeriod = settings.TokensPerPeriod,
        ReplenishmentPeriod = TimeSpan.FromSeconds(settings.PeriodSeconds),
        AutoReplenishment = true,
        QueueLimit = 0
    };
}
