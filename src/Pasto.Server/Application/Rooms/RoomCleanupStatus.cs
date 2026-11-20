namespace Pasto.Server.Application.Rooms;

internal sealed record RoomCleanupSnapshot(DateTimeOffset? LastSuccessfulAt, int ConsecutiveFailures);

internal sealed class RoomCleanupStatus
{
    private RoomCleanupSnapshot snapshot = new(null, 0);

    public RoomCleanupSnapshot Read() => Volatile.Read(ref snapshot);

    // Worker 是唯一寫入者，探針一次讀取完整快照。
    public void RecordSuccess(DateTimeOffset now) => Volatile.Write(ref snapshot, new(now, 0));

    public int RecordFailure()
    {
        var next = snapshot with { ConsecutiveFailures = snapshot.ConsecutiveFailures + 1 };
        Volatile.Write(ref snapshot, next);
        return next.ConsecutiveFailures;
    }
}
