using System.Data;
using Microsoft.EntityFrameworkCore;
using Pasto.Server.Application.Items;

namespace Pasto.Server.Infrastructure.Persistence;

public sealed class ItemSnapshotStore(IDbContextFactory<PastoDbContext> factory) : IItemSnapshotStore
{
    public async Task<ItemSnapshot> GetAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await context.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var (room, _) = await RoomReadAccess.LoadAsync(context, roomId, participantId, cancellationToken);
        var items = await context.Items.Where(item => item.RoomId == roomId)
            .OrderByDescending(item => item.CreatedVersion).ThenByDescending(item => item.Id)
            .Select(item => new SnapshotItem(item.Id, item.Content, item.CreatedAt, item.CreatedVersion,
                item.OwnerParticipantId, item.OwnerParticipantId == participantId,
                item.OwnerParticipantId == room.CreatorParticipantId))
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ItemSnapshot(items, room.ItemCount, room.Version, room.ExpiresAt);
    }
}
