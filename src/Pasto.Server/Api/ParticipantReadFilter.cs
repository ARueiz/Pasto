using System.Globalization;

namespace Pasto.Server.Api;

// me、metadata 與文字快照共用同房檢查及 Participant 讀取配額。
internal sealed class ParticipantReadFilter(ParticipantRateLimits limiter) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var roomId = Guid.Parse(Convert.ToString(http.GetRouteValue("roomId"), CultureInfo.InvariantCulture)!);
        var identity = ParticipantAuthenticationHandler.Identity(http.User);
        if (limiter.TryTake(identity.ParticipantId, ParticipantRateProfile.Read) is int retry)
            return CreationRateLimiting.Rejection(http, retry, "RateLimited", "participant_rate");
        if (identity.RoomId != roomId) return ApiProblem.Create(http, 403, "ParticipantUnauthorized");
        return await next(context);
    }
}
