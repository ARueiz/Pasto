using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Domain;

namespace Pasto.Server.Infrastructure.Persistence;

// 僅共用房間寫入的鎖／時間／同房檢查；交易由 use case 的 Store 開始與提交。
internal static class RoomWriteAccess
{
    public static async Task<(Room Room, DateTime Now)> LockAsync(PastoDbContext context,
        Guid roomId, Guid participantId, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Room write access requires an active transaction.");

        // 僅作用於本交易，連線回池後不殘留；2 秒是初始候選值，S8 再依量測調整。
        await context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", cancellationToken);
        List<Room> rooms;
        try
        {
            rooms = await context.Rooms.FromSqlInterpolated($"""
                SELECT * FROM rooms WHERE id = {roomId} FOR UPDATE
                """).ToListAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable }
            or InvalidOperationException { InnerException: PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } })
        {
            // 僅轉換取得房間鎖的 55P03；不包住 SaveChanges／commit 的結果不確定例外。
            throw new RoomBusyException(exception);
        }

        var room = rooms.SingleOrDefault() ?? throw new RoomUnavailableException();
        // FOR UPDATE 完成後另發查詢，不在等鎖前計算，也不用交易起始時間 now()。
        var now = await context.Database.SqlQuery<DateTime>($"""
            SELECT clock_timestamp() AS "Value"
            """).SingleAsync(cancellationToken);
        if (!RoomRules.IsAvailable(room, now))
            throw new RoomUnavailableException();

        // 檢查順序：有效性 → Participant 同房 → 呼叫端處理操作權限／容量。
        if (!await context.Participants.AnyAsync(x => x.RoomId == roomId && x.Id == participantId, cancellationToken))
            throw new RoomParticipantMismatchException();

        return (room, now);
    }
}
