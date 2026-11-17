using Pasto.Server.Domain;

namespace Pasto.Server.Application.Rooms;

public interface IRoomStateStore
{
    Task<RoomState> GetAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken);
}

public sealed class GetRoomState(IRoomStateStore store)
{
    public Task<RoomState> ExecuteAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken)
        => store.GetAsync(roomId, participantId, cancellationToken);
}

public sealed record RoomState(long Version, int ItemCount, long TotalBytes, DateTime ExpiresAt, DateTime ServerTime)
{
    public RoomLimits Limits { get; } = new(RoomRules.MaxContentBytes, RoomRules.MaxItemCount, RoomRules.MaxParticipantCount);
}

public sealed record RoomLimits(int MaxContentBytes, int MaxItemCount, int MaxParticipantCount);
