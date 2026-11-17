using System.Data.Common;
using System.Diagnostics;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Pasto.Server.Application.Items;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Domain;
using Pasto.Server.Infrastructure.Persistence;
using Pasto.Server.Infrastructure.Security;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class ItemDeletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OwnerCanDelete_RepeatDoesNotChangeStatisticsOrVersion(bool creator)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var ownerId = creator ? room.ParticipantId : await AddParticipantAsync(database, room);
        var item = await AddAsync(database, room, ownerId, "中😀");
        var retained = await AddAsync(database, room, room.ParticipantId, "keep");
        var operation = CreateDelete(database);
        var deleted = await operation.ExecuteAsync(room.RoomId, ownerId, item.ItemId);
        Assert.True(deleted.Changed);
        Assert.Equal(3, deleted.CommittedVersion);
        var repeated = await operation.ExecuteAsync(room.RoomId, ownerId, item.ItemId);
        Assert.False(repeated.Changed);
        Assert.Equal(deleted.CommittedVersion, repeated.CommittedVersion);
        await AssertStateAsync(database, room.RoomId, 1, 4, 3);
        await using var verification = database.CreateContext();
        Assert.Equal(retained.ItemId, (await verification.Items.SingleAsync()).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OthersCannotDelete_EvenCreator(bool creator)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var ownerId = await AddParticipantAsync(database, room);
        var callerId = creator ? room.ParticipantId : await AddParticipantAsync(database, room);
        var item = await AddAsync(database, room, ownerId, "text");
        await Assert.ThrowsAsync<ItemOwnershipException>(() => CreateDelete(database)
            .ExecuteAsync(room.RoomId, callerId, item.ItemId));
        await AssertStateAsync(database, room.RoomId, 1, 4, 1);
        await using var verification = database.CreateContext();
        Assert.Equal(ownerId, (await verification.Items.SingleAsync()).OwnerParticipantId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrOtherRoomItem_IsNoChangeAndDoesNotTouchOtherRoom(bool otherRoomItem)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var other = await CreateRoomAsync(database, "ROOM02");
        await AddAsync(database, room, room.ParticipantId, "keep");
        var otherItem = await AddAsync(database, other, other.ParticipantId, "other");
        var result = await CreateDelete(database).ExecuteAsync(room.RoomId, room.ParticipantId,
            otherRoomItem ? otherItem.ItemId : Guid.NewGuid());
        Assert.False(result.Changed);
        Assert.Equal(1, result.CommittedVersion);
        await AssertStateAsync(database, room.RoomId, 1, 4, 1);
        await AssertStateAsync(database, other.RoomId, 1, 5, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidParticipant_IsRejectedBeforeOwnershipOrMissingItem(bool existingItem)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var other = await CreateRoomAsync(database, "ROOM02");
        var item = await AddAsync(database, room, room.ParticipantId, "text");
        foreach (var participantId in new[] { other.ParticipantId, Guid.NewGuid() })
            await Assert.ThrowsAsync<RoomParticipantMismatchException>(() => CreateDelete(database)
                .ExecuteAsync(room.RoomId, participantId, existingItem ? item.ItemId : Guid.NewGuid()));
        await AssertStateAsync(database, room.RoomId, 1, 4, 1);
        await AssertStateAsync(database, other.RoomId, 0, 0, 0);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("closed")]
    [InlineData("expired")]
    public async Task UnavailableRoom_IsRejectedBeforeParticipantOrItemChecks(string state)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var item = await AddAsync(database, room, room.ParticipantId, "text");
        await using (var setup = database.CreateContext())
        {
            if (state == "closed")
                await setup.Rooms.Where(x => x.Id == room.RoomId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, DateTime.UtcNow));
            if (state == "expired")
                await setup.Rooms.Where(x => x.Id == room.RoomId).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddHours(-6))
                    .SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddHours(-1)));
        }
        foreach (var participantId in new[] { room.ParticipantId, Guid.NewGuid() })
            await Assert.ThrowsAsync<RoomUnavailableException>(() => CreateDelete(database)
                .ExecuteAsync(state == "missing" ? Guid.NewGuid() : room.RoomId, participantId, item.ItemId));
        await AssertStateAsync(database, room.RoomId, 1, 4, 1);
    }

    [Fact]
    public async Task ConcurrentDeleteSameItem_ChangesExactlyOnce()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var item = await AddAsync(database, room, room.ParticipantId, "text");
        var holder = new HoldAfterSaveInterceptor();
        var attempt = new LockAttemptInterceptor();
        var first = CreateDelete(database, holder).ExecuteAsync(room.RoomId, room.ParticipantId, item.ItemId);
        Task<ItemDeletionResult>? second = null;
        try
        {
            await holder.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = CreateDelete(database, attempt).ExecuteAsync(room.RoomId, room.ParticipantId, item.ItemId);
            await WaitForBlockedBackendAsync(database, await attempt.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            holder.Release.TrySetResult();
            var firstResult = await first;
            Assert.True(firstResult.Changed);
            Assert.Equal(2, firstResult.CommittedVersion);
            if (second is not null)
            {
                var secondResult = await second;
                Assert.False(secondResult.Changed);
                Assert.Equal(2, secondResult.CommittedVersion);
            }
        }
        await AssertStateAsync(database, room.RoomId, 0, 0, 2);
    }

    [Fact]
    public async Task ConcurrentDeletesOfDifferentItems_UpdateEachExactlyOnce()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var items = new List<AddedItem>();
        for (var index = 0; index < 8; index++)
            items.Add(await AddAsync(database, room, room.ParticipantId, "中😀"));
        var barrier = new BeforeLockBarrierInterceptor(8);
        var results = await Task.WhenAll(items.Select(item => CreateDelete(database, barrier)
            .ExecuteAsync(room.RoomId, room.ParticipantId, item.ItemId)));
        Assert.All(results, result => Assert.True(result.Changed));
        Assert.Equal(Enumerable.Range(9, 8).Select(x => (long)x), results.Select(x => x.CommittedVersion).Order());
        await AssertStateAsync(database, room.RoomId, 0, 0, 16);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddAndDelete_UseSameLockOrderAndKeepAccurateStatistics(bool deleteFirst)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var item = await AddAsync(database, room, room.ParticipantId, "old");
        var holder = new HoldAfterSaveInterceptor();
        var attempt = new LockAttemptInterceptor();
        Task first = deleteFirst
            ? CreateDelete(database, holder).ExecuteAsync(room.RoomId, room.ParticipantId, item.ItemId)
            : CreateAdd(database, holder).ExecuteAsync(room.RoomId, room.ParticipantId, "中😀");
        Task? second = null;
        try
        {
            await holder.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = deleteFirst
                ? CreateAdd(database, attempt).ExecuteAsync(room.RoomId, room.ParticipantId, "中😀")
                : CreateDelete(database, attempt).ExecuteAsync(room.RoomId, room.ParticipantId, item.ItemId);
            await WaitForBlockedBackendAsync(database, await attempt.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            holder.Release.TrySetResult();
            await first;
            if (second is not null) await second;
        }
        await AssertStateAsync(database, room.RoomId, 1, 7, 3);
        await using var verification = database.CreateContext();
        var retained = await verification.Items.SingleAsync();
        Assert.Equal("中😀", retained.Content);
        Assert.Equal(deleteFirst ? 3 : 2, retained.CreatedVersion);
    }

    [Fact]
    public async Task DeleteFreesCapacityForNextAdd()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var add = CreateAdd(database);
        AddedItem? last = null;
        for (var index = 0; index < 300; index++)
            last = await add.ExecuteAsync(room.RoomId, room.ParticipantId, "x");
        await CreateDelete(database).ExecuteAsync(room.RoomId, room.ParticipantId, last!.ItemId);
        var replacement = await add.ExecuteAsync(room.RoomId, room.ParticipantId, "中");
        Assert.Equal(302, replacement.CommittedVersion);
        await AssertStateAsync(database, room.RoomId, 300, 302, 302);
    }

    [Fact]
    public async Task LockTimeout_IsRoomBusyAndDoesNotDelete()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var item = await AddAsync(database, room, room.ParticipantId, "text");
        var attempt = new LockAttemptInterceptor();
        await using (var blocker = database.CreateContext())
        {
            await using var transaction = await blocker.Database.BeginTransactionAsync();
            await blocker.Rooms.FromSqlInterpolated($"SELECT * FROM rooms WHERE id = {room.RoomId} FOR UPDATE").ToListAsync();
            await Assert.ThrowsAsync<RoomBusyException>(() => CreateDelete(database, attempt)
                .ExecuteAsync(room.RoomId, room.ParticipantId, item.ItemId).WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Equal(1, attempt.Attempts);
        }
        await AssertStateAsync(database, room.RoomId, 1, 4, 1);
        Assert.True((await CreateDelete(database).ExecuteAsync(room.RoomId, room.ParticipantId, item.ItemId)).Changed);
        await AssertStateAsync(database, room.RoomId, 0, 0, 2);
    }

    [Fact]
    public async Task CancellationWhileWaitingForLock_DoesNotDelete()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var item = await AddAsync(database, room, room.ParticipantId, "text");
        using var cancellation = new CancellationTokenSource();
        await using (var blocker = database.CreateContext())
        {
            await using var transaction = await blocker.Database.BeginTransactionAsync();
            await blocker.Rooms.FromSqlInterpolated($"SELECT * FROM rooms WHERE id = {room.RoomId} FOR UPDATE").ToListAsync();
            var attempt = new LockAttemptInterceptor();
            var pending = CreateDelete(database, attempt).ExecuteAsync(room.RoomId, room.ParticipantId, item.ItemId, cancellation.Token);
            try
            {
                await WaitForBlockedBackendAsync(database, await attempt.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            finally
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
        }
        await AssertStateAsync(database, room.RoomId, 1, 4, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationAfterSave_RestoresItemAndStatistics(bool cancel)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var item = await AddAsync(database, room, room.ParticipantId, "text");
        using var cancellation = new CancellationTokenSource();
        var interceptor = new FailAfterSaveInterceptor(cancel ? cancellation : null);
        var pending = CreateDelete(database, interceptor).ExecuteAsync(room.RoomId, room.ParticipantId, item.ItemId, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else await Assert.ThrowsAsync<InjectedFailureException>(() => pending);
        Assert.Equal(1, interceptor.Saves);
        await AssertStateAsync(database, room.RoomId, 1, 4, 1);
        await using var verification = database.CreateContext();
        Assert.Equal(item.ItemId, (await verification.Items.SingleAsync()).Id);
    }

    [Fact]
    public async Task FailureBeforeCommit_RestoresItemAndStatistics()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var item = await AddAsync(database, room, room.ParticipantId, "text");
        var interceptor = new FailBeforeCommitInterceptor();
        await Assert.ThrowsAsync<InjectedFailureException>(() => CreateDelete(database, interceptor)
            .ExecuteAsync(room.RoomId, room.ParticipantId, item.ItemId));
        Assert.True(interceptor.ReachedCommit);
        await AssertStateAsync(database, room.RoomId, 1, 4, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationAfterCommit_DoesNotRetryOrRestoreDeletion(bool cancel)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var item = await AddAsync(database, room, room.ParticipantId, "text");
        using var cancellation = new CancellationTokenSource();
        var interceptor = new FailAfterCommitInterceptor(cancel ? cancellation : null);
        var pending = CreateDelete(database, interceptor).ExecuteAsync(room.RoomId, room.ParticipantId, item.ItemId, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else await Assert.ThrowsAsync<InjectedFailureException>(() => pending);
        Assert.Equal(1, interceptor.Commits);
        await AssertStateAsync(database, room.RoomId, 0, 0, 2);
    }

    [Fact]
    public async Task PrecanceledRequest_DoesNotCallStore()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DeleteOwnItem(new UnexpectedStore())
            .ExecuteAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), cancellation.Token));
    }

    private static AddItem CreateAdd(PostgresTestDatabase database, params IInterceptor[] interceptors)
        => new(new ItemAdditionStore(database.CreateContextFactory(interceptors)));
    private static DeleteOwnItem CreateDelete(PostgresTestDatabase database, params IInterceptor[] interceptors)
        => new(new ItemDeletionStore(database.CreateContextFactory(interceptors)));
    private static Task<CreatedRoom> CreateRoomAsync(PostgresTestDatabase database, string code = "ROOM01")
        => TestRoomCreation.Create(database, new SequenceRoomSecrets(code)).ExecuteAsync();
    private static Task<AddedItem> AddAsync(PostgresTestDatabase database, CreatedRoom room, Guid ownerId, string content)
        => CreateAdd(database).ExecuteAsync(room.RoomId, ownerId, content);

    private static async Task<Guid> AddParticipantAsync(PostgresTestDatabase database, CreatedRoom room)
    {
        // Join 留 S2；只準備同房身分，所有文字均經正式 AddItem。
        var participantId = Guid.NewGuid();
        await using var setup = database.CreateContext();
        setup.Participants.Add(new RoomParticipant
        {
            Id = participantId,
            RoomId = room.RoomId,
            CredentialHash = new RoomSecrets().GenerateCredential().Hash,
            CreatedAt = room.CreatedAt
        });
        await setup.SaveChangesAsync();
        return participantId;
    }

    private static async Task AssertStateAsync(PostgresTestDatabase database, Guid roomId, int count, long bytes, long version)
    {
        await using var context = database.CreateContext();
        var room = await context.Rooms.SingleAsync(x => x.Id == roomId);
        var items = await context.Items.Where(x => x.RoomId == roomId).ToListAsync();
        Assert.Equal(count, items.Count);
        Assert.Equal(count, room.ItemCount);
        Assert.Equal(bytes, room.TotalBytes);
        Assert.Equal(bytes, items.Sum(x => (long)Encoding.UTF8.GetByteCount(x.Content)));
        Assert.Equal(version, room.Version);
        Assert.All(items, item => Assert.Equal(Encoding.UTF8.GetByteCount(item.Content), item.ContentBytes));
    }

    private static async Task WaitForBlockedBackendAsync(PostgresTestDatabase database, int pid)
    {
        await using var context = database.CreateContext();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            if (await context.Database.SqlQuery<bool>($"SELECT cardinality(pg_blocking_pids({pid})) > 0 AS \"Value\"").SingleAsync())
                return;
            await Task.Delay(10); // 輪詢實際 DB 等鎖狀態，不以延遲猜測競爭已發生。
        }
        Assert.Fail("Second connection did not enter a PostgreSQL lock wait.");
    }

    private sealed class UnexpectedStore : IItemDeletionStore
    {
        public Task<ItemDeletionResult> DeleteOwnAsync(Guid roomId, Guid participantId, Guid itemId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Canceled input must not reach persistence.");
    }
    private sealed class InjectedFailureException : Exception;

    private sealed class HoldAfterSaveInterceptor : SaveChangesInterceptor
    {
        public TaskCompletionSource Saved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Saved.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            return result;
        }
    }
    private sealed class LockAttemptInterceptor : DbCommandInterceptor
    {
        public int Attempts { get; private set; }
        public TaskCompletionSource<int> Attempted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            {
                Attempts++;
                Attempted.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class BeforeLockBarrierInterceptor(int participants) : DbCommandInterceptor
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref arrivals) == participants) ready.TrySetResult();
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            }
            return result;
        }
    }
    private sealed class FailAfterSaveInterceptor(CancellationTokenSource? cancellation) : SaveChangesInterceptor
    {
        public int Saves { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            Saves++;
            cancellation?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InjectedFailureException();
        }
    }
    private sealed class FailBeforeCommitInterceptor : DbTransactionInterceptor
    {
        public bool ReachedCommit { get; private set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default)
        {
            ReachedCommit = true;
            throw new InjectedFailureException();
        }
    }
    private sealed class FailAfterCommitInterceptor(CancellationTokenSource? cancellation) : DbTransactionInterceptor
    {
        public int Commits { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            // 明確已提交後注入取消／故障，不宣稱模擬真實 COMMIT 網路競爭。
            Commits++;
            cancellation?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InjectedFailureException();
        }
    }
}
