using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Api;

internal static class ParticipantEndpoints
{
    public const string JoinName = "JoinRoom";
    public const string MeName = "GetParticipant";

    public static void MapParticipants(this WebApplication app)
    {
        app.MapPost("/api/rooms/join", async (JoinRequest request, JoinRoom join, CancellationToken cancellationToken) =>
        {
            var joined = await join.ExecuteAsync(request.Code, cancellationToken);
            return Results.Created($"/api/rooms/{joined.RoomId}/me", new JoinedRoomResponse(joined));
        }).WithName(JoinName).AllowAnonymous();
        app.MapGet("/api/rooms/{roomId:guid}/me", async (Guid roomId, HttpContext context, GetParticipant get,
            CancellationToken cancellationToken) =>
        {
            var identity = ParticipantAuthenticationHandler.Identity(context, roomId);
            var participant = await get.ExecuteAsync(roomId, identity.ParticipantId, cancellationToken);
            return Results.Ok(new
            {
                participant.RoomId,
                participant.ParticipantId,
                participant.IsCreator,
                authorCode = ParticipantAuthorCode.From(participant.RoomId, participant.ParticipantId)
            });
        }).WithName(MeName).RequireAuthorization().AddEndpointFilter<ParticipantReadFilter>();
    }
}

internal sealed record JoinRequest(string? Code);

internal sealed class JoinedRoomResponse(JoinedRoom room)
{
    public Guid RoomId { get; } = room.RoomId;
    public Guid ParticipantId { get; } = room.ParticipantId;
    public string AuthorCode { get; } = ParticipantAuthorCode.From(room.RoomId, room.ParticipantId);
    public string Code { get; } = room.Code;
    public string JoinUrl { get; } = $"/r/{room.Code}";
    public DateTime ExpiresAt { get; } = room.ExpiresAt;
    public string Version { get; } = room.Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public string Credential { get; } = room.Credential;
    public bool IsCreator { get; } = false;
}
