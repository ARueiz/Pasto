namespace Pasto.Server.Application.Rooms;

internal enum RoomTrackingResult
{
    Tracked,
    AtCapacity,
    ConnectionAborted,
    RoomMismatch
}

internal sealed class RoomConnections(ILogger<RoomConnections> logger, int capacity = 1500)
{
    private static readonly EventId TrackingFull = new(6009, "RoomConnectionTrackingFull");
    private readonly object gate = new();
    private readonly Dictionary<string, (Guid RoomId, Action Abort)> connections = new();
    private long untrackedSubscriptions;

    public RoomTrackingResult TryTrack(string connectionId, Guid roomId, CancellationToken connectionAborted, Action abort)
    {
        lock (gate)
        {
            if (connectionAborted.IsCancellationRequested)
                return RoomTrackingResult.ConnectionAborted;
            if (connections.TryGetValue(connectionId, out var tracked))
                return tracked.RoomId == roomId ? RoomTrackingResult.Tracked : RoomTrackingResult.RoomMismatch;
            if (connections.Count >= capacity)
                return RoomTrackingResult.AtCapacity;

            connections.Add(connectionId, (roomId, abort));
            return RoomTrackingResult.Tracked;
        }
    }

    public void RecordUntrackedSubscription() => Interlocked.Increment(ref untrackedSubscriptions);

    public void ReportUntrackedSubscriptions()
    {
        var count = Interlocked.Exchange(ref untrackedSubscriptions, 0);
        if (count > 0)
            logger.LogWarning(TrackingFull, "Room connection tracking was full for {UntrackedSubscriptions} subscriptions", count);
    }

    public void Untrack(string connectionId)
    {
        lock (gate)
            connections.Remove(connectionId);
    }

    public Guid[] GetRoomIds()
    {
        lock (gate)
            return connections.Values.Select(connection => connection.RoomId).Distinct().Order().ToArray();
    }

    public void AbortRooms(IReadOnlySet<Guid> roomIds)
    {
        if (roomIds.Count == 0)
            return;

        Action[] abort;
        lock (gate)
            abort = connections.Values.Where(connection => roomIds.Contains(connection.RoomId))
                .Select(connection => connection.Abort).ToArray();

        foreach (var close in abort)
        {
            try
            {
                close();
            }
            catch (Exception exception)
            {
                logger.LogWarning("Room connection cleanup failed ({ErrorType})", exception.GetType().Name);
            }
        }
    }
}
