namespace Pasto.Server.Application.Items;

public sealed class DeleteOwnItem(IItemDeletionStore store)
{
    // roomId／participantId 由 S2 憑證驗證器提供；Store 仍在房間鎖內重驗同房與 Owner。
    public Task<ItemDeletionResult> ExecuteAsync(Guid roomId, Guid participantId, Guid itemId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return store.DeleteOwnAsync(roomId, participantId, itemId, cancellationToken);
    }
}
