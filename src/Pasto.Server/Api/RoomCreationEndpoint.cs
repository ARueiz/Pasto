using System.Globalization;
using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Api;

internal static class RoomCreationEndpoint
{
    public const string Name = "CreateRoom";

    public static void MapRoomCreation(this WebApplication app)
        => app.MapPost("/api/rooms", async (CreateRoom create, CancellationToken cancellationToken) =>
        {
            var room = await create.ExecuteAsync(cancellationToken);
            return Results.Created($"/api/rooms/{room.RoomId}", new CreatedRoomResponse(room));
        }).WithName(Name).AllowAnonymous();
}

// 不用 record，避免自動 ToString 印出 Credential。
internal sealed class CreatedRoomResponse(CreatedRoom room)
{
    public Guid RoomId { get; } = room.RoomId;
    public string Code { get; } = room.Code;
    public string JoinUrl { get; } = $"/r/{room.Code}";
    public DateTime ExpiresAt { get; } = room.ExpiresAt;
    public string Version { get; } = room.Version.ToString(CultureInfo.InvariantCulture);
    public Guid ParticipantId { get; } = room.ParticipantId;
    public string Credential { get; } = room.Credential;
    public bool IsCreator { get; } = true;
}
