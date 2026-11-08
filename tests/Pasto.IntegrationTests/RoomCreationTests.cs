using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Domain;
using Pasto.Server.Infrastructure.Security;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class RoomCreationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CodeCollision_RetriesWithFreshTransaction(bool reservationOnly)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        if (reservationOnly)
        {
            // 模擬舊房已清除、房碼仍在冷卻；不能只查 Rooms 是否有這個碼。
            await using var setup = database.CreateContext();
            setup.CodeReservations.Add(new RoomCodeReservation { Code = "TAKEN1", ReusableAfter = DateTime.UtcNow.AddDays(1) });
            await setup.SaveChangesAsync();
        }
        else
        {
            await TestRoomCreation.Create(database, new SequenceRoomSecrets("TAKEN1")).ExecuteAsync();
        }

        var secrets = new SequenceRoomSecrets("TAKEN1", "FRESH1");
        var result = await TestRoomCreation.Create(database, secrets).ExecuteAsync();
        Assert.Equal("FRESH1", result.Code);
        Assert.Equal(2, secrets.CodeCalls);
        await using var verification = database.CreateContext();
        Assert.Equal(reservationOnly ? 1 : 2, await verification.Rooms.CountAsync());
        Assert.Equal(reservationOnly ? 1 : 2, await verification.Participants.CountAsync());
        Assert.Equal(2, await verification.CodeReservations.CountAsync());
    }

    [Fact]
    public async Task CodeCollisions_StopAtConfiguredLimitWithoutPartialData()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var existing = await TestRoomCreation.Create(database, new SequenceRoomSecrets("TAKEN1")).ExecuteAsync();
        var secrets = new SequenceRoomSecrets("TAKEN1");
        await Assert.ThrowsAsync<RoomCodeAllocationException>(() => TestRoomCreation.Create(database, secrets, 3).ExecuteAsync());
        Assert.Equal(3, secrets.CodeCalls);
        await using var verification = database.CreateContext();
        Assert.Equal(existing.RoomId, (await verification.Rooms.SingleAsync()).Id);
        Assert.Equal(existing.ParticipantId, (await verification.Participants.SingleAsync()).Id);
        Assert.Equal("TAKEN1", (await verification.CodeReservations.SingleAsync()).Code);
    }

    [Fact]
    public async Task CredentialUniqueViolation_IsNotRetriedAsCodeCollision()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var credential = new RoomSecrets().GenerateCredential();
        await TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM01") { FixedCredential = credential }).ExecuteAsync();
        var secrets = new SequenceRoomSecrets("ROOM02") { FixedCredential = credential };
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => TestRoomCreation.Create(database, secrets).ExecuteAsync());
        var postgres = Assert.IsType<PostgresException>(exception.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgres.SqlState);
        Assert.Equal("IX_room_participants_credential_hash", postgres.ConstraintName);
        Assert.Equal(1, secrets.CodeCalls);
        await using var verification = database.CreateContext();
        Assert.Equal(1, await verification.Rooms.CountAsync());
        Assert.Equal(1, await verification.Participants.CountAsync());
        Assert.Equal("ROOM01", (await verification.CodeReservations.SingleAsync()).Code);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task FailureAfterSavedStage_RollsBackAllWrites(int savedStage)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var interceptor = new FailAfterSaveInterceptor(savedStage);
        var secrets = new SequenceRoomSecrets("ROOM01");
        await Assert.ThrowsAsync<InjectedFailureException>(() =>
            TestRoomCreation.Create(database, secrets, 5, interceptor).ExecuteAsync());
        Assert.Equal(savedStage, interceptor.SuccessfulSaves);
        Assert.Equal(1, secrets.CodeCalls);
        await AssertEmptyAsync(database);
    }

    [Fact]
    public async Task FailureImmediatelyBeforeCommit_RollsBackAllWrites()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var interceptor = new FailBeforeCommitInterceptor();
        await Assert.ThrowsAsync<InjectedFailureException>(() =>
            TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM01"), 5, interceptor).ExecuteAsync());
        Assert.True(interceptor.ReachedCommit);
        await AssertEmptyAsync(database);
    }

    [Fact]
    public async Task CancellationAfterRoomInsert_RollsBackAndPropagatesCancellation()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var interceptor = new CancelAfterRoomSaveInterceptor(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM01"), 5, interceptor).ExecuteAsync(cancellation.Token));
        Assert.True(interceptor.Canceled);
        await AssertEmptyAsync(database);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureAfterCommit_IsNotRetriedAndCommittedRoomRemains(bool cancellationAfterCommit)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var secrets = new SequenceRoomSecrets("ROOM01");
        using var cancellation = new CancellationTokenSource();
        var interceptor = new FailAfterCommitInterceptor(cancellationAfterCommit ? cancellation : null);
        var operation = TestRoomCreation.Create(database, secrets, 5, interceptor);
        if (cancellationAfterCommit)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.ExecuteAsync(cancellation.Token));
            Assert.True(cancellation.IsCancellationRequested);
        }
        else
        {
            await Assert.ThrowsAsync<InjectedFailureException>(() => operation.ExecuteAsync(cancellation.Token));
        }
        Assert.Equal(1, secrets.CodeCalls);
        // 模擬 DB 已成功，但呼叫端沒有拿到成功結果；不可將它當房碼碰撞重送。
        await using var verification = database.CreateContext();
        Assert.Equal(1, await verification.Rooms.CountAsync());
        Assert.Equal(1, await verification.Participants.CountAsync());
        Assert.Equal(1, await verification.CodeReservations.CountAsync());
    }

    [Fact]
    public async Task ConcurrentCodeCollision_CommitsTwoDistinctRooms()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var barrier = new ReservationBarrierInterceptor();
        var firstSecrets = new SequenceRoomSecrets("RACE01", "UNIQ01");
        var secondSecrets = new SequenceRoomSecrets("RACE01", "UNIQ02");
        var results = await Task.WhenAll(
            TestRoomCreation.Create(database, firstSecrets, 2, barrier).ExecuteAsync(),
            TestRoomCreation.Create(database, secondSecrets, 2, barrier).ExecuteAsync());
        Assert.NotEqual(results[0].RoomId, results[1].RoomId);
        Assert.NotEqual(results[0].Code, results[1].Code);
        Assert.Single(results, x => x.Code == "RACE01");
        Assert.Equal(3, firstSecrets.CodeCalls + secondSecrets.CodeCalls);
        await using var verification = database.CreateContext();
        Assert.Equal(2, await verification.Rooms.CountAsync());
        Assert.Equal(2, await verification.Participants.CountAsync());
        Assert.Equal(2, await verification.CodeReservations.CountAsync());
    }

    private static async Task AssertEmptyAsync(PostgresTestDatabase database)
    {
        // 用新 context／連線讀 commit 後狀態，不把原本的 tracking 狀態當成 DB 證據。
        await using var verification = database.CreateContext();
        Assert.False(await verification.Rooms.AnyAsync());
        Assert.False(await verification.Participants.AnyAsync());
        Assert.False(await verification.CodeReservations.AnyAsync());
    }

    private sealed class InjectedFailureException : Exception;

    private sealed class FailAfterSaveInterceptor(int stage) : SaveChangesInterceptor
    {
        public int SuccessfulSaves { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (++SuccessfulSaves == stage)
            {
                throw new InjectedFailureException();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailBeforeCommitInterceptor : DbTransactionInterceptor
    {
        public bool ReachedCommit { get; private set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            ReachedCommit = true;
            throw new InjectedFailureException();
        }
    }

    private sealed class CancelAfterRoomSaveInterceptor(CancellationTokenSource cancellation) : SaveChangesInterceptor
    {
        private int saves;
        public bool Canceled { get; private set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (++saves == 2)
            {
                Canceled = true;
                cancellation.Cancel();
            }

            return ValueTask.FromResult(result);
        }
    }

    private sealed class FailAfterCommitInterceptor(CancellationTokenSource? cancellation) : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            // 在已知 commit 完成的明確同步點取消，證明收到取消也可能已寫入。
            // 這不模擬 Npgsql 在網路上送 COMMIT／cancel request 的實際競爭。
            cancellation?.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InjectedFailureException();
        }
    }

    private sealed class ReservationBarrierInterceptor : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<RoomCodeReservation>()
                .Any(x => x.State == EntityState.Added && x.Entity.Code == "RACE01"))
            {
                // 兩個獨立交易都準備 INSERT 才放行，不用 sleep 猜測排程。
                if (Interlocked.Increment(ref arrivals) == 2)
                {
                    ready.TrySetResult();
                }

                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }

            return result;
        }
    }
}
