using System.Globalization;

namespace Pasto.Server.Api;

// 刪除文字與關房共用 Participant 刪除配額。
internal sealed class ParticipantWriteFilter(ParticipantRateLimits limiter) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var roomId = Guid.Parse(Convert.ToString(http.GetRouteValue("roomId"), CultureInfo.InvariantCulture)!);
        var identity = ParticipantAuthenticationHandler.Identity(http.User);
        var crossRoom = identity.RoomId != roomId;
        var profile = crossRoom ? ParticipantRateProfile.Read :
            HttpMethods.IsPost(http.Request.Method) ? ParticipantRateProfile.Add : ParticipantRateProfile.Delete;
        if (limiter.TryTake(identity.ParticipantId, profile) is int retry)
            return CreationRateLimiting.Rejection(http, retry, "RateLimited", "participant_rate");
        if (crossRoom) return ApiProblem.Create(http, 403, "ParticipantUnauthorized");
        return await next(context);
    }
}
