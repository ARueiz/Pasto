using System.Diagnostics;
using Microsoft.AspNetCore.SignalR;

namespace Pasto.Server.Api;

internal sealed class RoomHubFailureFilter(ILogger<RoomHubFailureFilter> logger) : IHubFilter
{
    private static readonly EventId InvocationFailed = new(6101, "RoomHubInvocationFailed");

    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocation,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        try
        {
            return await next(invocation);
        }
        catch (Exception exception) when (exception is not HubException &&
            (exception is not OperationCanceledException || !invocation.Context.ConnectionAborted.IsCancellationRequested))
        {
            logger.LogError(InvocationFailed, "Room hub invocation failed ({ExceptionType}). Frames: {StackFrames}",
                exception.GetType().FullName, new StackTrace(exception, false).ToString());
            throw;
        }
    }
}
