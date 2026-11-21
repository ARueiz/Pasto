using System.Globalization;
using System.Text.Json.Serialization;
using Pasto.Server.Application.Items;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Observability;

namespace Pasto.Server.Api;

internal static class ItemEndpoints
{
    public const string AddName = "AddItem";
    public const string DeleteName = "DeleteOwnItem";
    public const string SnapshotName = "GetItemSnapshot";

    public static void MapItems(this WebApplication app)
    {
        app.MapGet("/api/rooms/{roomId:guid}/items", async (Guid roomId, HttpContext context, GetItemSnapshot get,
            CancellationToken cancellationToken) =>
        {
            var identity = ParticipantAuthenticationHandler.Identity(context, roomId);
            var snapshot = await get.ExecuteAsync(roomId, identity.ParticipantId, cancellationToken);
            return Results.Ok(new
            {
                items = snapshot.Items.Select(item => new
                {
                    item.Id,
                    item.Content,
                    item.CreatedAt,
                    createdVersion = item.CreatedVersion.ToString(CultureInfo.InvariantCulture),
                    item.IsOwn,
                    canDelete = item.IsOwn,
                    authorCode = ParticipantAuthorCode.From(roomId, item.OwnerParticipantId),
                    item.AuthorIsCreator
                }),
                snapshot.ItemCount,
                snapshotVersion = snapshot.Version.ToString(CultureInfo.InvariantCulture),
                snapshot.ExpiresAt
            });
        }).WithName(SnapshotName).RequireAuthorization().LimitWork(HttpWork.ReadDelete).AddEndpointFilter<ParticipantReadFilter>();

        app.MapPost("/api/rooms/{roomId:guid}/items", async (Guid roomId, AddItemRequest request,
            HttpContext context, AddItem add, RoomChangeNotifications notifications, ServerMetrics metrics,
            IStorageFailureClassifier storageFailures,
            CancellationToken cancellationToken) =>
        {
            var identity = ParticipantAuthenticationHandler.Identity(context, roomId);
            AddedItem result;
            try
            {
                result = await add.ExecuteAsync(roomId, identity.ParticipantId, request.Content!, cancellationToken);
            }
            catch (RoomAddRateLimitedException denied)
            {
                return CreationRateLimiting.Rejection(context, denied.RetryAfterSeconds, "RateLimited", "room_add");
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                if (ApiFailureMiddleware.Classify(exception, storageFailures).Status is >= 500 and < 600)
                    metrics.ItemAddFailed();
                throw;
            }
            await notifications.PublishAsync(roomId, result.CommittedVersion);
            return Results.Json(new
            {
                item = new
                {
                    id = result.ItemId,
                    content = request.Content,
                    createdAt = result.CreatedAt,
                    createdVersion = result.CommittedVersion.ToString(CultureInfo.InvariantCulture),
                    isOwn = true,
                    canDelete = true
                },
                committedVersion = result.CommittedVersion.ToString(CultureInfo.InvariantCulture)
            }, statusCode: StatusCodes.Status201Created);
        }).WithName(AddName).RequireAuthorization().LimitWork(HttpWork.Add).AddEndpointFilter<ParticipantWriteFilter>();

        app.MapDelete("/api/rooms/{roomId:guid}/items/{itemId:guid}", async (Guid roomId, Guid itemId,
            HttpContext context, DeleteOwnItem delete, RoomChangeNotifications notifications, CancellationToken cancellationToken) =>
        {
            var identity = ParticipantAuthenticationHandler.Identity(context, roomId);
            var result = await delete.ExecuteAsync(roomId, identity.ParticipantId, itemId, cancellationToken);
            if (result.Changed) await notifications.PublishAsync(roomId, result.CommittedVersion);
            return Results.Ok(new { result.Changed, committedVersion = result.CommittedVersion.ToString(CultureInfo.InvariantCulture) });
        }).WithName(DeleteName).RequireAuthorization().LimitWork(HttpWork.ReadDelete).AddEndpointFilter<ParticipantWriteFilter>();
    }
}

// 拒絕授權欄位及其他未知輸入；避免含正文的 record.ToString。
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class AddItemRequest
{
    public string? Content { get; init; }
}
