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

public sealed class ItemAdditionTests
{
    [Theory]
    [InlineData("empty", ItemContentError.Empty)]
    [InlineData("whitespace", ItemContentError.Empty)]
    [InlineData("nul", ItemContentError.InvalidUnicode)]
    [InlineData("highSurrogate", ItemContentError.InvalidUnicode)]
    [InlineData("lowSurrogate", ItemContentError.InvalidUnicode)]
    [InlineData("asciiTooLarge", ItemContentError.TooLarge)]
    [InlineData("unicodeTooLarge", ItemContentError.TooLarge)]
    public async Task InvalidContent_IsRejectedBeforeCallingStore(string scenario, ItemContentError error)
    {
        var content = scenario switch
        {
            "empty" => "",
            "whitespace" => " \t\r\n　",
            "nul" => "a\0b",
            "highSurrogate" => "a\ud800",
            "lowSurrogate" => "\udc00b",
            "asciiTooLarge" => new string('x', 8193),
            "unicodeTooLarge" => new string('中', 2731),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        var exception = await Assert.ThrowsAsync<InvalidItemContentException>(() =>
            new AddItem(new UnexpectedStore()).ExecuteAsync(Guid.NewGuid(), Guid.NewGuid(), content));
        Assert.Equal(error, exception.Error);
    }

    [Fact]
    public async Task PrecanceledRequest_DoesNotCallStore()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AddItem(new UnexpectedStore())
            .ExecuteAsync(Guid.NewGuid(), Guid.NewGuid(), "text", cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Add_PreservesOriginalContentAndCommitsStatistics(bool byteBoundary)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var content = byteBoundary ? new string('中', 2729) + "😀x" : "  原文 😀\r\n\t結尾  ";
        var bytes = Encoding.UTF8.GetByteCount(content);
        if (byteBoundary) Assert.Equal(8192, bytes);
        var result = await CreateAdd(database).ExecuteAsync(room.RoomId, room.ParticipantId, content);
        await using var verification = database.CreateContext();
        var item = await verification.Items.SingleAsync();
        Assert.Equal(result.ItemId, item.Id);
        Assert.Equal(content, item.Content);
        Assert.Equal(bytes, item.ContentBytes);
        Assert.Equal(room.ParticipantId, item.OwnerParticipantId);
        Assert.Equal(room.RoomId, item.RoomId);
        Assert.Equal(1, result.CommittedVersion);
        Assert.Equal(result.CommittedVersion, item.CreatedVersion);
        Assert.True(item.CreatedAt >= room.CreatedAt);
        await AssertStatisticsAsync(database, room.RoomId, 1, bytes, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ParticipantMustBelongToRoom_BeforeCapacityCheck(bool nonexistent)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var other = await TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM02")).ExecuteAsync();
        await FillAsync(database, room, 300, "x");
        await Assert.ThrowsAsync<RoomParticipantMismatchException>(() => CreateAdd(database)
            .ExecuteAsync(room.RoomId, nonexistent ? Guid.NewGuid() : other.ParticipantId, "text"));
        await AssertStatisticsAsync(database, room.RoomId, 300, 300, 300);
        await AssertStatisticsAsync(database, other.RoomId, 0, 0, 0);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("closed")]
    [InlineData("expired")]
    public async Task UnavailableRoom_TakesPrecedenceOverParticipantMismatch(string state)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        await using (var setup = database.CreateContext())
        {
            if (state == "closed")
                await setup.Rooms.Where(x => x.Id == room.RoomId).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, DateTime.UtcNow));
            if (state == "expired")
                await setup.Rooms.Where(x => x.Id == room.RoomId).ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddHours(-6))
                    .SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddHours(-1)));
        }

        await Assert.ThrowsAsync<RoomUnavailableException>(() => CreateAdd(database)
            .ExecuteAsync(state == "missing" ? Guid.NewGuid() : room.RoomId, Guid.NewGuid(), "text"));
        await AssertStatisticsAsync(database, room.RoomId, 0, 0, 0);
    }

    [Fact]
    public async Task TwoHundredMaximumItems_ExceedOldByteQuotaAndRemainValid()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        await FillAsync(database, room, 200, new string('x', 8192));
        await AssertStatisticsAsync(database, room.RoomId, 200, 200L * 8192, 200);
    }

    [Fact]
    public async Task NonCreatorParticipant_CanAddAndOwnsTheItem()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var participantId = Guid.NewGuid();
        await using (var setup = database.CreateContext())
        {
            // Join 尚未實作；只準備另一位同房 Participant，文字仍經正式 use case。
            setup.Participants.Add(new RoomParticipant
            {
                Id = participantId,
                RoomId = room.RoomId,
                CredentialHash = new RoomSecrets().GenerateCredential().Hash,
                CreatedAt = room.CreatedAt
            });
            await setup.SaveChangesAsync();
        }
        await CreateAdd(database).ExecuteAsync(room.RoomId, participantId, "text");
        await using var verification = database.CreateContext();
        Assert.Equal(participantId, (await verification.Items.SingleAsync()).OwnerParticipantId);
        await AssertStatisticsAsync(database, room.RoomId, 1, 4, 1);
    }

    [Fact]
    public async Task LastSlot_ConcurrentAddsCommitExactlyOne()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        await FillAsync(database, room, 299, "x");
        var holder = new HoldAfterSaveInterceptor();
        var waiter = new LockAttemptInterceptor();
        var first = CreateAdd(database, holder).ExecuteAsync(room.RoomId, room.ParticipantId, "first");
        Task<AddedItem>? second = null;
        try
        {
            await holder.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = CreateAdd(database, waiter).ExecuteAsync(room.RoomId, room.ParticipantId, "second");
            var pid = await waiter.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // 觀察 PostgreSQL 實際等待鎖，才釋放第一個交易，不靠 sleep 猜排程。
            await WaitForBlockedBackendAsync(database, pid);
        }
        finally
        {
            holder.Release.TrySetResult();
            await first;
            if (second is not null)
                await Assert.ThrowsAsync<RoomItemLimitReachedException>(() => second);
        }

        await AssertStatisticsAsync(database, room.RoomId, 300, 304, 300);
        await using var verification = database.CreateContext();
        Assert.False(await verification.Items.AnyAsync(x => x.Content == "second"));
    }

    [Fact]
    public async Task ConcurrentAdds_AssignDistinctVersionsAndCorrectTotals()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var barrier = new BeforeLockBarrierInterceptor(8);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            CreateAdd(database, barrier).ExecuteAsync(room.RoomId, room.ParticipantId, "中😀")));
        Assert.Equal(Enumerable.Range(1, 8).Select(x => (long)x), results.Select(x => x.CommittedVersion).Order());
        Assert.Equal(8, results.Select(x => x.ItemId).Distinct().Count());
        await AssertStatisticsAsync(database, room.RoomId, 8, 56, 8);
    }

    [Fact]
    public async Task HeldRoom_DoesNotBlockDifferentRoom()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var other = await TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM02")).ExecuteAsync();
        await using var blocker = database.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync();
        await blocker.Rooms.FromSqlInterpolated($"SELECT * FROM rooms WHERE id = {room.RoomId} FOR UPDATE").ToListAsync();
        await CreateAdd(database).ExecuteAsync(other.RoomId, other.ParticipantId, "other");
        await AssertStatisticsAsync(database, other.RoomId, 1, 5, 1);
    }

    [Fact]
    public async Task LockTimeout_IsBoundedAndLeavesNoWrites()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        await using (var blocker = database.CreateContext())
        {
            await using var transaction = await blocker.Database.BeginTransactionAsync();
            await blocker.Rooms.FromSqlInterpolated($"SELECT * FROM rooms WHERE id = {room.RoomId} FOR UPDATE").ToListAsync();
            var attempt = new LockAttemptInterceptor();
            // Npgsql EF 的預設非重試策略會將 transient SQLSTATE 包在 InvalidOperationException。
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateAdd(database, attempt).ExecuteAsync(room.RoomId, room.ParticipantId, "text").WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, Assert.IsType<PostgresException>(exception.InnerException).SqlState);
            Assert.Equal(1, attempt.Attempts);
        }
        // 同一房隨後可正常操作，失敗交易未留下鎖或統計。
        await AssertStatisticsAsync(database, room.RoomId, 0, 0, 0);
        await CreateAdd(database).ExecuteAsync(room.RoomId, room.ParticipantId, "ok");
        await AssertStatisticsAsync(database, room.RoomId, 1, 2, 1);
    }

    [Fact]
    public async Task CancellationWhileWaitingForLock_LeavesNoWrites()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        using var cancellation = new CancellationTokenSource();
        await using (var blocker = database.CreateContext())
        {
            await using var transaction = await blocker.Database.BeginTransactionAsync();
            await blocker.Rooms.FromSqlInterpolated($"SELECT * FROM rooms WHERE id = {room.RoomId} FOR UPDATE").ToListAsync();
            var waiter = new LockAttemptInterceptor();
            var pending = CreateAdd(database, waiter).ExecuteAsync(room.RoomId, room.ParticipantId, "text", cancellation.Token);
            try
            {
                var pid = await waiter.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitForBlockedBackendAsync(database, pid);
            }
            finally
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
        }
        await AssertStatisticsAsync(database, room.RoomId, 0, 0, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationAfterSave_RollsBackItemAndStatistics(bool cancel)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        using var cancellation = new CancellationTokenSource();
        var interceptor = new FailAfterSaveInterceptor(cancel ? cancellation : null);
        var operation = CreateAdd(database, interceptor);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.ExecuteAsync(room.RoomId, room.ParticipantId, "text", cancellation.Token));
        else
            await Assert.ThrowsAsync<InjectedFailureException>(() => operation.ExecuteAsync(room.RoomId, room.ParticipantId, "text"));
        Assert.Equal(1, interceptor.Saves);
        await AssertStatisticsAsync(database, room.RoomId, 0, 0, 0);
    }

    [Fact]
    public async Task FailureBeforeCommit_RollsBackItemAndStatistics()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var interceptor = new FailBeforeCommitInterceptor();
        await Assert.ThrowsAsync<InjectedFailureException>(() => CreateAdd(database, interceptor)
            .ExecuteAsync(room.RoomId, room.ParticipantId, "text"));
        Assert.True(interceptor.ReachedCommit);
        await AssertStatisticsAsync(database, room.RoomId, 0, 0, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationAfterCommit_DoesNotRetryCommittedAdd(bool cancel)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        using var cancellation = new CancellationTokenSource();
        var interceptor = new FailAfterCommitInterceptor(cancel ? cancellation : null);
        var operation = CreateAdd(database, interceptor);
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.ExecuteAsync(room.RoomId, room.ParticipantId, "text", cancellation.Token));
        else
            await Assert.ThrowsAsync<InjectedFailureException>(() => operation.ExecuteAsync(room.RoomId, room.ParticipantId, "text"));
        Assert.Equal(1, interceptor.Commits);
        await AssertStatisticsAsync(database, room.RoomId, 1, 4, 1);
    }

    private static AddItem CreateAdd(PostgresTestDatabase database, params IInterceptor[] interceptors)
        => new(new ItemAdditionStore(database.CreateContextFactory(interceptors)));

    private static Task<CreatedRoom> CreateRoomAsync(PostgresTestDatabase database)
        => TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM01")).ExecuteAsync();

    private static async Task FillAsync(PostgresTestDatabase database, CreatedRoom room, int count, string content)
    {
        var operation = CreateAdd(database);
        for (var index = 0; index < count; index++)
            await operation.ExecuteAsync(room.RoomId, room.ParticipantId, content);
    }

    private static async Task AssertStatisticsAsync(PostgresTestDatabase database, Guid roomId, int count, long bytes, long version)
    {
        await using var context = database.CreateContext();
        var room = await context.Rooms.SingleAsync(x => x.Id == roomId);
        var items = await context.Items.Where(x => x.RoomId == roomId).ToListAsync();
        Assert.Equal(count, items.Count);
        Assert.Equal(count, room.ItemCount);
        Assert.Equal(bytes, room.TotalBytes);
        Assert.Equal(bytes, items.Sum(x => (long)Encoding.UTF8.GetByteCount(x.Content)));
        Assert.Equal(version, room.Version);
        Assert.Equal(items.Count, items.Select(x => x.CreatedVersion).Distinct().Count());
        Assert.All(items, item => Assert.InRange(item.CreatedVersion, 1, version));
    }

    private static async Task WaitForBlockedBackendAsync(PostgresTestDatabase database, int pid)
    {
        await using var context = database.CreateContext();
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            if (await context.Database.SqlQuery<bool>($"SELECT cardinality(pg_blocking_pids({pid})) > 0 AS \"Value\"").SingleAsync())
                return;
            // 有界輪詢實際 DB 狀態；等待時間不作為「已發生競爭」的證據。
            await Task.Delay(10);
        }
        Assert.Fail("Second connection did not enter a PostgreSQL lock wait.");
    }

    private sealed class UnexpectedStore : IItemAdditionStore
    {
        public Task<AddedItem> AddAsync(Guid roomId, Guid participantId, string content, int contentBytes, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Invalid input must not reach persistence.");
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
            if (cancellation is not null)
            {
                cancellation.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }
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
            Commits++;
            // 明確已提交後注入，不把此測試宣稱為真實 COMMIT／cancel 網路競爭。
            cancellation?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InjectedFailureException();
        }
    }
}
