namespace Pasto.Server.Application.Items;

public interface IItemDeletionStore
{
    Task<ItemDeletionResult> DeleteOwnAsync(Guid roomId, Guid participantId, Guid itemId,
        CancellationToken cancellationToken);
}

public sealed record ItemDeletionResult(bool Changed, long CommittedVersion);
public sealed class ItemOwnershipException() : Exception("Only the item owner may delete it.");
