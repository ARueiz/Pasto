using System.Globalization;
using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Api;

internal static class RoomEndpoints
{
    public const string StateName = "GetRoomState";
    public const string CloseName = "CloseRoom";

    public static void MapRoomStateAndClosure(this WebApplication app)
    {
        app.MapGet("/api/rooms/{roomId:guid}", async (Guid roomId, HttpContext context, GetRoomState get,
            CancellationToken cancellationToken) =>
        {
            var identity = ParticipantAuthenticationHandler.Identity(context, roomId);
            var state = await get.ExecuteAsync(roomId, identity.ParticipantId, cancellationToken);
            return Results.Ok(new RoomStateResponse(state));
        }).WithName(StateName).RequireAuthorization().AddEndpointFilter<ParticipantReadFilter>();

        app.MapDelete("/api/rooms/{roomId:guid}", async (Guid roomId, HttpContext context, CloseRoom close,
            CancellationToken cancellationToken) =>
        {
            var identity = ParticipantAuthenticationHandler.Identity(context, roomId);
            await close.ExecuteAsync(roomId, identity.ParticipantId, cancellationToken);
            return Results.NoContent();
        }).WithName(CloseName).RequireAuthorization().AddEndpointFilter<ParticipantWriteFilter>();
    }
}

internal sealed class RoomStateResponse(RoomState state)
{
    public string Version { get; } = state.Version.ToString(CultureInfo.InvariantCulture);
    public int ItemCount { get; } = state.ItemCount;
    public long TotalBytes { get; } = state.TotalBytes;
    public DateTime ExpiresAt { get; } = state.ExpiresAt;
    public DateTime ServerTime { get; } = state.ServerTime;
    public RoomLimits Limits { get; } = state.Limits;
}
