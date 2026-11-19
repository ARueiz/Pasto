using Microsoft.AspNetCore.SignalR;
using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Api;

internal sealed class RoomHub(GetRoomState getRoomState) : Hub
{
    internal const string Path = "/hubs/room";

    public async Task SubscribeRoom(Guid roomId)
    {
        var cancellationToken = Context.ConnectionAborted;
        try
        {
            var identity = ParticipantAuthenticationHandler.Identity(Context.User, roomId);
            await getRoomState.ExecuteAsync(roomId, identity.ParticipantId, cancellationToken);
            var group = roomId.ToString();
            try
            {
                await Groups.AddToGroupAsync(Context.ConnectionId, group, cancellationToken);
                // 加入群組後再確認房間狀態，處理第一次檢查後才關房的競爭。
                await getRoomState.ExecuteAsync(roomId, identity.ParticipantId, cancellationToken);
            }
            catch
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, group);
                throw;
            }
        }
        catch (RoomUnavailableException)
        {
            throw new HubException("RoomUnavailable");
        }
        catch (RoomParticipantMismatchException)
        {
            throw new HubException("ParticipantUnauthorized");
        }
    }

    internal static async Task CheckOriginAsync(HttpContext context, RequestDelegate next)
    {
        var request = context.Request;
        if ((request.Path == Path || request.Path == Path + "/negotiate") && request.Headers.TryGetValue("Origin", out var origin)
            && (origin.Count != 1 || !Uri.TryCreate(origin[0], UriKind.Absolute, out var actual)
                || !Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out var expected)
                || actual.UserInfo.Length != 0 || actual.PathAndQuery != "/" || actual.Fragment.Length != 0
                || actual.GetLeftPart(UriPartial.Authority) != expected.GetLeftPart(UriPartial.Authority)))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    }
}
