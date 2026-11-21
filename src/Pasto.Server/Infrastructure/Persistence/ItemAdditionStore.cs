using Microsoft.EntityFrameworkCore;
using Pasto.Server.Application.Items;
using Pasto.Server.Domain;

namespace Pasto.Server.Infrastructure.Persistence;

public sealed class ItemAdditionStore(IDbContextFactory<PastoDbContext> contextFactory) : IItemAdditionStore
{
    private readonly RoomAddQuota? quota;

    internal ItemAdditionStore(IDbContextFactory<PastoDbContext> contextFactory, RoomAddQuota quota) : this(contextFactory)
    {
        this.quota = quota;
    }

    public async Task<AddedItem> AddAsync(Guid roomId, Guid participantId, string content, int contentBytes,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var (room, now) = await RoomWriteAccess.LockAsync(context, roomId, participantId, cancellationToken);
        if (room.ItemCount >= RoomRules.MaxItemCount)
            throw new RoomItemLimitReachedException();
        if (quota?.RetryAfter(roomId) is int retry)
            throw new RoomAddRateLimitedException(retry);

        room.ItemCount++;
        room.TotalBytes = checked(room.TotalBytes + contentBytes);
        room.Version = checked(room.Version + 1);
        var item = new ClipboardItem
        {
            Id = Guid.NewGuid(),
            RoomId = room.Id,
            OwnerParticipantId = participantId,
            Content = content,
            ContentBytes = contentBytes,
            CreatedAt = now,
            CreatedVersion = room.Version
        };
        context.Items.Add(item);
        await context.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        quota?.Charge(roomId);
        // commit 期間取消／連線失敗可能已提交，不自動重試；成功結果只能在 commit 後回傳。
        await transaction.CommitAsync(cancellationToken);
        return new AddedItem(item.Id, room.Version, now);
    }
}
