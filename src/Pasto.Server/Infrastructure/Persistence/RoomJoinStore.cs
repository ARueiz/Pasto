using Microsoft.EntityFrameworkCore;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Domain;

namespace Pasto.Server.Infrastructure.Persistence;

public sealed class RoomJoinStore(IDbContextFactory<PastoDbContext> factory) : IRoomJoinStore
{
    private readonly RoomJoinQuota? quota;

    internal RoomJoinStore(IDbContextFactory<PastoDbContext> factory, RoomJoinQuota quota) : this(factory)
    {
        this.quota = quota;
    }

    public async Task<(Room Room, Guid ParticipantId)> JoinAsync(string code, byte[] credentialHash, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var (room, now) = await RoomWriteAccess.LockByCodeAsync(context, code, cancellationToken);
        if (await context.Participants.CountAsync(x => x.RoomId == room.Id, cancellationToken) >= RoomRules.MaxParticipantCount)
            throw new ParticipantLimitReachedException();
        quota?.EnsureAvailable(room.Id);
        var participant = new RoomParticipant
        {
            Id = Guid.NewGuid(),
            RoomId = room.Id,
            CredentialHash = credentialHash,
            CreatedAt = now
        };
        context.Participants.Add(participant);
        await context.SaveChangesAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        quota?.Charge(room.Id);
        // 不更新內容版本，不自動重送可能已提交的加入。
        await transaction.CommitAsync(cancellationToken);
        return (room, participant.Id);
    }
}
