using System.Data;
using Microsoft.EntityFrameworkCore;
using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Infrastructure.Persistence;

public sealed class ParticipantRecoveryStore(IDbContextFactory<PastoDbContext> factory) : IParticipantRecoveryStore
{
    public async Task<RecoveredParticipant> GetAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var (room, _) = await RoomReadAccess.LoadAsync(context, roomId, participantId, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new RecoveredParticipant(roomId, participantId, room.CreatorParticipantId == participantId);
    }
}
