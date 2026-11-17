using Microsoft.EntityFrameworkCore;
using Pasto.Server.Application.Items;

namespace Pasto.Server.Infrastructure.Persistence;

public sealed class ItemDeletionStore(IDbContextFactory<PastoDbContext> contextFactory) : IItemDeletionStore
{
    public async Task<ItemDeletionResult> DeleteOwnAsync(Guid roomId, Guid participantId, Guid itemId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var (room, _) = await RoomWriteAccess.LockAsync(context, roomId, participantId, cancellationToken);
        // 限定同房查詢；他房的 itemId 視為本房不存在，不查出他房資料再判斷。
        var item = await context.Items.SingleOrDefaultAsync(x => x.RoomId == roomId && x.Id == itemId, cancellationToken);
        if (item is not null)
        {
            // 房主沒有刪除他人單筆的特權。
            if (item.OwnerParticipantId != participantId)
                throw new ItemOwnershipException();

            context.Items.Remove(item);
            room.ItemCount = checked(room.ItemCount - 1);
            room.TotalBytes = checked(room.TotalBytes - item.ContentBytes);
            room.Version = checked(room.Version + 1);
            await context.SaveChangesAsync(cancellationToken);
        }

        // 不存在也回傳鎖內觀察的版本；不扣統計、不加版本。
        // commit 期間失敗／取消不自動重送，也不宣稱必然回滾。
        await transaction.CommitAsync(cancellationToken);
        return new ItemDeletionResult(item is not null, room.Version);
    }
}
