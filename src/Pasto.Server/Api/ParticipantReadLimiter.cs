using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;

namespace Pasto.Server.Api;

// 只用驗證後的內部 Participant ID 作鍵，包含跨房嘗試。
public sealed class ParticipantReadLimiter(IOptions<ApiOptions> settings) : IDisposable
{
    private readonly PartitionedRateLimiter<Guid> limiter = PartitionedRateLimiter.Create<Guid, Guid>(id =>
        RateLimitPartition.GetTokenBucketLimiter(id, _ => CreationRateLimiting.Bucket(settings.Value.ReadPerParticipant)));

    internal async Task<bool> AllowAsync(HttpContext http, Guid participantId)
    {
        using var lease = limiter.AttemptAcquire(participantId);
        if (lease.IsAcquired) return true;
        await CreationRateLimiting.RejectAsync(http, lease, settings.Value.ReadPerParticipant.PeriodSeconds);
        return false;
    }
    public void Dispose() => limiter.Dispose();
}
