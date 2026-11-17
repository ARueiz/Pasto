using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Pasto.Server.Application.Items;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Domain;
using Pasto.Server.Infrastructure.Persistence;
using Xunit;
using static Pasto.IntegrationTests.TransactionTestHooks;

namespace Pasto.IntegrationTests;

public sealed class RoomExpiryTests
{
    [Theory]
    [InlineData("add", -1, false)]
    [InlineData("add", 0, false)]
    [InlineData("add", 1, false)]
    [InlineData("delete", -1, false)]
    [InlineData("delete", 0, false)]
    [InlineData("delete", 1, false)]
    [InlineData("close", -1, false)]
    [InlineData("close", 0, false)]
    [InlineData("close", 1, false)]
    [InlineData("add", -1, true)]
    [InlineData("add", 0, true)]
    [InlineData("add", 1, true)]
    [InlineData("delete", -1, true)]
    [InlineData("delete", 0, true)]
    [InlineData("delete", 1, true)]
    [InlineData("close", -1, true)]
    [InlineData("close", 0, true)]
    [InlineData("close", 1, true)]
    public async Task ExactCheckpoint_OnlyBeforeExpiryAndNotClosedMayWrite(string operation, int offsetMicroseconds, bool closed)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var item = await CreateAdd(database).ExecuteAsync(room.RoomId, room.ParticipantId, "text");
        if (closed)
            await new CloseRoom(new RoomClosureStore(database.CreateContextFactory())).ExecuteAsync(room.RoomId, room.ParticipantId);

        await using var verification = database.CreateContext();
        var before = await verification.Rooms.AsNoTracking().SingleAsync();
        var clock = new BoundaryClockInterceptor(room.RoomId, offsetMicroseconds);
        var factory = database.CreateContextFactory(clock);
        var accepted = offsetMicroseconds < 0 && !closed;
        if (accepted)
            await ExecuteAsync(operation, factory, room, item.ItemId);
        else
            await Assert.ThrowsAsync<RoomUnavailableException>(() => ExecuteAsync(operation, factory, room, item.ItemId));

        Assert.Equal(1, clock.Queries);
        var after = await verification.Rooms.AsNoTracking().SingleAsync();
        var items = await verification.Items.AsNoTracking().ToListAsync();
        Assert.Equal(before.Version + (accepted ? 1 : 0), after.Version);
        Assert.Equal(accepted && operation == "add" ? 2 : accepted && operation == "delete" ? 0 : 1, after.ItemCount);
        Assert.Equal(after.ItemCount, items.Count);
        Assert.Equal(accepted && operation == "add" ? 7 : accepted && operation == "delete" ? 0 : 4, after.TotalBytes);
        Assert.Equal(after.TotalBytes, items.Sum(x => (long)x.ContentBytes));
        Assert.Equal(before.CreatedAt, after.CreatedAt);
        Assert.Equal(before.ExpiresAt, after.ExpiresAt);
        Assert.Equal(before.CreatorParticipantId, after.CreatorParticipantId);
        Assert.Equal(before.Code, after.Code);
        if (accepted && operation == "close")
            Assert.Equal(before.ExpiresAt.AddTicks(-10), after.DeletedAt); // PostgreSQL 一微秒為十個 .NET ticks。
        else
            Assert.Equal(before.DeletedAt, after.DeletedAt);

        if (accepted && operation == "add")
        {
            var added = Assert.Single(items, x => x.Id != item.ItemId);
            Assert.Equal(before.ExpiresAt.AddTicks(-10), added.CreatedAt);
            Assert.Equal(after.Version, added.CreatedVersion);
        }
        if (!accepted)
        {
            var retained = Assert.Single(items);
            Assert.Equal(item.ItemId, retained.Id);
            Assert.Equal("text", retained.Content);
            Assert.Equal(1, retained.CreatedVersion);
        }
        Assert.Equal(1, await verification.Participants.CountAsync());
        Assert.Equal(room.ExpiresAt.AddHours(24), (await verification.CodeReservations.SingleAsync()).ReusableAfter);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("delete")]
    [InlineData("close")]
    public async Task ExpiryWhileWaitingForLock_RechecksRealDatabaseTimeAndLeavesNoWrites(string operation)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var item = await CreateAdd(database).ExecuteAsync(room.RoomId, room.ParticipantId, "text");
        var attempt = new LockAttemptInterceptor();
        DateTime expiresAt;
        DateTime transactionStartedAt;
        await using (var blocker = database.CreateContext())
        {
            await using var transaction = await blocker.Database.BeginTransactionAsync();
            await blocker.Rooms.FromSqlInterpolated($"SELECT * FROM rooms WHERE id = {room.RoomId} FOR UPDATE").ToListAsync();
            var outcome = Record.ExceptionAsync(() => ExecuteAsync(operation, database.CreateContextFactory(attempt), room, item.ItemId));
            var committed = false;
            try
            {
                var pid = await attempt.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitForBlockedBackendAsync(database, pid);
                await using var observer = database.CreateContext();
                transactionStartedAt = await observer.Database.SqlQuery<DateTime>($"""
                    SELECT xact_start AS "Value" FROM pg_stat_activity WHERE pid = {pid}
                    """).SingleAsync();
                // 在已確認等鎖後，用持鎖交易將期限改為 DB 此刻，再提交釋鎖。
                // fixture 時間早於鎖後檢查、晚於等待方交易起點，能抓出誤用 now() 的實作。
                await blocker.Database.ExecuteSqlAsync($"""
                    UPDATE rooms SET created_at = clock_timestamp() - INTERVAL '5 hours',
                        expires_at = clock_timestamp() WHERE id = {room.RoomId}
                    """);
                expiresAt = await blocker.Rooms.Where(x => x.Id == room.RoomId).Select(x => x.ExpiresAt).SingleAsync();
                Assert.True(transactionStartedAt < expiresAt, "Expiry must be later than the waiting transaction's start.");
                await transaction.CommitAsync();
                committed = true;
            }
            finally
            {
                if (!committed) await transaction.RollbackAsync();
                await outcome;
            }
            Assert.IsType<RoomUnavailableException>(await outcome);
        }

        await using var verification = database.CreateContext();
        var after = await verification.Rooms.SingleAsync();
        Assert.Equal(expiresAt, after.ExpiresAt);
        Assert.Null(after.DeletedAt);
        Assert.Equal(1, after.Version);
        Assert.Equal(1, after.ItemCount);
        Assert.Equal(4, after.TotalBytes);
        var retained = await verification.Items.SingleAsync();
        Assert.Equal(item.ItemId, retained.Id);
        Assert.Equal("text", retained.Content);
        Assert.Equal(1, retained.CreatedVersion);
        Assert.Equal(1, await verification.Participants.CountAsync());
        // fixture 只改房間時間；正式保留碼仍維持建房時的原期限，不被失敗操作修改。
        Assert.Equal(room.ExpiresAt.AddHours(24), (await verification.CodeReservations.SingleAsync()).ReusableAfter);
    }

    private static Task<CreatedRoom> CreateRoomAsync(PostgresTestDatabase database)
        => TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM01")).ExecuteAsync();
    private static AddItem CreateAdd(PostgresTestDatabase database)
        => new(new ItemAdditionStore(database.CreateContextFactory()));

    private static async Task ExecuteAsync(string operation, IDbContextFactory<PastoDbContext> factory, CreatedRoom room, Guid itemId)
    {
        switch (operation)
        {
            case "add":
                await new AddItem(new ItemAdditionStore(factory)).ExecuteAsync(room.RoomId, room.ParticipantId, "new");
                break;
            case "delete":
                await new DeleteOwnItem(new ItemDeletionStore(factory)).ExecuteAsync(room.RoomId, room.ParticipantId, itemId);
                break;
            case "close":
                await new CloseRoom(new RoomClosureStore(factory)).ExecuteAsync(room.RoomId, room.ParticipantId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    // 只控制「比較時間」的精確等號語意；實際 clock_timestamp／等鎖行為由上方真時間測試證明。
    // 不攔截 reader 或偽造資料：改成在同一個真 DB 交易內讀 persisted ExpiresAt 加偏移。
    private sealed class BoundaryClockInterceptor(Guid roomId, int offsetMicroseconds) : DbCommandInterceptor
    {
        private bool lockAcquired;
        public int Queries { get; private set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
                lockAcquired = true;
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("clock_timestamp()", StringComparison.Ordinal))
            {
                Assert.True(lockAcquired, "The clock query must execute after the room lock is acquired.");
                Assert.NotNull(command.Transaction);
                Queries++;
                command.CommandText = "SELECT expires_at + INTERVAL '1 microsecond' * @expiry_offset AS \"Value\" FROM rooms WHERE id = @expiry_room";
                command.Parameters.Add(new NpgsqlParameter("expiry_offset", offsetMicroseconds));
                command.Parameters.Add(new NpgsqlParameter("expiry_room", roomId));
            }
            return ValueTask.FromResult(result);
        }
    }
}
