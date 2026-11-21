using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Api;

internal static class ParticipantEndpoints
{
    public const string JoinName = "JoinRoom";
    public const string MeName = "GetParticipant";

    public static void MapParticipants(this WebApplication app)
    {
        app.MapPost("/api/rooms/join", JoinAsync).WithName(JoinName).AllowAnonymous();
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

    private static async Task JoinAsync(JoinRequest request, HttpContext context, JoinRoom join,
        JoinSourceCooldown cooldown, JoinPressureLimiter pressure, CancellationToken cancellationToken)
    {
        try
        {
            var joined = await join.ExecuteAsync(request.Code, cancellationToken);
            await Results.Created($"/api/rooms/{joined.RoomId}/me", new JoinedRoomResponse(joined)).ExecuteAsync(context);
        }
        catch (Exception exception) when (exception is InvalidRoomCodeException or RoomUnavailableException)
        {
            if (exception is RoomUnavailableException) pressure.RecordLookupMiss();
            if (cooldown.RecordFailure(CreationRateLimiting.IpKey(context), request.Code) is not int seconds)
                throw;
            await PublicJoinAdmission.RejectAsync(context, seconds, "join_source_cooldown");
        }
        catch (RoomJoinRateLimitedException denied)
        {
            await PublicJoinAdmission.RejectAsync(context, denied.RetryAfterSeconds, "join_room");
        }
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
