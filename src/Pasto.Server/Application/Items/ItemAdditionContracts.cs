namespace Pasto.Server.Application.Items;

// 窄介面只負責一次原子新增；content／contentBytes 由 AddItem 驗證與計算。
public interface IItemAdditionStore
{
    Task<AddedItem> AddAsync(Guid roomId, Guid participantId, string content, int contentBytes,
        CancellationToken cancellationToken);
}

// 不攜帶正文，避免結果的 ToString 或日誌意外暴露內容。
public sealed record AddedItem(Guid ItemId, long CommittedVersion, DateTime CreatedAt);

public enum ItemContentError { Empty, InvalidUnicode, TooLarge }

public sealed class InvalidItemContentException(ItemContentError error)
    : Exception("Item content is invalid.")
{
    public ItemContentError Error { get; } = error;
}

public sealed class RoomItemLimitReachedException() : Exception("Room item limit was reached.");
