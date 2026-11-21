using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Pasto.Server.Api;

// 配額鍵只使用已驗證的 Participant；刪除文字與關房共用刪除配額。
public sealed class ParticipantWriteLimiter(IOptions<ApiOptions> settings) : IDisposable
{
    private readonly PartitionedRateLimiter<(Guid Id, bool Add)> limiter =
        PartitionedRateLimiter.Create<(Guid Id, bool Add), (Guid Id, bool Add)>(key =>
            RateLimitPartition.GetTokenBucketLimiter(key, _ => CreationRateLimiting.Bucket(
                key.Add ? settings.Value.AddPerParticipant : settings.Value.DeletePerParticipant)));

    public RateLimitLease Acquire(Guid id, bool add) => limiter.AttemptAcquire((id, add));
    public void Dispose() => limiter.Dispose();
}

public sealed class ParticipantWriteFilter(ParticipantWriteLimiter limiter, ParticipantReadLimiter readLimiter,
    IOptions<ApiOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var roomId = Guid.Parse(Convert.ToString(http.GetRouteValue("roomId"), CultureInfo.InvariantCulture)!);
        var identity = ParticipantAuthenticationHandler.Identity(http.User);
        if (identity.RoomId != roomId)
            return await readLimiter.AllowAsync(http, identity.ParticipantId)
                ? ApiProblem.Create(http, 403, "ParticipantUnauthorized") : Results.Empty;
        var add = HttpMethods.IsPost(http.Request.Method);
        using var lease = limiter.Acquire(identity.ParticipantId, add);
        if (!lease.IsAcquired)
        {
            await CreationRateLimiting.RejectAsync(http, lease,
                (add ? options.Value.AddPerParticipant : options.Value.DeletePerParticipant).PeriodSeconds);
            return Results.Empty;
        }
        return await next(context);
    }
}
