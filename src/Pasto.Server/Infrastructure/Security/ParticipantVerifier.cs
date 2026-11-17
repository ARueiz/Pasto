using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Persistence;

namespace Pasto.Server.Infrastructure.Security;

public sealed class ParticipantVerifier(IDbContextFactory<PastoDbContext> factory) : IParticipantVerifier
{
    public async Task<ParticipantIdentity?> VerifyAsync(string credential, CancellationToken cancellationToken)
    {
        if (credential.Length != 22 || credential.Any(c => c is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z'
            or >= '0' and <= '9' or '-' or '_'))) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(credential));
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        return await context.Participants.AsNoTracking().Where(x => x.CredentialHash == hash)
            .Select(x => new ParticipantIdentity(x.Id, x.RoomId)).SingleOrDefaultAsync(cancellationToken);
    }
}
