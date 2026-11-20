using System.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;
using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Infrastructure.Persistence;

public sealed class RoomCleanupOptions
{
    public bool Enabled { get; set; } = true;
    public int MaxPoolSize { get; set; } = 4;
}

internal sealed class RoomCleanupWorker(
    IOptions<RoomCleanupOptions> options,
    CleanUpRooms cleanup,
    RoomCleanupStore store,
    RoomConnections connections,
    IStorageFailureClassifier storageFailures,
    TimeProvider timeProvider,
    ILogger<RoomCleanupWorker> logger) : BackgroundService
{
    private static readonly EventId UnexpectedFailure = new(6001, "RoomCleanupFailed");
    private static readonly EventId RoundTimedOut = new(6002, "RoomCleanupRoundTimedOut");
    private static readonly EventId DatabaseFailure = new(6003, "RoomCleanupDatabaseFailed");
    private static readonly EventId SingleRoomTimedOut = new(6005, "RoomCleanupSingleRoomTimedOut");
    private static readonly EventId RoundIncomplete = new(6006, "RoomCleanupRoundIncomplete");
    private static readonly EventId ConnectionCleanupDelayed = new(6007, "RoomConnectionCleanupDelayed");
    private static readonly EventId CodeCleanupDelayed = new(6008, "RoomCodeCleanupDelayed");
    private Guid lastInspectedRoom;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;

        var consecutiveFailures = 0;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), timeProvider);
        do
        {
            var progress = new RoomCleanupProgress();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20), timeProvider);
            using var round = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, budget.Token);
            try
            {
                await cleanup.ExecuteAsync(progress, round.Token);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested && progress.CommittedBatches > 0)
            {
                consecutiveFailures = 0;
                logger.LogWarning(RoundIncomplete,
                    "Room cleanup reached the round time budget after committing {CommittedBatches} batches and deleting {DeletedRooms} rooms",
                    progress.CommittedBatches, progress.DeletedRooms);
            }
            catch (OperationCanceledException) when (budget.IsCancellationRequested)
            {
                logger.LogWarning(RoundTimedOut, "Room cleanup reached the round time budget. Consecutive failures: {ConsecutiveFailures}",
                    ++consecutiveFailures);
            }
            catch (RoomCleanupBatchTimeoutException)
            {
                logger.LogWarning(SingleRoomTimedOut, "Room cleanup timed out at batch size {BatchSize}. Consecutive failures: {ConsecutiveFailures}",
                    1, ++consecutiveFailures);
            }
            catch (Exception exception) when (storageFailures.IsStorageFailure(exception))
            {
                var postgres = exception as PostgresException ?? exception.GetBaseException() as PostgresException;
                logger.LogWarning(DatabaseFailure,
                    "Room cleanup database operation failed ({ErrorType}, SQLSTATE {SqlState}). Consecutive failures: {ConsecutiveFailures}",
                    exception.GetType().Name, postgres?.SqlState, ++consecutiveFailures);
            }
            catch (Exception exception)
            {
                logger.LogError(UnexpectedFailure, "Room cleanup failed ({ErrorType}). Consecutive failures: {ConsecutiveFailures}. Frames: {StackFrames}",
                    exception.GetType().Name, ++consecutiveFailures, new StackTrace(exception, false).ToString());
            }

            await RunMaintenanceAsync(CleanUpConnectionsAsync, ConnectionCleanupDelayed, stoppingToken);
            await RunMaintenanceAsync(ReclaimCodesAsync, CodeCleanupDelayed, stoppingToken);
            connections.ReportUntrackedSubscriptions();
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task RunMaintenanceAsync(Func<CancellationToken, Task> run, EventId failure, CancellationToken stoppingToken)
    {
        if (stoppingToken.IsCancellationRequested)
            return;

        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5), timeProvider);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, budget.Token);
        try
        {
            await run(cancellation.Token);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            logger.LogWarning(failure, "Room lifecycle maintenance will resume next round ({ErrorType})", exception.GetType().Name);
        }
    }

    private async Task CleanUpConnectionsAsync(CancellationToken cancellationToken)
    {
        var roomIds = connections.GetRoomIds();
        var index = Array.BinarySearch(roomIds, lastInspectedRoom);
        var start = index >= 0 ? index + 1 : ~index;
        // 下一輪接續上次完成的批次，避免總從同一批房間開始掃描。
        foreach (var batch in roomIds.Skip(start).Concat(roomIds.Take(start)).Chunk(CleanUpRooms.MaxBatchSize))
        {
            cancellationToken.ThrowIfCancellationRequested();
            connections.AbortRooms(await store.FindUnavailableRoomsAsync(batch, cancellationToken));
            lastInspectedRoom = batch[^1];
        }
    }

    private async Task ReclaimCodesAsync(CancellationToken cancellationToken)
    {
        int deleted;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            deleted = await store.ReclaimCodesAsync(CleanUpRooms.MaxBatchSize, cancellationToken);
            if (deleted > 0)
                logger.LogInformation("Room code cleanup reclaimed {ReclaimedCodes} reservations", deleted);
        }
        while (deleted == CleanUpRooms.MaxBatchSize);
    }
}
