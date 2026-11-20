using System.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Infrastructure.Persistence;

public sealed class RoomCleanupStore(IDbContextFactory<PastoDbContext> contextFactory) : IRoomCleanupStore
{
    public async Task<int> ReclaimCodesAsync(int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, CleanUpRooms.MaxBatchSize);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", cancellationToken);
        await context.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = '5s'", cancellationToken);

        var codes = await context.Database.SqlQuery<string>($"""
            SELECT reservation.code AS "Value"
            FROM room_code_reservations AS reservation
            WHERE reusable_after <= statement_timestamp()
              AND NOT EXISTS (SELECT 1 FROM rooms WHERE code = reservation.code)
            ORDER BY reusable_after, code
            LIMIT {batchSize}
            FOR UPDATE OF reservation SKIP LOCKED
            """).ToArrayAsync(cancellationToken);
        if (codes.Length == 0)
            return 0;

        // 取得碼的列鎖後，以 READ COMMITTED 的新 statement snapshot 複驗引用及期限。
        var deleted = await context.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE FROM room_code_reservations AS reservation
            WHERE reservation.code = ANY({codes})
              AND reusable_after <= clock_timestamp()
              AND NOT EXISTS (SELECT 1 FROM rooms WHERE code = reservation.code)
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return deleted;
    }

    public async Task<HashSet<Guid>> FindUnavailableRoomsAsync(Guid[] roomIds, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(roomIds.Length, CleanUpRooms.MaxBatchSize);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var unavailable = await context.Database.SqlQuery<Guid>($"""
            SELECT tracked.id AS "Value"
            FROM unnest({roomIds}) AS tracked(id)
            LEFT JOIN rooms AS room ON room.id = tracked.id
            WHERE room.id IS NULL OR room.deleted_at IS NOT NULL OR room.expires_at <= statement_timestamp()
            """).ToArrayAsync(cancellationToken);
        return unavailable.ToHashSet();
    }

    public async Task<RoomCleanupBacklog> MeasureBacklogAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Database.SqlQueryRaw<RoomCleanupBacklog>("""
            SELECT expired.room_count + closed.room_count AS "PendingRooms",
                   LEAST(expired.oldest, closed.oldest) AS "OldestInvalidatedAt"
            FROM (
                SELECT count(*) AS room_count, min(expires_at) AS oldest
                FROM rooms
                WHERE deleted_at IS NULL AND expires_at <= statement_timestamp()
            ) AS expired
            CROSS JOIN (
                SELECT count(*) AS room_count, min(deleted_at) AS oldest
                FROM rooms
                WHERE deleted_at IS NOT NULL
            ) AS closed
            """).SingleAsync(cancellationToken);
    }

    public async Task<int> DeleteBatchAsync(int batchSize, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(batchSize, CleanUpRooms.MaxBatchSize);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        await context.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", cancellationToken);
        await context.Database.ExecuteSqlRawAsync("SET LOCAL statement_timeout = '5s'", cancellationToken);

        try
        {
            var deleted = await DeleteRoomsAsync(context, batchSize, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return deleted;
        }
        catch (Exception exception) when (IsCommandTimeout(exception))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new RoomCleanupBatchTimeoutException(exception);
        }
    }

    private static Task<int> DeleteRoomsAsync(PastoDbContext context, int batchSize, CancellationToken cancellationToken)
    {
        // 先在兩個單表查詢跳過被鎖列，再合併排序，避免一整批候選都卡在相同的房間。
        return context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (
                SELECT id, expires_at AS invalidated_at
                FROM rooms
                WHERE deleted_at IS NULL AND expires_at <= statement_timestamp()
                ORDER BY expires_at, id
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
            ), closed AS (
                SELECT id, deleted_at AS invalidated_at
                FROM rooms
                WHERE deleted_at IS NOT NULL
                ORDER BY deleted_at, id
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
            ), batch AS (
                SELECT id FROM (
                    SELECT id, invalidated_at FROM expired
                    UNION ALL
                    SELECT id, invalidated_at FROM closed
                ) AS candidates
                ORDER BY invalidated_at, id
                LIMIT {batchSize}
            )
            DELETE FROM rooms AS room
            USING batch
            WHERE room.id = batch.id
              AND (room.deleted_at IS NOT NULL OR room.expires_at <= clock_timestamp())
            """, cancellationToken);
    }

    private static bool IsCommandTimeout(Exception exception) => exception switch
    {
        PostgresException postgres => postgres.SqlState == PostgresErrorCodes.QueryCanceled,
        NpgsqlException { InnerException: TimeoutException } => true,
        InvalidOperationException { InnerException: Exception inner } => IsCommandTimeout(inner),
        _ => false
    };
}
