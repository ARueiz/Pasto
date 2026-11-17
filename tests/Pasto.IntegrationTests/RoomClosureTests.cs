using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pasto.Server.Application.Items;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Domain;
using Pasto.Server.Infrastructure.Persistence;
using Pasto.Server.Infrastructure.Security;
using Xunit;
using static Pasto.IntegrationTests.TransactionTestHooks;

namespace Pasto.IntegrationTests;

public sealed class RoomClosureTests
{
    [Fact]
    public async Task CreatorClosesRoom_OnlyChangesDeletedAtAndVersion()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var memberId = await AddParticipantAsync(database, room);
        var other = await CreateRoomAsync(database, "ROOM02");
        await CreateAdd(database).ExecuteAsync(room.RoomId, memberId, "中😀");
        await CreateAdd(database).ExecuteAsync(other.RoomId, other.ParticipantId, "other");

        var result = await CreateClose(database).ExecuteAsync(room.RoomId, room.ParticipantId);
        Assert.Equal(2, result.CommittedVersion);
        Assert.True(result.DeletedAt >= room.CreatedAt);
        await AssertStateAsync(database, room, true, 1, 7, 2);
        await AssertStateAsync(database, other, false, 1, 5, 1);
        await using var verification = database.CreateContext();
        Assert.Equal(result.DeletedAt, (await verification.Rooms.SingleAsync(x => x.Id == room.RoomId)).DeletedAt);
        Assert.Equal(2, await verification.Rooms.CountAsync());
        Assert.Equal(3, await verification.Participants.CountAsync());
        Assert.Equal(2, await verification.Items.CountAsync());
        Assert.Equal(2, await verification.CodeReservations.CountAsync());
    }

    [Fact]
    public async Task MemberCannotCloseRoom()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var memberId = await AddParticipantAsync(database, room);
        await CreateAdd(database).ExecuteAsync(room.RoomId, memberId, "text");
        await Assert.ThrowsAsync<RoomOwnershipException>(() => CreateClose(database).ExecuteAsync(room.RoomId, memberId));
        await AssertStateAsync(database, room, false, 1, 4, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidParticipant_IsRejectedBeforeCreatorCheck(bool nonexistent)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var other = await CreateRoomAsync(database, "ROOM02");
        await Assert.ThrowsAsync<RoomParticipantMismatchException>(() => CreateClose(database)
            .ExecuteAsync(room.RoomId, nonexistent ? Guid.NewGuid() : other.ParticipantId));
        await AssertStateAsync(database, room, false, 0, 0, 0);
        await AssertStateAsync(database, other, false, 0, 0, 0);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("closed")]
    [InlineData("expired")]
    public async Task UnavailableRoom_TakesPrecedenceAndNeverIncrementsVersionAgain(string state)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var memberId = await AddParticipantAsync(database, room);
        await CreateAdd(database).ExecuteAsync(room.RoomId, room.ParticipantId, "text");
        if (state == "closed")
            await CreateClose(database).ExecuteAsync(room.RoomId, room.ParticipantId);
        if (state == "expired")
        {
            await using var setup = database.CreateContext();
            await setup.Rooms.Where(x => x.Id == room.RoomId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddHours(-6))
                .SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddHours(-1)));
        }

        await using var verification = database.CreateContext();
        var before = await verification.Rooms.AsNoTracking().SingleAsync(x => x.Id == room.RoomId);
        foreach (var participantId in new[] { room.ParticipantId, memberId, Guid.NewGuid() })
            await Assert.ThrowsAsync<RoomUnavailableException>(() => CreateClose(database)
                .ExecuteAsync(state == "missing" ? Guid.NewGuid() : room.RoomId, participantId));
        var after = await verification.Rooms.AsNoTracking().SingleAsync(x => x.Id == room.RoomId);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.DeletedAt, after.DeletedAt);
        Assert.Equal(1, after.ItemCount);
        Assert.Equal(4, after.TotalBytes);
        Assert.Equal(1, await verification.Items.CountAsync());
    }

    [Fact]
    public async Task ConcurrentClose_OnlyWinnerChangesVersionAndLoserGetsUnavailable()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        await CreateAdd(database).ExecuteAsync(room.RoomId, room.ParticipantId, "text");
        var holder = new HoldAfterSaveInterceptor();
        var attempt = new LockAttemptInterceptor();
        var first = CreateClose(database, holder).ExecuteAsync(room.RoomId, room.ParticipantId);
        Task<Exception?>? secondError = null;
        try
        {
            await holder.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            secondError = Record.ExceptionAsync(() => CreateClose(database, attempt).ExecuteAsync(room.RoomId, room.ParticipantId));
            await WaitForBlockedBackendAsync(database, await attempt.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            holder.Release.TrySetResult();
            await Task.WhenAll(first, (Task?)secondError ?? Task.CompletedTask);
        }

        Assert.Equal(2, (await first).CommittedVersion);
        Assert.IsType<RoomUnavailableException>(await secondError!);
        await AssertStateAsync(database, room, true, 1, 4, 2);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("closeByMember")]
    public async Task CloseFirst_WaitingOperationRechecksClosedRoom(string operation)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var memberId = await AddParticipantAsync(database, room);
        var item = await CreateAdd(database).ExecuteAsync(room.RoomId, room.ParticipantId, "text");
        var holder = new HoldAfterSaveInterceptor();
        var attempt = new LockAttemptInterceptor();
        var close = CreateClose(database, holder).ExecuteAsync(room.RoomId, room.ParticipantId);
        Task<Exception?>? waitingError = null;
        try
        {
            await holder.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task pending = operation switch
            {
                "add" => CreateAdd(database, attempt).ExecuteAsync(room.RoomId, room.ParticipantId, "late"),
                "delete" => CreateDelete(database, attempt).ExecuteAsync(room.RoomId, room.ParticipantId, item.ItemId),
                // 即使不是房主，也應先回不可用，而非無權限。
                "closeByMember" => CreateClose(database, attempt).ExecuteAsync(room.RoomId, memberId),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };
            waitingError = Record.ExceptionAsync(() => pending);
            await WaitForBlockedBackendAsync(database, await attempt.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            holder.Release.TrySetResult();
            await Task.WhenAll(close, (Task?)waitingError ?? Task.CompletedTask);
        }

        Assert.IsType<RoomUnavailableException>(await waitingError!);
        Assert.Equal(2, (await close).CommittedVersion);
        await AssertStateAsync(database, room, true, 1, 4, 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteFirst_CompletesBeforeWaitingClose(bool delete)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var item = await CreateAdd(database).ExecuteAsync(room.RoomId, room.ParticipantId, "text");
        var holder = new HoldAfterSaveInterceptor();
        var attempt = new LockAttemptInterceptor();
        Task write = delete
            ? CreateDelete(database, holder).ExecuteAsync(room.RoomId, room.ParticipantId, item.ItemId)
            : CreateAdd(database, holder).ExecuteAsync(room.RoomId, room.ParticipantId, "中😀");
        Task<ClosedRoom>? close = null;
        try
        {
            await holder.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            close = CreateClose(database, attempt).ExecuteAsync(room.RoomId, room.ParticipantId);
            await WaitForBlockedBackendAsync(database, await attempt.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            holder.Release.TrySetResult();
            await Task.WhenAll(write, (Task?)close ?? Task.CompletedTask);
        }

        Assert.Equal(3, (await close!).CommittedVersion);
        await AssertStateAsync(database, room, true, delete ? 0 : 2, delete ? 0 : 11, 3);
        if (!delete)
        {
            await using var verification = database.CreateContext();
            Assert.Equal(2, (await verification.Items.SingleAsync(x => x.Content == "中😀")).CreatedVersion);
        }
    }

    [Fact]
    public async Task FailedClose_RollsBackBeforeWaitingAddProceeds()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        await CreateAdd(database).ExecuteAsync(room.RoomId, room.ParticipantId, "text");
        var holder = new HoldAfterSaveInterceptor();
        var failure = new FailBeforeCommitInterceptor();
        var attempt = new LockAttemptInterceptor();
        var closeError = Record.ExceptionAsync(() => CreateClose(database, holder, failure).ExecuteAsync(room.RoomId, room.ParticipantId));
        Task<AddedItem>? add = null;
        try
        {
            await holder.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            add = CreateAdd(database, attempt).ExecuteAsync(room.RoomId, room.ParticipantId, "中😀");
            await WaitForBlockedBackendAsync(database, await attempt.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            holder.Release.TrySetResult();
            await Task.WhenAll(closeError, (Task?)add ?? Task.CompletedTask);
        }

        Assert.IsType<InjectedFailureException>(await closeError);
        Assert.True(failure.ReachedCommit);
        Assert.Equal(2, (await add!).CommittedVersion);
        await AssertStateAsync(database, room, false, 2, 11, 2);
    }

    [Fact]
    public async Task LockTimeout_IsRoomBusyAndLeavesRoomOpen()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var attempt = new LockAttemptInterceptor();
        await using (var blocker = database.CreateContext())
        {
            await using var transaction = await blocker.Database.BeginTransactionAsync();
            await blocker.Rooms.FromSqlInterpolated($"SELECT * FROM rooms WHERE id = {room.RoomId} FOR UPDATE").ToListAsync();
            await Assert.ThrowsAsync<RoomBusyException>(() => CreateClose(database, attempt)
                .ExecuteAsync(room.RoomId, room.ParticipantId).WaitAsync(TimeSpan.FromSeconds(8)));
            Assert.Equal(1, attempt.Attempts);
        }
        await AssertStateAsync(database, room, false, 0, 0, 0);
        await CreateClose(database).ExecuteAsync(room.RoomId, room.ParticipantId);
        await AssertStateAsync(database, room, true, 0, 0, 1);
    }

    [Fact]
    public async Task CancellationWhileWaitingForLock_LeavesRoomOpen()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        using var cancellation = new CancellationTokenSource();
        await using (var blocker = database.CreateContext())
        {
            await using var transaction = await blocker.Database.BeginTransactionAsync();
            await blocker.Rooms.FromSqlInterpolated($"SELECT * FROM rooms WHERE id = {room.RoomId} FOR UPDATE").ToListAsync();
            var attempt = new LockAttemptInterceptor();
            var pending = CreateClose(database, attempt).ExecuteAsync(room.RoomId, room.ParticipantId, cancellation.Token);
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
        await AssertStateAsync(database, room, false, 0, 0, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationAfterSave_RestoresRoomState(bool cancel)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        await CreateAdd(database).ExecuteAsync(room.RoomId, room.ParticipantId, "text");
        using var cancellation = new CancellationTokenSource();
        var failure = new FailOrCancelAfterSaveInterceptor(cancel ? cancellation : null);
        var pending = CreateClose(database, failure).ExecuteAsync(room.RoomId, room.ParticipantId, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else await Assert.ThrowsAsync<InjectedFailureException>(() => pending);
        Assert.Equal(1, failure.Saves);
        await AssertStateAsync(database, room, false, 1, 4, 1);
    }

    [Fact]
    public async Task FailureBeforeCommit_RestoresRoomState()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var failure = new FailBeforeCommitInterceptor();
        await Assert.ThrowsAsync<InjectedFailureException>(() => CreateClose(database, failure).ExecuteAsync(room.RoomId, room.ParticipantId));
        Assert.True(failure.ReachedCommit);
        await AssertStateAsync(database, room, false, 0, 0, 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureOrCancellationAfterCommit_DoesNotRetryOrReopenRoom(bool cancel)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        using var cancellation = new CancellationTokenSource();
        var failure = new FailAfterCommitInterceptor(cancel ? cancellation : null);
        var pending = CreateClose(database, failure).ExecuteAsync(room.RoomId, room.ParticipantId, cancellation.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else await Assert.ThrowsAsync<InjectedFailureException>(() => pending);
        Assert.Equal(1, failure.Commits);
        await AssertStateAsync(database, room, true, 0, 0, 1);
    }

    [Fact]
    public async Task PrecanceledRequest_DoesNotCallStore()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CloseRoom(new UnexpectedStore())
            .ExecuteAsync(Guid.NewGuid(), Guid.NewGuid(), cancellation.Token));
    }

    private static CloseRoom CreateClose(PostgresTestDatabase database, params IInterceptor[] interceptors)
        => new(new RoomClosureStore(database.CreateContextFactory(interceptors)));
    private static AddItem CreateAdd(PostgresTestDatabase database, params IInterceptor[] interceptors)
        => new(new ItemAdditionStore(database.CreateContextFactory(interceptors)));
    private static DeleteOwnItem CreateDelete(PostgresTestDatabase database, params IInterceptor[] interceptors)
        => new(new ItemDeletionStore(database.CreateContextFactory(interceptors)));
    private static Task<CreatedRoom> CreateRoomAsync(PostgresTestDatabase database, string code = "ROOM01")
        => TestRoomCreation.Create(database, new SequenceRoomSecrets(code)).ExecuteAsync();

    private static async Task<Guid> AddParticipantAsync(PostgresTestDatabase database, CreatedRoom room)
    {
        // Join 尚未實作，只準備同房身分；文字仍全部經正式 AddItem。
        var id = Guid.NewGuid();
        await using var setup = database.CreateContext();
        setup.Participants.Add(new RoomParticipant
        {
            Id = id,
            RoomId = room.RoomId,
            CredentialHash = new RoomSecrets().GenerateCredential().Hash,
            CreatedAt = room.CreatedAt
        });
        await setup.SaveChangesAsync();
        return id;
    }

    private static async Task AssertStateAsync(PostgresTestDatabase database, CreatedRoom created,
        bool closed, int count, long bytes, long version)
    {
        await using var context = database.CreateContext();
        var room = await context.Rooms.SingleAsync(x => x.Id == created.RoomId);
        var items = await context.Items.Where(x => x.RoomId == created.RoomId).ToListAsync();
        Assert.Equal(closed, room.DeletedAt is not null);
        Assert.Equal(created.CreatedAt, room.CreatedAt);
        Assert.Equal(created.ExpiresAt, room.ExpiresAt);
        Assert.Equal(created.ParticipantId, room.CreatorParticipantId);
        Assert.Equal(created.Code, room.Code);
        Assert.Equal(count, room.ItemCount);
        Assert.Equal(count, items.Count);
        Assert.Equal(bytes, room.TotalBytes);
        Assert.Equal(bytes, items.Sum(x => (long)Encoding.UTF8.GetByteCount(x.Content)));
        Assert.Equal(version, room.Version);
        var reservation = await context.CodeReservations.SingleAsync(x => x.Code == created.Code);
        Assert.Equal(created.ExpiresAt.AddHours(24), reservation.ReusableAfter);
    }

    private sealed class UnexpectedStore : IRoomClosureStore
    {
        public Task<ClosedRoom> CloseAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Canceled input must not reach persistence.");
    }
}
