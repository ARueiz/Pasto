using Microsoft.EntityFrameworkCore;
using Pasto.Server.Application.Items;
using Pasto.Server.Domain;

namespace Pasto.Server.Infrastructure.Persistence;

public sealed class ItemAdditionStore(IDbContextFactory<PastoDbContext> contextFactory) : IItemAdditionStore
{
    public async Task<AddedItem> AddAsync(Guid roomId, Guid participantId, string content, int contentBytes,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // 僅作用於本交易，連線回池後不殘留；2 秒是初始候選值，S8 再依量測調整。
        await context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", cancellationToken);
        var rooms = await context.Rooms.FromSqlInterpolated($"""
            SELECT * FROM rooms WHERE id = {roomId} FOR UPDATE
            """).ToListAsync(cancellationToken);
        var room = rooms.SingleOrDefault();
        if (room is null)
        {
            throw new RoomUnavailableException();
        }

        // 必須在 FOR UPDATE 完成後另發查詢，不在等鎖前計算時間，也不用交易起始時間 now()。
        var now = await context.Database.SqlQuery<DateTime>($"""
            SELECT clock_timestamp() AS "Value"
            """).SingleAsync(cancellationToken);
        if (room.DeletedAt is not null || room.ExpiresAt <= now)
        {
            throw new RoomUnavailableException();
        }

        // 明確回報應用層失敗；複合 FK 仍為最後防線，而非正常授權流程。
        if (!await context.Participants.AnyAsync(x => x.RoomId == roomId && x.Id == participantId, cancellationToken))
        {
            throw new RoomParticipantMismatchException();
        }

        if (room.ItemCount >= 300)
        {
            throw new RoomItemLimitReachedException();
        }

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
        // commit 期間取消／連線失敗可能已提交，不自動重試；成功結果只能在 commit 後回傳。
        await transaction.CommitAsync(cancellationToken);
        return new AddedItem(item.Id, room.Version);
    }
}
