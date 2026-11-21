using System.Globalization;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Pasto.Server.Api;

internal sealed class RoomHubAdmission(HubConnectionQuota connections, TimeProvider clock) : IHubFilter
{
    internal const string ConnectionLimitCode = "HubConnectionLimitReached";
    internal const string SubscribeLimitCode = "SubscribeRateLimited";
    private static readonly object LeaseKey = new();
    private static readonly object SubscriptionQuotaKey = new();
    private readonly long origin = clock.GetTimestamp();

    public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
    {
        var caller = context.Context;
        var http = caller.GetHttpContext()!;
        var identity = ParticipantAuthenticationHandler.Identity(caller.User);
        var lease = connections.TryAcquire(CreationRateLimiting.IpKey(http), identity, caller.ConnectionAborted)
            ?? throw new HubException(ConnectionLimitCode);
        try
        {
            caller.Items[LeaseKey] = lease;
            await next(context);
            caller.ConnectionAborted.ThrowIfCancellationRequested();
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    public Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        if (context.Context.Items.TryGetValue(LeaseKey, out var item) && item is HubConnectionQuota.Lease lease)
            lease.Dispose();
        return next(context, exception);
    }

    public ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
    {
        if (context.HubMethodName == nameof(RoomHub.SubscribeRoom))
        {
            var items = context.Context.Items;
            lock (items)
            {
                if (!items.TryGetValue(SubscriptionQuotaKey, out var state))
                    items[SubscriptionQuotaKey] = state = new HubTokenBucket(3, 1d / 6);
                var bucket = (HubTokenBucket)state!;
                var now = clock.GetElapsedTime(origin);
                if (!bucket.CanConsume(now)) throw new HubException(SubscribeLimitCode);
                bucket.Consume(now);
            }
        }
        return next(context);
    }
}

internal sealed class HubNegotiateAdmission(RequestDelegate next, HubNegotiateLimiter limiter, HubConnectionQuota connections,
    IOptions<HubAdmissionOptions> options, HttpWorkAdmission admission)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var metadata = context.GetEndpoint()?.Metadata;
        if (HttpMethods.IsPost(context.Request.Method) && metadata?.GetMetadata<NegotiateMetadata>() is not null &&
            metadata.GetMetadata<HubMetadata>()?.HubType == typeof(RoomHub) &&
            context.User.Identity?.IsAuthenticated == true)
        {
            var source = CreationRateLimiting.IpKey(context);
            using var lease = admission.TryAcquire(HttpWork.ReadDelete, source, out var workRetry);
            if (lease is null)
            {
                await HttpWorkAdmission.Reject(context, workRetry).ExecuteAsync(context);
                return;
            }
            context.Response.OnStarting(() =>
            {
                lease.Dispose();
                return Task.CompletedTask;
            });
            if (limiter.Check(source) is int retryAfter)
            {
                await RejectAsync(context, retryAfter, "RateLimited", "hub_negotiate");
                return;
            }

            var identity = ParticipantAuthenticationHandler.Identity(context.User);
            if (!connections.HasCapacity(source, identity))
            {
                await RejectAsync(context, options.Value.ClientTimeoutSeconds, RoomHubAdmission.ConnectionLimitCode, "hub_connections");
                return;
            }
            await next(context);
            return;
        }
        await next(context);
    }

    private static Task RejectAsync(HttpContext context, int retryAfter, string code, string reason)
    {
        context.Features.Get<IHttpMetricsTagsFeature>()?.Tags.Add(new("pasto.rate_limit.reason", reason));
        context.Response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
        return ApiProblem.Create(context, 429, code, retryAfter).ExecuteAsync(context);
    }
}
