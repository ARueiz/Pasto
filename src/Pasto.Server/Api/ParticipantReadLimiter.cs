using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Pasto.Server.Api;

// 只用驗證後的內部 Participant ID 作鍵；粗 IP／全站防護先於 AuthenticationHandler。
public sealed class ParticipantReadLimiter(IOptions<ApiOptions> settings) : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> limiter = PartitionedRateLimiter.Create<Guid, Guid>(id =>
        RateLimitPartition.GetTokenBucketLimiter(id, _ => CreationRateLimiting.Bucket(settings.Value.ReadPerParticipant)));

    public RateLimitLease Acquire(Guid participantId) => limiter.AttemptAcquire(participantId);
    public void Dispose() => limiter.Dispose();
}
