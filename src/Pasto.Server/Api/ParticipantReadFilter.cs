using System.Globalization;
using Microsoft.Extensions.Options;

namespace Pasto.Server.Api;

// me、metadata 與文字快照共用同房檢查及 Participant 讀取配額。
public sealed class ParticipantReadFilter(ParticipantReadLimiter limiter, IOptions<ApiOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var roomId = Guid.Parse(Convert.ToString(http.GetRouteValue("roomId"), CultureInfo.InvariantCulture)!);
        var identity = ParticipantAuthenticationHandler.Identity(http, roomId);
        using var lease = limiter.Acquire(identity.ParticipantId);
        if (!lease.IsAcquired)
        {
            await CreationRateLimiting.RejectAsync(http, lease, options.Value.ReadPerParticipant.PeriodSeconds);
            return Results.Empty;
        }
        return await next(context);
    }
}
