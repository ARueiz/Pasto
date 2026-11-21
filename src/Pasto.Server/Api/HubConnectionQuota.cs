using Microsoft.Extensions.Options;
using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Api;

internal sealed class HubConnectionQuota(IOptions<HubAdmissionOptions> options)
{
    private readonly HubAdmissionOptions limits = options.Value;
    private readonly object gate = new();
    private readonly Dictionary<string, int> sources = new();
    private readonly Dictionary<Guid, int> rooms = new();
    private readonly Dictionary<Guid, int> participants = new();
    private int total;

    internal bool HasCapacity(string source, ParticipantIdentity identity)
    {
        lock (gate)
            return HasCapacityLocked(source, identity);
    }

    internal Lease? TryAcquire(string source, ParticipantIdentity identity, CancellationToken connectionAborted)
    {
        lock (gate)
        {
            connectionAborted.ThrowIfCancellationRequested();
            if (!HasCapacityLocked(source, identity)) return null;
            var lease = new Lease(this, source, identity);
            total++;
            sources[source] = sources.GetValueOrDefault(source) + 1;
            rooms[identity.RoomId] = rooms.GetValueOrDefault(identity.RoomId) + 1;
            participants[identity.ParticipantId] = participants.GetValueOrDefault(identity.ParticipantId) + 1;
            return lease;
        }
    }

    private bool HasCapacityLocked(string source, ParticipantIdentity identity)
        => total < limits.GlobalConnections && sources.GetValueOrDefault(source) < limits.SourceConnections &&
           rooms.GetValueOrDefault(identity.RoomId) < limits.RoomConnections &&
           participants.GetValueOrDefault(identity.ParticipantId) < limits.ParticipantConnections;

    private void Release(string source, ParticipantIdentity identity)
    {
        lock (gate)
        {
            total--;
            Decrement(sources, source);
            Decrement(rooms, identity.RoomId);
            Decrement(participants, identity.ParticipantId);
        }
    }

    private static void Decrement<TKey>(Dictionary<TKey, int> counts, TKey key) where TKey : notnull
    {
        if (counts[key] == 1) counts.Remove(key);
        else counts[key]--;
    }

    internal sealed class Lease(HubConnectionQuota owner, string source, ParticipantIdentity identity) : IDisposable
    {
        private HubConnectionQuota? quota = owner;

        public void Dispose() => Interlocked.Exchange(ref quota, null)?.Release(source, identity);
    }
}
