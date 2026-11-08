using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pasto.Server.Domain;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class OwnershipAndCascadeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ItemOwner_MustBelongToSameRoom(bool crossRoom)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var first = await TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM01")).ExecuteAsync();
        var second = await TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM02")).ExecuteAsync();
        await using (var context = database.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            // 僅準備 FK 測試資料；正式新增文字與鎖內統計流程留在 S1-04。
            var room = await context.Rooms.SingleAsync(x => x.Id == first.RoomId);
            const string content = "測試文字";
            var bytes = Encoding.UTF8.GetByteCount(content);
            context.Items.Add(new ClipboardItem
            {
                Id = Guid.NewGuid(),
                RoomId = room.Id,
                OwnerParticipantId = crossRoom ? second.ParticipantId : first.ParticipantId,
                Content = content,
                ContentBytes = bytes,
                CreatedAt = DateTime.UtcNow,
                CreatedVersion = 1
            });
            room.ItemCount = 1;
            room.TotalBytes = bytes;
            room.Version = 1;
            await context.SaveChangesAsync();
            if (crossRoom)
            {
                var exception = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
                Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
                Assert.Equal("fk_items_owner", exception.ConstraintName);
            }
            else
            {
                await transaction.CommitAsync();
            }
        }

        await using var verification = database.CreateContext();
        Assert.Equal(crossRoom ? 0 : 1, await verification.Items.CountAsync());
        var verifiedRoom = await verification.Rooms.SingleAsync(x => x.Id == first.RoomId);
        Assert.Equal(crossRoom ? 0 : 1, verifiedRoom.ItemCount);
        Assert.Equal(crossRoom ? 0L : 1L, verifiedRoom.Version);
        Assert.Equal(crossRoom ? 0L : Encoding.UTF8.GetByteCount("測試文字"), verifiedRoom.TotalBytes);
        Assert.Equal(2, await verification.Rooms.CountAsync());
        Assert.Equal(2, await verification.Participants.CountAsync());
        Assert.Equal(2, await verification.CodeReservations.CountAsync());
    }

    [Fact]
    public async Task DeleteRoom_CascadesChildrenButKeepsReservationsAndOtherRoom()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var first = await TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM01")).ExecuteAsync();
        var second = await TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM02")).ExecuteAsync();
        await using (var setup = database.CreateContext())
        {
            // 兩房都放入文字，避免「空集合本來就沒資料」造成 cascade 測試假通過。
            foreach (var created in new[] { first, second })
            {
                setup.Items.Add(new ClipboardItem
                {
                    Id = Guid.NewGuid(),
                    RoomId = created.RoomId,
                    OwnerParticipantId = created.ParticipantId,
                    Content = "fixture",
                    ContentBytes = 7,
                    CreatedAt = DateTime.UtcNow,
                    CreatedVersion = 1
                });
                var room = await setup.Rooms.SingleAsync(x => x.Id == created.RoomId);
                room.ItemCount = 1;
                room.TotalBytes = 7;
                room.Version = 1;
            }

            await setup.SaveChangesAsync();
        }

        await using (var context = database.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            // 直接刪 Room，確認真正由 DB cascade 清 children，並在 commit 通過反向 deferred FK。
            Assert.Equal(1, await context.Rooms.Where(x => x.Id == first.RoomId).ExecuteDeleteAsync());
            await transaction.CommitAsync();
        }

        await using var verification = database.CreateContext();
        Assert.Equal(second.RoomId, (await verification.Rooms.SingleAsync()).Id);
        Assert.Equal(second.ParticipantId, (await verification.Participants.SingleAsync()).Id);
        Assert.Equal(second.RoomId, (await verification.Items.SingleAsync()).RoomId);
        Assert.Equal(2, await verification.CodeReservations.CountAsync());
        var reservation = await verification.CodeReservations.SingleAsync(x => x.Code == first.Code);
        Assert.Equal(first.ExpiresAt.AddHours(24), reservation.ReusableAfter);
    }

    [Fact]
    public async Task DeleteCreatorAlone_IsRejectedAndRoomRemainsIntact()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM01")).ExecuteAsync();
        await using (var context = database.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            Assert.Equal(1, await context.Participants.Where(x => x.Id == room.ParticipantId).ExecuteDeleteAsync());
            var exception = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
            Assert.Equal("fk_rooms_creator", exception.ConstraintName);
        }

        await using var verification = database.CreateContext();
        Assert.Equal(room.RoomId, (await verification.Rooms.SingleAsync()).Id);
        Assert.Equal(room.ParticipantId, (await verification.Participants.SingleAsync()).Id);
        Assert.Equal(room.Code, (await verification.CodeReservations.SingleAsync()).Code);
    }

    [Fact]
    public async Task DeleteReservationWhileReferenced_IsRejected()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM01")).ExecuteAsync();
        await using (var context = database.CreateContext())
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() =>
                context.CodeReservations.Where(x => x.Code == room.Code).ExecuteDeleteAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
            Assert.Equal("FK_rooms_room_code_reservations_code", exception.ConstraintName);
        }

        await using var verification = database.CreateContext();
        Assert.True(await verification.CodeReservations.AnyAsync(x => x.Code == room.Code));
        Assert.True(await verification.Rooms.AnyAsync(x => x.Id == room.RoomId));
    }
}
