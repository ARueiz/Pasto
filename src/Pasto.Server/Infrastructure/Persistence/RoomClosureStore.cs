using Microsoft.EntityFrameworkCore;
using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Infrastructure.Persistence;

public sealed class RoomClosureStore(IDbContextFactory<PastoDbContext> contextFactory) : IRoomClosureStore
{
    public async Task<ClosedRoom> CloseAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var (room, now) = await RoomWriteAccess.LockAsync(context, roomId, participantId, cancellationToken);
        if (room.CreatorParticipantId != participantId)
            throw new RoomOwnershipException();

        // 只邏輯關房，保留文字／Participant／統計與原期限；實體清理由 S6 處理。
        room.DeletedAt = now;
        room.Version = checked(room.Version + 1);
        await context.SaveChangesAsync(cancellationToken);
        // 已關房在共用檢查就拒絕，不作冪等成功；commit 失敗／取消不自動重送。
        await transaction.CommitAsync(cancellationToken);
        return new ClosedRoom(now, room.Version);
    }
}
