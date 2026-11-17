using System.Data;
using Microsoft.EntityFrameworkCore;
using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Infrastructure.Persistence;

public sealed class RoomStateStore(IDbContextFactory<PastoDbContext> factory) : IRoomStateStore
{
    public async Task<RoomState> GetAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var (room, now) = await RoomReadAccess.LoadAsync(context, roomId, participantId, cancellationToken);
        var state = new RoomState(room.Version, room.ItemCount, room.TotalBytes, room.ExpiresAt, now);
        await transaction.CommitAsync(cancellationToken);
        return state;
    }
}
