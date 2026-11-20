using Microsoft.AspNetCore.SignalR;
using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Api;

internal sealed class RoomHub(GetRoomState getRoomState, RoomConnections connections) : Hub
{
    internal const string Path = "/hubs/room";
    private static readonly object UntrackedSubscription = new();

    public async Task SubscribeRoom(Guid roomId)
    {
        var connection = Context;
        var cancellationToken = connection.ConnectionAborted;
        try
        {
            var identity = ParticipantAuthenticationHandler.Identity(Context.User, roomId);
            await getRoomState.ExecuteAsync(roomId, identity.ParticipantId, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
                return;

            var alreadyUntracked = connection.Items.ContainsKey(UntrackedSubscription);
            // 先登記，讓並行關房也能中止尚在加群組的連線。加入後仍以 DB 複驗。
            var tracking = alreadyUntracked ? RoomTrackingResult.AtCapacity
                : connections.TryTrack(connection.ConnectionId, roomId, cancellationToken, connection.Abort);
            if (tracking == RoomTrackingResult.ConnectionAborted)
                return;
            if (tracking == RoomTrackingResult.RoomMismatch)
            {
                // 憑證只授權一個房間，現有 Client 換房會建立新連線；同一連線不得轉移追蹤。
                connection.Abort();
                return;
            }

            var group = roomId.ToString();
            try
            {
                await Groups.AddToGroupAsync(connection.ConnectionId, group, cancellationToken);
                // 加入群組後再確認房間狀態，處理第一次檢查後才關房的競爭。
                await getRoomState.ExecuteAsync(roomId, identity.ParticipantId, cancellationToken);
                if (tracking == RoomTrackingResult.AtCapacity && !alreadyUntracked)
                {
                    // 已完成的訂閱在此連線生命週期沿用未追蹤狀態，不回頭占用名額。
                    connection.Items[UntrackedSubscription] = true;
                    connections.RecordUntrackedSubscription();
                }
            }
            catch
            {
                connections.Untrack(connection.ConnectionId);
                await Groups.RemoveFromGroupAsync(connection.ConnectionId, group);
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

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Untrack(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
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
