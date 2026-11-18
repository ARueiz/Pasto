using System.Globalization;
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
            // DELETE 同樣須先保護憑證查詢，但不耗 Participant 的讀取配額。
            ParticipantEndpoints.MeName or RoomEndpoints.StateName or RoomEndpoints.CloseName
                or ItemEndpoints.AddName or ItemEndpoints.DeleteName or ItemEndpoints.SnapshotName
                => ("read", settings.ReadPerIp, settings.ReadGlobal),
            _ => null
        };

    internal static Task RejectAsync(HttpContext context, RateLimitLease lease, int fallbackSeconds)
    {
        var seconds = lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
            ? Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds)) : fallbackSeconds;
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return ApiProblem.WriteAsync(context, 429, "RateLimited");
    }

    private static string IpKey(HttpContext context)
        => context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";

    internal static TokenBucketRateLimiterOptions Bucket(BucketOptions settings) => new()
    {
        TokenLimit = settings.TokenLimit,
        TokensPerPeriod = settings.TokensPerPeriod,
        ReplenishmentPeriod = TimeSpan.FromSeconds(settings.PeriodSeconds),
        AutoReplenishment = true,
        QueueLimit = 0
    };
}
