using System.Globalization;
using Microsoft.AspNetCore.SignalR;

namespace Pasto.Server.Api;

internal sealed class RoomChangeNotifications(IHubContext<RoomHub> hub, ILogger<RoomChangeNotifications> logger)
{
    public async Task PublishAsync(Guid roomId, long version)
    {
        // 資料已提交。通知使用獨立期限，避免請求取消或慢連線影響已完成的操作。
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await hub.Clients.Group(roomId.ToString()).SendAsync("RoomChanged",
                new RoomChanged(roomId, version.ToString(CultureInfo.InvariantCulture)), deadline.Token);
        }
        catch (Exception exception)
        {
            logger.LogWarning("Room change notification failed with {ExceptionType}.", exception.GetType().Name);
        }
    }

    private sealed record RoomChanged(Guid RoomId, string Version);
}
