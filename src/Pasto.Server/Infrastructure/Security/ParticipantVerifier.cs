using Microsoft.EntityFrameworkCore;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Persistence;

namespace Pasto.Server.Infrastructure.Security;

public sealed class ParticipantVerifier(IDbContextFactory<PastoDbContext> factory) : IParticipantVerifier
{
    public async Task<ParticipantIdentity?> VerifyAsync(byte[] credentialHash, CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await context.Participants.AsNoTracking().Where(x => x.CredentialHash == credentialHash)
            .Select(x => new ParticipantIdentity(x.Id, x.RoomId)).SingleOrDefaultAsync(cancellationToken);
    }
}
