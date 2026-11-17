namespace Pasto.Server.Application.Rooms;

public sealed class CloseRoom(IRoomClosureStore store)
{
    // S2 由憑證驗證器提供身分；鎖內仍須驗證同房與 Creator。
    public Task<ClosedRoom> ExecuteAsync(Guid roomId, Guid participantId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return store.CloseAsync(roomId, participantId, cancellationToken);
    }
}
