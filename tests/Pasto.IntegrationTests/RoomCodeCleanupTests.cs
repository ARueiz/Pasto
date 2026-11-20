using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Domain;
using Pasto.Server.Infrastructure.Persistence;
using Pasto.Server.Infrastructure.Security;
using Xunit;
using static Pasto.IntegrationTests.TransactionTestHooks;

namespace Pasto.IntegrationTests;

public sealed class RoomCodeCleanupTests
{
    [Fact]
    public async Task Reclaim_RequiresElapsedCooldownAndNoReferencingRoom()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var create = TestRoomCreation.Create(database, new SequenceRoomSecrets("CLEAN1", "CLEAN2"));
        var first = await create.ExecuteAsync();
        var referenced = await create.ExecuteAsync();
        var close = new CloseRoom(new RoomClosureStore(database.CreateContextFactory()));
        await close.ExecuteAsync(first.RoomId, first.ParticipantId);

        var cleanup = new RoomCleanupStore(database.CreateContextFactory());
        Assert.Equal(1, await cleanup.DeleteBatchAsync(1, default));
        Assert.Equal(0, await cleanup.ReclaimCodesAsync(100, default));

        await using var context = database.CreateContext();
        Assert.Equal(first.ExpiresAt.AddHours(24), (await context.CodeReservations.AsNoTracking()
            .SingleAsync(x => x.Code == first.Code)).ReusableAfter);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE room_code_reservations SET reusable_after = clock_timestamp()
            WHERE code = {first.Code} OR code = {referenced.Code}
            """);

        Assert.Equal(1, await cleanup.ReclaimCodesAsync(100, default));
        Assert.Equal(0, await cleanup.ReclaimCodesAsync(100, default));
        Assert.Equal(referenced.Code, (await context.CodeReservations.SingleAsync()).Code);
        Assert.Equal(referenced.RoomId, (await context.Rooms.SingleAsync()).Id);
    }

    [Fact]
    public async Task Reclaim_SkipsLockedCodesAndBatchesOldestEligibleFirst()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        var oldest = DateTime.UtcNow.AddHours(-2);
        setup.CodeReservations.AddRange(Enumerable.Range(0, 102).Select(i => new RoomCodeReservation
        {
            Code = $"CL{i:D4}",
            ReusableAfter = oldest.AddSeconds(i)
        }));
        await setup.SaveChangesAsync();
        setup.ChangeTracker.Clear();

        var cleanup = new RoomCleanupStore(database.CreateContextFactory());
        await using var blocker = database.CreateContext();
        await using (var transaction = await blocker.Database.BeginTransactionAsync())
        {
            await blocker.CodeReservations.FromSqlRaw(
                "SELECT * FROM room_code_reservations WHERE code = 'CL0000' FOR UPDATE").ToListAsync();
            Assert.Equal(100, await cleanup.ReclaimCodesAsync(100, default));
            Assert.Equal(new[] { "CL0000", "CL0101" }, await setup.CodeReservations
                .OrderBy(x => x.Code).Select(x => x.Code).ToArrayAsync());
            await transaction.CommitAsync();
        }
        Assert.Equal(2, await cleanup.ReclaimCodesAsync(100, default));
        Assert.Equal(0, await cleanup.ReclaimCodesAsync(100, default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reclaim_ConcurrentCreateOnlyReusesCodeAfterCommittedReclamation(bool rollBack)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var old = await TestRoomCreation.Create(database, new SequenceRoomSecrets("CLEAN1")).ExecuteAsync();
        await new CloseRoom(new RoomClosureStore(database.CreateContextFactory())).ExecuteAsync(old.RoomId, old.ParticipantId);
        var cleanup = new RoomCleanupStore(database.CreateContextFactory());
        Assert.Equal(1, await cleanup.DeleteBatchAsync(1, default));
        await using var setup = database.CreateContext();
        await setup.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE room_code_reservations SET reusable_after = clock_timestamp() WHERE code = {old.Code}
            """);

        var atCommit = new HoldReclaimCommit(rollBack);
        var pendingReclaim = new RoomCleanupStore(database.CreateContextFactory(atCommit)).ReclaimCodesAsync(100, default);
        var insert = new ReservationInsertAttempt();
        var credential = new RoomSecrets().GenerateCredential();
        Task<Room?>? pendingCreate = null;
        try
        {
            await atCommit.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, await cleanup.ReclaimCodesAsync(100, default));
            pendingCreate = new RoomCreationStore(database.CreateContextFactory(insert))
                .TryCreateAsync(old.Code, credential.Hash, default);
            await WaitForBlockedBackendAsync(database, await insert.Backend.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            atCommit.Release.TrySetResult();
        }

        if (rollBack)
        {
            await Assert.ThrowsAsync<InjectedFailureException>(() => pendingReclaim);
            Assert.Null(await pendingCreate);
            Assert.Empty(await setup.Rooms.ToArrayAsync());
            Assert.Equal(old.Code, (await setup.CodeReservations.SingleAsync()).Code);
            return;
        }

        Assert.Equal(1, await pendingReclaim);
        var created = Assert.IsType<Room>(await pendingCreate);
        Assert.NotEqual(old.RoomId, created.Id);
        Assert.Equal(created.Id, (await setup.Rooms.SingleAsync()).Id);
        Assert.Equal(created.ExpiresAt.AddHours(24), (await setup.CodeReservations.SingleAsync()).ReusableAfter);
        Assert.Equal(0, await cleanup.ReclaimCodesAsync(100, default));

        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var oldRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/rooms/{created.Id}");
        oldRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", old.Credential);
        using var newRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/rooms/{created.Id}");
        newRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.Value);
        using var oldResponse = await client.SendAsync(oldRequest);
        using var newResponse = await client.SendAsync(newRequest);
        Assert.Equal(HttpStatusCode.Unauthorized, oldResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, newResponse.StatusCode);
    }

    [Fact]
    public async Task EnabledWorker_CatchesUpAcrossMultipleReclamationBatchesAtStartup()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var setup = database.CreateContext();
        var due = DateTime.UtcNow.AddHours(-1);
        setup.CodeReservations.AddRange(Enumerable.Range(0, 101).Select(i => new RoomCodeReservation
        {
            Code = $"CL{i:D4}",
            ReusableAfter = due
        }));
        await setup.SaveChangesAsync();

        using var logs = new ReclamationLogs();
        await using var factory = new PastoApiFactory(database.ConnectionString,
            new Dictionary<string, string?> { ["Cleanup:Enabled"] = "true" }, logs: logs);
        using var client = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        Assert.Equal(100, await logs.Committed.Reader.ReadAsync(deadline.Token));
        Assert.Equal(1, await logs.Committed.Reader.ReadAsync(deadline.Token));
        Assert.Empty(await setup.CodeReservations.ToArrayAsync(deadline.Token));
    }

    private sealed class ReclamationLogs : ILoggerProvider, ILogger
    {
        public Channel<int> Committed { get; } = Channel.CreateUnbounded<int>();

        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Dispose() { }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (state is IEnumerable<KeyValuePair<string, object?>> entries &&
                entries.SingleOrDefault(entry => entry.Key == "ReclaimedCodes").Value is int count)
                Committed.Writer.TryWrite(count);
        }
    }

    private sealed class HoldReclaimCommit(bool rollBack) : DbTransactionInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            Reached.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            if (rollBack)
                throw new InjectedFailureException();
            return result;
        }
    }

    private sealed class ReservationInsertAttempt : DbCommandInterceptor
    {
        public TaskCompletionSource<int> Backend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO room_code_reservations", StringComparison.Ordinal))
                Backend.TrySetResult(((NpgsqlConnection)command.Connection!).ProcessID);
            return ValueTask.FromResult(result);
        }
    }
}
