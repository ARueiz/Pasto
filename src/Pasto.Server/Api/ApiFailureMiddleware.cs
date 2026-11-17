using Pasto.Server.Application.Items;
using Pasto.Server.Application.Rooms;

namespace Pasto.Server.Api;

public sealed class ApiFailureMiddleware(RequestDelegate next, ILogger<ApiFailureMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, IStorageFailureClassifier storageFailures)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });
        try
        {
            await next(context);
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            var (status, code) = exception switch
            {
                RoomUnavailableException => (404, "RoomUnavailable"),
                RoomParticipantMismatchException => (401, "ParticipantUnauthorized"),
                RoomOwnershipException => (403, "RoomDeleteForbidden"),
                ItemOwnershipException => (403, "ItemDeleteForbidden"),
                ParticipantLimitReachedException => (409, "ParticipantLimitReached"),
                RoomItemLimitReachedException => (409, "RoomItemLimitReached"),
                InvalidItemContentException => (400, "InvalidRequest"),
                BadHttpRequestException bad => (bad.StatusCode, bad.StatusCode == 413 ? "RequestBodyTooLarge" : "InvalidRequest"),
                RoomBusyException or RoomCodeAllocationException or OperationCanceledException => (503, "ServiceUnavailable"),
                _ when storageFailures.IsStorageFailure(exception) => (503, "ServiceUnavailable"),
                _ => (500, "InternalError")
            };
            // 不記錄例外物件／訊息、房碼、憑證或正文；保留安全 code 與追蹤識別碼。
            logger.LogError("Request failed with {ErrorCode}; trace {CorrelationId}.", code, ApiProblem.CorrelationId(context));
            context.Response.Clear();
            context.Response.Headers.CacheControl = "no-store";
            if (status == 401) context.Response.Headers.WWWAuthenticate = "Bearer";
            // Client 已斷線時無法保證送達；這不代表交易必然回滾。
            if (!context.RequestAborted.IsCancellationRequested)
                await ApiProblem.WriteAsync(context, status, code);
            else
                context.Response.StatusCode = status;
        }
    }
}
