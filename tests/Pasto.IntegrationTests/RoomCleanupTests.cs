using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pasto.Server.Application.Items;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Persistence;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class RoomCleanupTests
{
    [Fact]
    public async Task Batch_DeletesOldestAcrossBothCategoriesAndPreservesActiveRoomAndReservations()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var create = TestRoomCreation.Create(database,
            new SequenceRoomSecrets(Enumerable.Range(0, 103).Select(i => $"CL{i:D4}").ToArray()));
        var invalidationStart = DateTime.UtcNow.AddHours(-3);
        var rooms = new List<CreatedRoom>();
        await using (var setup = database.CreateContext())
        {
            for (var i = 0; i < 102; i++)
            {
                var room = await create.ExecuteAsync();
                rooms.Add(room);
                var invalidatedAt = invalidationStart.AddMinutes(i);
                if (i % 2 == 0)
                    await setup.Rooms.Where(x => x.Id == room.RoomId).ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.CreatedAt, invalidatedAt.AddHours(-5))
                        .SetProperty(x => x.ExpiresAt, invalidatedAt));
                else
                    await setup.Rooms.Where(x => x.Id == room.RoomId).ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.CreatedAt, invalidatedAt.AddMinutes(-1))
                        .SetProperty(x => x.DeletedAt, invalidatedAt));
            }
        }

        var active = await create.ExecuteAsync();
        var cleanup = new RoomCleanupStore(database.CreateContextFactory());
        var backlog = await cleanup.MeasureBacklogAsync(default);
        Assert.Equal(102, backlog.PendingRooms);
        Assert.Equal(invalidationStart.Ticks / 10, backlog.OldestInvalidatedAt!.Value.Ticks / 10);
        Assert.Equal(100, await cleanup.DeleteBatchAsync(100, default));

        await using var verification = database.CreateContext();
        Assert.Equal(new[] { active.RoomId, rooms[100].RoomId, rooms[101].RoomId }.Order(),
            (await verification.Rooms.Select(x => x.Id).ToListAsync()).Order());
        Assert.Equal(2, await cleanup.DeleteBatchAsync(100, default));
        Assert.Equal(0, await cleanup.DeleteBatchAsync(100, default));
        Assert.Equal(new RoomCleanupBacklog(0, null), await cleanup.MeasureBacklogAsync(default));
        Assert.Equal(active.ParticipantId, (await verification.Participants.SingleAsync()).Id);
        Assert.Equal(103, await verification.CodeReservations.CountAsync());
    }

    [Fact]
    public async Task Batch_SkipsLockedOldestRoomAndCleansItAfterRelease()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var create = TestRoomCreation.Create(database, new SequenceRoomSecrets("CLEAN1", "CLEAN2"));
        var oldest = await create.ExecuteAsync();
        var next = await create.ExecuteAsync();
        await CloseAsync(database, oldest);
        await CloseAsync(database, next);

        var cleanup = new RoomCleanupStore(database.CreateContextFactory());
        await using var blocker = database.CreateContext();
        await using (var transaction = await blocker.Database.BeginTransactionAsync())
        {
            await blocker.Rooms.FromSqlInterpolated($"SELECT * FROM rooms WHERE id = {oldest.RoomId} FOR UPDATE")
                .AsNoTracking().ToListAsync();
            // 候選上限為 1 時也要選到第二房，證明先跳鎖再套用 LIMIT。
            Assert.Equal(1, await cleanup.DeleteBatchAsync(1, default));
            Assert.Equal(oldest.RoomId, (await blocker.Rooms.AsNoTracking().SingleAsync()).Id);
            Assert.Equal(0, await cleanup.DeleteBatchAsync(1, default));
            await transaction.CommitAsync();
        }

        Assert.Equal(1, await cleanup.DeleteBatchAsync(1, default));
        Assert.Empty(await blocker.Rooms.ToListAsync());
    }

    [Fact]
    public async Task Batch_DeferredCommitFailureRollsBackAllCascadesAndAllowsRetry()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var create = TestRoomCreation.Create(database, new SequenceRoomSecrets("CLEAN1", "CLEAN2"));
        var first = await create.ExecuteAsync();
        var second = await create.ExecuteAsync();
        var add = new AddItem(new ItemAdditionStore(database.CreateContextFactory()));
        foreach (var room in new[] { first, second })
        {
            await add.ExecuteAsync(room.RoomId, room.ParticipantId, "test item");
            await CloseAsync(database, room);
        }

        await using var verification = database.CreateContext();
        await verification.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_cleanup() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD.code = 'CLEAN2' THEN
                    RAISE EXCEPTION 'test cleanup failure';
                END IF;
                RETURN NULL;
            END;
            $$;
            CREATE CONSTRAINT TRIGGER fail_cleanup_at_commit
            AFTER DELETE ON rooms DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION fail_cleanup();
            """);

        var cleanup = new RoomCleanupStore(database.CreateContextFactory());
        var exception = await Assert.ThrowsAsync<PostgresException>(() => cleanup.DeleteBatchAsync(100, default));
        Assert.Equal(PostgresErrorCodes.RaiseException, exception.SqlState);
        Assert.Equal(2, await verification.Rooms.CountAsync());
        Assert.Equal(2, await verification.Participants.CountAsync());
        Assert.Equal(2, await verification.Items.CountAsync());

        await verification.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_cleanup_at_commit ON rooms");
        Assert.Equal(2, await cleanup.DeleteBatchAsync(100, default));
        Assert.Empty(await verification.Rooms.ToListAsync());
        Assert.Empty(await verification.Participants.ToListAsync());
        Assert.Empty(await verification.Items.ToListAsync());
        Assert.Equal(2, await verification.CodeReservations.CountAsync());
    }

    [Fact]
    public async Task Round_ContinuesAfterBacklogMeasurementFailureAndCleansMultipleBatches()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var create = TestRoomCreation.Create(database,
            new SequenceRoomSecrets(Enumerable.Range(0, 101).Select(i => $"CL{i:D4}").ToArray()));
        for (var i = 0; i < 101; i++)
            await create.ExecuteAsync();

        await using var setup = database.CreateContext();
        await setup.Rooms.ExecuteUpdateAsync(update => update.SetProperty(x => x.DeletedAt, DateTime.UtcNow));

        var store = new RoomCleanupStore(database.CreateContextFactory());
        var round = new CleanUpRooms(new BacklogMeasurementFailure(store), new StorageFailureClassifier(),
            NullLogger<CleanUpRooms>.Instance);
        await round.ExecuteAsync(new RoomCleanupProgress(), default);

        Assert.Empty(await setup.Rooms.ToListAsync());
        Assert.Equal(101, await setup.CodeReservations.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_TimeoutRollsBackAndLeavesRoomForNextRound(bool useClientTimeout)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await TestRoomCreation.Create(database, new SequenceRoomSecrets("CLEAN1")).ExecuteAsync();
        await CloseAsync(database, room);
        await using var verification = database.CreateContext();
        await verification.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION slow_cleanup() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                PERFORM pg_sleep(10);
                RETURN NULL;
            END;
            $$;
            CREATE TRIGGER slow_cleanup
            AFTER DELETE ON rooms FOR EACH ROW EXECUTE FUNCTION slow_cleanup();
            """);

        var contextFactory = useClientTimeout
            ? database.CreateContextFactory(new ShortDeleteCommandTimeout())
            : database.CreateContextFactory();
        var cleanup = new RoomCleanupStore(contextFactory);
        var failure = await Assert.ThrowsAsync<RoomCleanupBatchTimeoutException>(() => cleanup.DeleteBatchAsync(100, default));
        if (useClientTimeout)
        {
            Assert.IsType<TimeoutException>(failure.GetBaseException());
        }
        else
        {
            var postgres = Assert.IsType<PostgresException>(failure.GetBaseException());
            Assert.Equal(PostgresErrorCodes.QueryCanceled, postgres.SqlState);
        }
        Assert.Equal(room.RoomId, (await verification.Rooms.SingleAsync()).Id);
        Assert.Equal(room.ParticipantId, (await verification.Participants.SingleAsync()).Id);

        await verification.Database.ExecuteSqlRawAsync("DROP TRIGGER slow_cleanup ON rooms");
        Assert.Equal(1, await cleanup.DeleteBatchAsync(100, default));
    }

    [Fact]
    public async Task Round_ShrinksAfterPostgresTimeoutAndRecoversAfterCommittedFullBatch()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var create = TestRoomCreation.Create(database,
            new SequenceRoomSecrets(Enumerable.Range(0, 55).Select(i => $"CL{i:D4}").ToArray()));
        for (var i = 0; i < 55; i++)
            await create.ExecuteAsync();

        await using var setup = database.CreateContext();
        await setup.Rooms.ExecuteUpdateAsync(update => update.SetProperty(x => x.DeletedAt, DateTime.UtcNow));
        await setup.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION delay_large_cleanup_batch() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF (SELECT count(*) FROM deleted_rooms) > 50 THEN
                    PERFORM pg_sleep(8);
                END IF;
                RETURN NULL;
            END;
            $$;
            CREATE TRIGGER delay_large_cleanup_batch AFTER DELETE ON rooms
            REFERENCING OLD TABLE AS deleted_rooms
            FOR EACH STATEMENT EXECUTE FUNCTION delay_large_cleanup_batch();
            """);

        var store = new ObservedBatches(new RoomCleanupStore(database.CreateContextFactory()));
        var round = new CleanUpRooms(store, new StorageFailureClassifier(), NullLogger<CleanUpRooms>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await round.ExecuteAsync(new RoomCleanupProgress(), deadline.Token);
        Assert.Equal(new[] { 100, 50, 60 }, store.Sizes);
        Assert.Empty(await setup.Rooms.ToListAsync());
        Assert.Empty(await setup.Participants.ToListAsync());
        Assert.Equal(55, await setup.CodeReservations.CountAsync());

        await round.ExecuteAsync(new RoomCleanupProgress(), deadline.Token);
        Assert.Equal(new[] { 100, 50, 60, 60 }, store.Sizes);
    }

    [Fact]
    public async Task Round_StopsAfterOneSingleRoomTimeoutAndKeepsSizeForNextRound()
    {
        var store = new ScriptedCleanupStore(_ =>
            throw new RoomCleanupBatchTimeoutException(new TimeoutException()));
        var batches = new ObservedBatches(store);
        var round = new CleanUpRooms(batches, new StorageFailureClassifier(), NullLogger<CleanUpRooms>.Instance);

        await Assert.ThrowsAsync<RoomCleanupBatchTimeoutException>(() => round.ExecuteAsync(new RoomCleanupProgress(), default));
        Assert.Equal(new[] { 100, 50, 25, 12, 6, 3, 1 }, batches.Sizes);

        await Assert.ThrowsAsync<RoomCleanupBatchTimeoutException>(() => round.ExecuteAsync(new RoomCleanupProgress(), default));
        Assert.Equal(new[] { 100, 50, 25, 12, 6, 3, 1, 1 }, batches.Sizes);
    }

    [Fact]
    public async Task EnabledHost_UnexpectedCleanupErrorsDoNotStopApiAndSuccessResetsFailureCount()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await TestRoomCreation.Create(database, new SequenceRoomSecrets("CLEAN1")).ExecuteAsync();
        var time = new ManualCleanupTimeProvider();
        using var logs = new CleanupLogProvider();
        const string privateText = "private cleanup exception message";
        var store = new ScriptedCleanupStore(attempt => attempt == 3 ? 0 : throw new InvalidOperationException(privateText));
        await using var factory = new PastoApiFactory(database.ConnectionString,
            new Dictionary<string, string?> { ["Cleanup:Enabled"] = "true" },
            services =>
            {
                services.AddSingleton<TimeProvider>(time);
                services.AddSingleton<IRoomCleanupStore>(store);
            }, logs);
        using var client = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var first = await logs.RoundFailures.Reader.ReadAsync(deadline.Token);
        time.Tick();
        var second = await logs.RoundFailures.Reader.ReadAsync(deadline.Token);
        time.Tick();
        await logs.BatchCompleted.Task.WaitAsync(deadline.Token);
        time.Tick();
        var afterRecovery = await logs.RoundFailures.Reader.ReadAsync(deadline.Token);

        Assert.Equal(new[] { 1, 2, 1 }, new[] { first.Count, second.Count, afterRecovery.Count });
        foreach (var failure in new[] { first, second, afterRecovery })
        {
            Assert.Equal(LogLevel.Error, failure.Level);
            Assert.Null(failure.Exception);
            Assert.Contains(nameof(InvalidOperationException), failure.Message);
            Assert.Contains(nameof(ScriptedCleanupStore), failure.Message);
            Assert.DoesNotContain(privateText, failure.Message);
        }

        Assert.False(factory.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/rooms/{room.RoomId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", room.Credential);
        using var response = await client.SendAsync(request, deadline.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task EnabledHost_CatchesUpOnStartupAndRejectsOldCredential()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await TestRoomCreation.Create(database, new SequenceRoomSecrets("CLEAN1")).ExecuteAsync();
        await new AddItem(new ItemAdditionStore(database.CreateContextFactory()))
            .ExecuteAsync(room.RoomId, room.ParticipantId, "test item");
        await CloseAsync(database, room);

        using var logs = new CleanupLogProvider();
        await using var factory = new PastoApiFactory(database.ConnectionString,
            new Dictionary<string, string?> { ["Cleanup:Enabled"] = "true", ["Cleanup:MaxPoolSize"] = "2" },
            logs: logs);
        using var client = factory.CreateClient();
        await logs.BatchCommitted.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var apiSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        var workerSource = factory.Services.GetRequiredKeyedService<NpgsqlDataSource>("RoomCleanup");
        Assert.NotSame(apiSource, workerSource);
        Assert.Equal(64, new NpgsqlConnectionStringBuilder(apiSource.ConnectionString).MaxPoolSize);
        Assert.Equal(2, new NpgsqlConnectionStringBuilder(workerSource.ConnectionString).MaxPoolSize);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/rooms/{room.RoomId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", room.Credential);
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await using var verification = database.CreateContext();
        Assert.Empty(await verification.Rooms.ToListAsync());
        Assert.Empty(await verification.Participants.ToListAsync());
        Assert.Empty(await verification.Items.ToListAsync());
        Assert.Equal(room.ExpiresAt.AddHours(24), (await verification.CodeReservations.SingleAsync()).ReusableAfter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnabledHost_BudgetExhaustionOnlyCountsAsFailureWithoutProgress(bool priorFailure)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var time = new ManualCleanupTimeProvider();
        using var logs = new CleanupLogProvider();
        var waiting = Channel.CreateUnbounded<int>();
        var firstProgressBatch = priorFailure ? 2 : 1;
        var store = new ScriptedCleanupStore(async (attempt, cancellationToken) =>
        {
            if (attempt >= firstProgressBatch && attempt < firstProgressBatch + 2)
                return 100;

            waiting.Writer.TryWrite(attempt);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        });
        await using var factory = new PastoApiFactory(database.ConnectionString,
            new Dictionary<string, string?> { ["Cleanup:Enabled"] = "true" },
            services =>
            {
                services.AddSingleton<TimeProvider>(time);
                services.AddSingleton<IRoomCleanupStore>(store);
            }, logs);
        using var client = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var timedOut = new BudgetLog(6002, LogLevel.Warning, 1, null, null);
        if (priorFailure)
        {
            Assert.Equal(1, await waiting.Reader.ReadAsync(deadline.Token));
            time.Advance(TimeSpan.FromSeconds(20));
            Assert.Equal(timedOut, await logs.BudgetEvents.Reader.ReadAsync(deadline.Token));
            time.Advance(TimeSpan.FromSeconds(40));
        }

        Assert.Equal(firstProgressBatch + 2, await waiting.Reader.ReadAsync(deadline.Token));
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(new BudgetLog(6006, LogLevel.Warning, null, 2, 200),
            await logs.BudgetEvents.Reader.ReadAsync(deadline.Token));

        time.Advance(TimeSpan.FromSeconds(40));
        Assert.Equal(firstProgressBatch + 3, await waiting.Reader.ReadAsync(deadline.Token));
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(timedOut, await logs.BudgetEvents.Reader.ReadAsync(deadline.Token));
    }

    private static Task<ClosedRoom> CloseAsync(PostgresTestDatabase database, CreatedRoom room)
        => new CloseRoom(new RoomClosureStore(database.CreateContextFactory())).ExecuteAsync(room.RoomId, room.ParticipantId);

    private sealed class ShortDeleteCommandTimeout : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("DELETE FROM rooms", StringComparison.Ordinal))
                command.CommandTimeout = 1;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BacklogMeasurementFailure(IRoomCleanupStore store) : IRoomCleanupStore
    {
        public Task<RoomCleanupBacklog> MeasureBacklogAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("Wrapped storage failure", new NpgsqlException("Test timeout"));

        public Task<int> DeleteBatchAsync(int batchSize, CancellationToken cancellationToken)
            => store.DeleteBatchAsync(batchSize, cancellationToken);
    }

    private sealed class ObservedBatches(IRoomCleanupStore store) : IRoomCleanupStore
    {
        public List<int> Sizes { get; } = [];

        public Task<RoomCleanupBacklog> MeasureBacklogAsync(CancellationToken cancellationToken)
            => store.MeasureBacklogAsync(cancellationToken);

        public Task<int> DeleteBatchAsync(int batchSize, CancellationToken cancellationToken)
        {
            Sizes.Add(batchSize);
            return store.DeleteBatchAsync(batchSize, cancellationToken);
        }
    }

    private sealed class ScriptedCleanupStore(Func<int, CancellationToken, Task<int>> deleteOnAttempt) : IRoomCleanupStore
    {
        private int attempts;

        public ScriptedCleanupStore(Func<int, int> deleteOnAttempt)
            : this((attempt, _) => Task.FromResult(deleteOnAttempt(attempt))) { }

        public Task<RoomCleanupBacklog> MeasureBacklogAsync(CancellationToken cancellationToken)
            => Task.FromResult(new RoomCleanupBacklog(0, null));

        public Task<int> DeleteBatchAsync(int batchSize, CancellationToken cancellationToken)
            => deleteOnAttempt(++attempts, cancellationToken);
    }

    private sealed class ManualCleanupTimeProvider : TimeProvider
    {
        private readonly object timerLock = new();
        private readonly List<ManualTimer> timers = [];
        private TimeSpan elapsed;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (timerLock)
            {
                var timer = new ManualTimer(this, callback, state);
                timers.Add(timer);
                timer.Change(dueTime, period);
                return timer;
            }
        }

        public void Tick() => Advance(TimeSpan.FromMinutes(1));

        public void Advance(TimeSpan duration)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
            var target = elapsed + duration;
            while (true)
            {
                ManualTimer? next;
                lock (timerLock)
                {
                    next = timers.Where(timer => timer.DueAt <= target).MinBy(timer => timer.DueAt);
                    if (next is null)
                    {
                        elapsed = target;
                        return;
                    }

                    elapsed = next.DueAt!.Value;
                    next.DueAt = next.Period > TimeSpan.Zero ? elapsed + next.Period : null;
                }

                next.Fire();
            }
        }

        private sealed class ManualTimer(ManualCleanupTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            public TimeSpan? DueAt { get; set; }
            public TimeSpan Period { get; private set; }

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.timerLock)
                {
                    if (!owner.timers.Contains(this))
                        return false;

                    DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.elapsed + dueTime;
                    Period = period;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner.timerLock)
                    owner.timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed record BudgetLog(int EventId, LogLevel Level, int? Count, int? CommittedBatches, int? DeletedRooms);

    private sealed class CleanupLogProvider : ILoggerProvider, ILogger
    {
        public TaskCompletionSource BatchCommitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BatchCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<(int Count, LogLevel Level, string Message, Exception? Exception)> RoundFailures { get; }
            = Channel.CreateUnbounded<(int, LogLevel, string, Exception?)>();
        public Channel<BudgetLog> BudgetEvents { get; } = Channel.CreateUnbounded<BudgetLog>();

        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Dispose() { }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> entries)
                return;
            if (entries.Any(entry => entry is { Key: "DeletedRooms", Value: 1 }))
                BatchCommitted.TrySetResult();
            if (entries.Any(entry => entry.Key == "DeletedRooms"))
                BatchCompleted.TrySetResult();
            if (eventId == new EventId(6001, "RoomCleanupFailed"))
            {
                var count = (int)entries.Single(entry => entry.Key == "ConsecutiveFailures").Value!;
                RoundFailures.Writer.TryWrite((count, logLevel, formatter(state, exception), exception));
            }
            if (eventId.Id is 6002 or 6006)
                BudgetEvents.Writer.TryWrite(new BudgetLog(eventId.Id, logLevel,
                    entries.SingleOrDefault(entry => entry.Key == "ConsecutiveFailures").Value as int?,
                    entries.SingleOrDefault(entry => entry.Key == "CommittedBatches").Value as int?,
                    entries.SingleOrDefault(entry => entry.Key == "DeletedRooms").Value as int?));
        }
    }
}
