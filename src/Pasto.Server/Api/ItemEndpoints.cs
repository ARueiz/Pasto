using System.Globalization;
using System.Text.Json.Serialization;
using Pasto.Server.Application.Items;

namespace Pasto.Server.Api;

internal static class ItemEndpoints
{
    public const string AddName = "AddItem";
    public const string DeleteName = "DeleteOwnItem";

    public static void MapItemWrites(this WebApplication app)
    {
        app.MapPost("/api/rooms/{roomId:guid}/items", async (Guid roomId, AddItemRequest request,
            HttpContext context, AddItem add, CancellationToken cancellationToken) =>
        {
            var identity = ParticipantAuthenticationHandler.Identity(context, roomId);
            var result = await add.ExecuteAsync(roomId, identity.ParticipantId, request.Content!, cancellationToken);
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
        }).WithName(AddName).RequireAuthorization().AddEndpointFilter<ParticipantWriteFilter>();

        app.MapDelete("/api/rooms/{roomId:guid}/items/{itemId:guid}", async (Guid roomId, Guid itemId,
            HttpContext context, DeleteOwnItem delete, CancellationToken cancellationToken) =>
        {
            var identity = ParticipantAuthenticationHandler.Identity(context, roomId);
            var result = await delete.ExecuteAsync(roomId, identity.ParticipantId, itemId, cancellationToken);
            return Results.Ok(new { result.Changed, committedVersion = result.CommittedVersion.ToString(CultureInfo.InvariantCulture) });
        }).WithName(DeleteName).RequireAuthorization().AddEndpointFilter<ParticipantWriteFilter>();
    }
}

// 拒絕授權欄位及其他未知輸入；避免含正文的 record.ToString。
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class AddItemRequest
{
    public string? Content { get; init; }
}
