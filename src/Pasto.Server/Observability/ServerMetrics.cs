using System.Diagnostics.Metrics;

namespace Pasto.Server.Observability;

internal sealed class ServerMetrics(IMeterFactory factory)
{
    public const string MeterName = "Pasto.Server";
    private readonly Counter<long> itemAddFailures = factory.Create(MeterName).CreateCounter<long>("pasto.item.add.failures");
    private readonly Counter<long> cleanupFailures = factory.Create(MeterName).CreateCounter<long>("pasto.cleanup.failures");

    public void ItemAddFailed() => itemAddFailures.Add(1);
    public void CleanupFailed() => cleanupFailures.Add(1);
}
