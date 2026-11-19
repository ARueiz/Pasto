namespace Pasto.Server.Application.Items;

public interface IItemSnapshotStore
{
    Task<ItemSnapshot> GetAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken);
}

public sealed class GetItemSnapshot(IItemSnapshotStore store)
{
    public Task<ItemSnapshot> ExecuteAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken)
        => store.GetAsync(roomId, participantId, cancellationToken);
}

public sealed record ItemSnapshot(IReadOnlyList<SnapshotItem> Items, int ItemCount, long Version, DateTime ExpiresAt);

public sealed class SnapshotItem(Guid id, string content, DateTime createdAt, long createdVersion,
    Guid ownerParticipantId, bool isOwn, bool authorIsCreator)
{
    public Guid Id { get; } = id;
    public string Content { get; } = content;
    public DateTime CreatedAt { get; } = createdAt;
    public long CreatedVersion { get; } = createdVersion;
    internal Guid OwnerParticipantId { get; } = ownerParticipantId;
    public bool IsOwn { get; } = isOwn;
    public bool AuthorIsCreator { get; } = authorIsCreator;
}
