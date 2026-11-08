using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Pasto.Server.Domain;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class CreatorIntegrityTests
{
    [Fact]
    public async Task SameRoomCreator_CommitsThroughProductionCreateRoom()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var result = await TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM01")).ExecuteAsync();
        await using var verification = database.CreateContext();
        var room = await verification.Rooms.AsNoTracking().SingleAsync();
        var participant = await verification.Participants.AsNoTracking().SingleAsync();
        var reservation = await verification.CodeReservations.AsNoTracking().SingleAsync();
        Assert.Equal(result.RoomId, room.Id);
        Assert.Equal(result.ParticipantId, room.CreatorParticipantId);
        Assert.Equal(room.Id, participant.RoomId);
        Assert.Equal(room.CreatorParticipantId, participant.Id);
        Assert.Equal(room.Code, result.Code);
        Assert.Equal(room.Code, reservation.Code);
        Assert.Equal(room.CreatedAt, participant.CreatedAt);
        Assert.Equal(room.CreatedAt, result.CreatedAt);
        Assert.Equal(room.ExpiresAt, result.ExpiresAt);
        Assert.Equal(TimeSpan.FromHours(5), room.ExpiresAt - room.CreatedAt);
        Assert.Equal(room.ExpiresAt.AddHours(24), reservation.ReusableAfter);
        Assert.Equal(0, room.Version);
        Assert.Equal(0, room.ItemCount);
        Assert.Equal(0, room.TotalBytes);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(result.Credential)), participant.CredentialHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidCreator_IsRejectedAtCommitAndRollsBackNewRoom(bool omitCreator)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var first = await TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM01")).ExecuteAsync();
        var interceptor = new InvalidCreatorInterceptor(first.ParticipantId, omitCreator);
        var secrets = new SequenceRoomSecrets("ROOM02");
        var operation = TestRoomCreation.Create(database, secrets, 5, interceptor);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => operation.ExecuteAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal("fk_rooms_creator", exception.ConstraintName);
        Assert.Equal(3, interceptor.SuccessfulSaves);
        Assert.Equal(1, secrets.CodeCalls);

        await using var verification = database.CreateContext();
        Assert.Equal(first.RoomId, (await verification.Rooms.SingleAsync()).Id);
        Assert.Equal(first.ParticipantId, (await verification.Participants.SingleAsync()).Id);
        Assert.Equal("ROOM01", (await verification.CodeReservations.SingleAsync()).Code);
    }

    private sealed class InvalidCreatorInterceptor(Guid foreignCreatorId, bool omitCreator) : SaveChangesInterceptor
    {
        public int SuccessfulSaves { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var context = eventData.Context!;
            // 移除這次真正 Store 準備新增的房主，讓 commit 驗證「缺房主」反例。
            if (omitCreator)
            {
                foreach (var entry in context.ChangeTracker.Entries<RoomParticipant>().Where(x => x.State == EntityState.Added))
                {
                    entry.State = EntityState.Detached;
                }
            }

            return ValueTask.FromResult(result);
        }

        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            SuccessfulSaves++;
            if (!omitCreator && SuccessfulSaves == 2)
            {
                var context = eventData.Context!;
                var roomId = context.ChangeTracker.Entries<Room>().Single().Entity.Id;
                // 僅在原交易內污染 DB 的 Creator 欄位，不修改 Store 接下來要用的房主 ID。
                // 保持 FK 啟用；這是故障注入，不是另一套建房實作。
                await context.Database.ExecuteSqlAsync($"""
                    UPDATE rooms SET creator_participant_id = {foreignCreatorId} WHERE id = {roomId}
                    """, cancellationToken);
            }

            return result;
        }
    }
}
