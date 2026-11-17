using System.Data;
using Microsoft.EntityFrameworkCore;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Domain;

namespace Pasto.Server.Infrastructure.Persistence;

public sealed class ParticipantRecoveryStore(IDbContextFactory<PastoDbContext> factory) : IParticipantRecoveryStore
{
    public async Task<RecoveredParticipant> GetAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var room = await context.Rooms.AsNoTracking().SingleOrDefaultAsync(x => x.Id == roomId, cancellationToken);
        var now = await context.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
        if (room is null || !RoomRules.IsAvailable(room, now)) throw new RoomUnavailableException();
        if (!await context.Participants.AnyAsync(x => x.Id == participantId && x.RoomId == roomId, cancellationToken))
            throw new RoomParticipantMismatchException();
        await transaction.CommitAsync(cancellationToken);
        return new RecoveredParticipant(roomId, participantId, room.CreatorParticipantId == participantId);
    }
}
