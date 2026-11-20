using System.Diagnostics;

namespace Pasto.Server.Application.Rooms;

public sealed record RoomCleanupBacklog(long PendingRooms, DateTime? OldestInvalidatedAt);

public sealed class RoomCleanupProgress
{
    public int CommittedBatches { get; private set; }
    public int DeletedRooms { get; private set; }

    internal void RecordCommit(int deletedRooms)
    {
        if (deletedRooms <= 0)
            return;

        CommittedBatches++;
        DeletedRooms += deletedRooms;
    }
}

public sealed class RoomCleanupBatchTimeoutException(Exception innerException)
    : TimeoutException("The room cleanup batch timed out.", innerException);

public interface IRoomCleanupStore
{
    Task<RoomCleanupBacklog> MeasureBacklogAsync(CancellationToken cancellationToken);
    Task<int> DeleteBatchAsync(int batchSize, CancellationToken cancellationToken);
}

public sealed class CleanUpRooms(
    IRoomCleanupStore store,
    IStorageFailureClassifier storageFailures,
    ILogger<CleanUpRooms> logger)
{
    internal const int MaxBatchSize = 100;
    private static readonly EventId BatchReduced = new(6004, "RoomCleanupBatchReduced");
    private int batchSize = MaxBatchSize;

    public async Task ExecuteAsync(RoomCleanupProgress progress, CancellationToken cancellationToken)
    {
        try
        {
            var backlog = await store.MeasureBacklogAsync(cancellationToken);
            logger.LogInformation("Room cleanup starting with {PendingRooms} pending rooms. Oldest invalidation: {OldestInvalidatedAt}",
                backlog.PendingRooms, backlog.OldestInvalidatedAt);
        }
        catch (Exception exception) when (storageFailures.IsStorageFailure(exception))
        {
            // 待清量查詢逾時仍可清理，避免大批積壓時每輪都停在統計。
            logger.LogWarning("Room cleanup backlog measurement failed ({ErrorType})", exception.GetType().Name);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptedSize = batchSize;
            var started = Stopwatch.GetTimestamp();
            int deleted;
            try
            {
                deleted = await store.DeleteBatchAsync(attemptedSize, cancellationToken);
            }
            catch (RoomCleanupBatchTimeoutException) when (attemptedSize > 1)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batchSize = attemptedSize / 2;
                logger.LogWarning(BatchReduced, "Room cleanup batch timed out at {BatchSize} rooms. Next batch size: {NextBatchSize}",
                    attemptedSize, batchSize);
                continue;
            }

            progress.RecordCommit(deleted);
            logger.LogInformation("Room cleanup committed {DeletedRooms} deletions in {ElapsedMilliseconds} ms",
                deleted, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            if (deleted < attemptedSize)
                return;

            batchSize = Math.Min(attemptedSize + 10, MaxBatchSize);
        }
    }
}
