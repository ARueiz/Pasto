using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Pasto.Server.Api;

internal static class CreationRateLimiting
{
    public static void Configure(RateLimiterOptions options, ApiOptions settings)
    {
        options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
            PartitionedRateLimiter.Create<HttpContext, string>(context => IsCreation(context)
                ? RateLimitPartition.GetTokenBucketLimiter(IpKey(context), _ => Bucket(settings.CreatePerIp))
                : RateLimitPartition.GetNoLimiter("other")),
            PartitionedRateLimiter.Create<HttpContext, string>(context => IsCreation(context)
                ? RateLimitPartition.GetTokenBucketLimiter("create-global", _ => Bucket(settings.CreateGlobal))
                : RateLimitPartition.GetNoLimiter("other")));
        options.OnRejected = async (rejection, _) =>
        {
            var seconds = rejection.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter)
                ? Math.Max(1, Math.Ceiling(retryAfter.TotalSeconds))
                : Math.Max(settings.CreatePerIp.PeriodSeconds, settings.CreateGlobal.PeriodSeconds);
            rejection.HttpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            await ApiProblem.WriteAsync(rejection.HttpContext, 429, "RateLimited");
        };
    }

    private static bool IsCreation(HttpContext context)
        => context.GetEndpoint()?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == RoomCreationEndpoint.Name;

    private static string IpKey(HttpContext context)
        => context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";

    private static TokenBucketRateLimiterOptions Bucket(BucketOptions settings) => new()
    {
        TokenLimit = settings.TokenLimit,
        TokensPerPeriod = settings.TokensPerPeriod,
        ReplenishmentPeriod = TimeSpan.FromSeconds(settings.PeriodSeconds),
        AutoReplenishment = true,
        QueueLimit = 0
    };
}
