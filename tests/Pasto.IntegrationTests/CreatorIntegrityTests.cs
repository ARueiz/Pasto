using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pasto.Server.Domain;
using Pasto.Server.Infrastructure.Persistence;
using Xunit;
using Xunit.Abstractions;

namespace Pasto.IntegrationTests;

public sealed class CreatorIntegrityTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SameRoomCreator_CommitsThroughStagedEfWrites()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        output.WriteLine($"PostgreSQL {database.ServerVersion}; isolated schema {database.SchemaName}");
        var roomId = Guid.NewGuid();
        var creatorId = Guid.NewGuid();

        await using (var context = database.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            await InsertRoomAndParticipantAsync(context, roomId, creatorId, creatorId, "ROOM01");
            await transaction.CommitAsync();
        }

        await using var verification = database.CreateContext();
        var room = await verification.Rooms.AsNoTracking().SingleAsync();
        var participant = await verification.Participants.AsNoTracking().SingleAsync();
        var reservation = await verification.CodeReservations.AsNoTracking().SingleAsync();
        Assert.Equal(roomId, room.Id);
        Assert.Equal(creatorId, room.CreatorParticipantId);
        Assert.Equal(room.Id, participant.RoomId);
        Assert.Equal(room.CreatorParticipantId, participant.Id);
        Assert.Equal(room.Code, reservation.Code);
        Assert.Equal(TimeSpan.FromHours(5), room.ExpiresAt - room.CreatedAt);
        Assert.Equal(room.ExpiresAt.AddHours(24), reservation.ReusableAfter);
    }

    [Fact]
    public async Task CreatorFromAnotherRoom_IsRejectedAtCommitAndRollsBackNewRoom()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var firstRoomId = Guid.NewGuid();
        var firstCreatorId = Guid.NewGuid();
        await using (var context = database.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            await InsertRoomAndParticipantAsync(context, firstRoomId, firstCreatorId, firstCreatorId, "ROOM01");
            await transaction.CommitAsync();
        }

        var secondRoomId = Guid.NewGuid();
        var secondParticipantId = Guid.NewGuid();
        await using (var context = database.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            // The referenced participant exists, but belongs to the first room.
            // Staged SaveChanges succeeds because the composite creator FK is deferred.
            await InsertRoomAndParticipantAsync(
                context, secondRoomId, firstCreatorId, secondParticipantId, "ROOM02");
            var exception = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
            Assert.Equal("fk_rooms_creator", exception.ConstraintName);
        }

        await using var verification = database.CreateContext();
        Assert.False(await verification.Rooms.AnyAsync(x => x.Id == secondRoomId));
        Assert.False(await verification.Participants.AnyAsync(x => x.Id == secondParticipantId));
        Assert.False(await verification.CodeReservations.AnyAsync(x => x.Code == "ROOM02"));
        Assert.True(await verification.Rooms.AnyAsync(x => x.Id == firstRoomId));
        Assert.True(await verification.Participants.AnyAsync(x => x.Id == firstCreatorId));
    }

    // 既有 Creator 正反例的 fixture，僅替換為正式模型／Migration；正式 CreateRoom 留在 S1-03。
    // 每次 SaveChanges 僅有一個新 entity，避開 EF tracked graph 的循環排序；三次寫入仍在同交易。
    private static async Task InsertRoomAndParticipantAsync(
        PastoDbContext context, Guid roomId, Guid creatorId, Guid participantId, string code)
    {
        var createdAt = DateTime.UtcNow;
        var expiresAt = createdAt.AddHours(5);
        context.CodeReservations.Add(new RoomCodeReservation
        {
            Code = code,
            ReusableAfter = expiresAt.AddHours(24)
        });
        await context.SaveChangesAsync();

        context.Rooms.Add(new Room
        {
            Id = roomId,
            Code = code,
            CreatorParticipantId = creatorId,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt
        });
        await context.SaveChangesAsync();

        context.Participants.Add(new RoomParticipant
        {
            Id = participantId,
            RoomId = roomId,
            CredentialHash = SHA256.HashData(RandomNumberGenerator.GetBytes(16)),
            CreatedAt = createdAt
        });
        await context.SaveChangesAsync();
    }
}
