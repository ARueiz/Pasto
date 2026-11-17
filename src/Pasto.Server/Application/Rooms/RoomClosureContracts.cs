namespace Pasto.Server.Application.Rooms;

public interface IRoomClosureStore
{
    Task<ClosedRoom> CloseAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken);
}

public sealed record ClosedRoom(DateTime DeletedAt, long CommittedVersion);
public sealed class RoomOwnershipException() : Exception("Only the room creator may close it.");
