using Microsoft.EntityFrameworkCore;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Domain;

namespace Pasto.Server.Infrastructure.Persistence;

// 呼叫端持有短唯讀 snapshot 交易；與寫入流程分開，不為讀取取得 room row lock。
internal static class RoomReadAccess
{
    public static async Task<(Room Room, DateTime Now)> LoadAsync(PastoDbContext context,
        Guid roomId, Guid participantId, CancellationToken cancellationToken)
    {
        if (context.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Room reads require an active snapshot transaction.");
        var room = await context.Rooms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == roomId, cancellationToken);
        var now = await context.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
        if (room is null || !RoomRules.IsAvailable(room, now)) throw new RoomUnavailableException();
        if (!await context.Participants.AnyAsync(x => x.Id == participantId && x.RoomId == roomId, cancellationToken))
            throw new RoomParticipantMismatchException();
        return (room, now);
    }
}
