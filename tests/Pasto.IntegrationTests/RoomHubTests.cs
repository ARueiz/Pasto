using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Persistence;
using Pasto.Server.Infrastructure.Security;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class RoomHubTests
{
    private const string HubPath = "/hubs/room";

    [Fact]
    public async Task Subscriptions_AreSameRoomAndReceiveOnlyCommittedVersionHints()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var other = await CreateRoomAsync(database);
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var cancellation = deadline.Token;
        using var memberSocket = await ConnectAsync(factory, room.Credential, cancellation);
        using var otherSocket = await ConnectAsync(factory, other.Credential, cancellation);
        var member = new HubMessages(memberSocket, cancellation);
        var outsider = new HubMessages(otherSocket, cancellation);
        await member.HandshakeAsync();
        await outsider.HandshakeAsync();
        Assert.False((await member.SubscribeAsync(room.RoomId)).TryGetProperty("error", out _));
        Assert.Contains("ParticipantUnauthorized", (await outsider.SubscribeAsync(room.RoomId)).GetProperty("error").GetString());
        Assert.False((await outsider.SubscribeAsync(other.RoomId)).TryGetProperty("error", out _));

        using var added = await SendAsync(client, room, HttpMethod.Post, "/items", "room text");
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var item = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("item").GetProperty("id").GetGuid();
        await member.ExpectHintAsync(room.RoomId, "1");
        using var deleted = await SendAsync(client, room, HttpMethod.Delete, $"/items/{item}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        await member.ExpectHintAsync(room.RoomId, "2");
        using var repeated = await SendAsync(client, room, HttpMethod.Delete, $"/items/{item}");
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        using var closed = await SendAsync(client, room, HttpMethod.Delete, "");
        Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);
        await member.ExpectHintAsync(room.RoomId, "3");
        Assert.Contains("RoomUnavailable", (await member.SubscribeAsync(room.RoomId)).GetProperty("error").GetString());

        using var outsiderAdd = await SendAsync(client, other, HttpMethod.Post, "/items", "other text");
        Assert.Equal(HttpStatusCode.Created, outsiderAdd.StatusCode);
        // An incorrectly broadcast hint would arrive ahead of the other room's own hint.
        await outsider.ExpectHintAsync(other.RoomId, "1");
    }

    [Fact]
    public async Task BrowserQueryCredential_IsRestrictedToHubTransportAndOrigin()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var queryOnly = await client.PostAsync($"{HubPath}/negotiate?access_token={room.Credential}", null);
        Assert.Equal(HttpStatusCode.Unauthorized, queryOnly.StatusCode);
        using var rest = await client.GetAsync($"/api/rooms/{room.RoomId}?access_token={room.Credential}");
        Assert.Equal(HttpStatusCode.Unauthorized, rest.StatusCode);
        using var ordinaryGet = await client.GetAsync($"{HubPath}?access_token={room.Credential}");
        Assert.Equal(HttpStatusCode.Unauthorized, ordinaryGet.StatusCode);
        using var crossOrigin = new HttpRequestMessage(HttpMethod.Post, HubPath + "/negotiate");
        crossOrigin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", room.Credential);
        crossOrigin.Headers.Add("Origin", "http://unrelated.example");
        using var rejected = await client.SendAsync(crossOrigin);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedCommit_SkipsSending_AndFailedSendDoesNotHideCommittedData(bool afterCommit)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var commit = new FailNextCommit(afterCommit);
        var send = new FailedSend();
        await using var factory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
        {
            services.AddDbContextFactory<PastoDbContext>(options => options.AddInterceptors(commit));
            services.AddSingleton(send);
            services.AddSingleton(typeof(HubLifetimeManager<>), typeof(FailingHubLifetimeManager<>));
        });
        using var client = factory.CreateClient();
        using var failed = await SendAsync(client, room, HttpMethod.Post, "/items", "interrupted");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
        Assert.Equal(0, send.Attempts);

        using var committed = await SendAsync(client, room, HttpMethod.Post, "/items", "committed");
        Assert.Equal(HttpStatusCode.Created, committed.StatusCode);
        Assert.Equal(1, send.Attempts);
        using var snapshot = await SendAsync(client, room, HttpMethod.Get, "/items");
        Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        var json = await snapshot.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(afterCommit ? "2" : "1", json.GetProperty("snapshotVersion").GetString());
        var contents = json.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("content").GetString());
        Assert.Equal(afterCommit ? ["committed", "interrupted"] : new[] { "committed" }, contents);
    }

    private static Task<CreatedRoom> CreateRoomAsync(PostgresTestDatabase database) =>
        TestRoomCreation.Create(database, new RoomSecrets()).ExecuteAsync();

    private static Task<WebSocket> ConnectAsync(PastoApiFactory factory, string credential, CancellationToken cancellation)
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Origin = "http://localhost";
        return client.ConnectAsync(new Uri($"ws://localhost{HubPath}?access_token={credential}"), cancellation);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, CreatedRoom room, HttpMethod method,
        string suffix, string? content = null)
    {
        using var request = new HttpRequestMessage(method, $"/api/rooms/{room.RoomId}{suffix}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", room.Credential);
        if (content is not null) request.Content = JsonContent.Create(new { content });
        return await client.SendAsync(request);
    }

    private sealed class HubMessages(WebSocket socket, CancellationToken cancellation)
    {
        private readonly Queue<string> frames = new();
        private int invocation;

        public async Task HandshakeAsync()
        {
            await SendAsync(new { protocol = "json", version = 1 });
            Assert.Empty((await ReadAsync()).EnumerateObject());
        }

        public async Task<JsonElement> SubscribeAsync(Guid roomId)
        {
            var invocationId = (++invocation).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await SendAsync(new { type = 1, target = "SubscribeRoom", invocationId, arguments = new[] { roomId } });
            var response = await ReadAsync();
            Assert.Equal(3, response.GetProperty("type").GetInt32());
            Assert.Equal(invocationId, response.GetProperty("invocationId").GetString());
            return response;
        }

        public async Task ExpectHintAsync(Guid roomId, string version)
        {
            var response = await ReadAsync();
            Assert.Equal(1, response.GetProperty("type").GetInt32());
            Assert.Equal("RoomChanged", response.GetProperty("target").GetString());
            var hint = Assert.Single(response.GetProperty("arguments").EnumerateArray());
            Assert.Equal(2, hint.EnumerateObject().Count());
            Assert.Equal(roomId, hint.GetProperty("roomId").GetGuid());
            Assert.Equal(version, hint.GetProperty("version").GetString());
        }

        private Task SendAsync(object message) => socket.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + '\u001e')),
            WebSocketMessageType.Text, true, cancellation);

        private async Task<JsonElement> ReadAsync()
        {
            while (true)
            {
                if (frames.TryDequeue(out var next))
                {
                    using var json = JsonDocument.Parse(next);
                    if (!json.RootElement.TryGetProperty("type", out var type) || type.GetInt32() != 6)
                        return json.RootElement.Clone();
                    continue;
                }

                var buffer = new byte[1024];
                using var message = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
                    Assert.Equal(WebSocketMessageType.Text, received.MessageType);
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                foreach (var frame in Encoding.UTF8.GetString(message.ToArray()).Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
                    frames.Enqueue(frame);
            }
        }
    }

    private sealed class FailNextCommit(bool afterCommit) : DbTransactionInterceptor
    {
        private bool failed;

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!afterCommit) InterruptOnce();
            return ValueTask.FromResult(result);
        }

        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (afterCommit) InterruptOnce();
            return Task.CompletedTask;
        }

        private void InterruptOnce()
        {
            if (failed) return;
            failed = true;
            throw new OperationCanceledException();
        }
    }

    private sealed class FailedSend
    {
        public int Attempts { get; set; }
    }

    private sealed class FailingHubLifetimeManager<THub>(ILogger<DefaultHubLifetimeManager<THub>> logger, FailedSend send)
        : DefaultHubLifetimeManager<THub>(logger) where THub : Hub
    {
        public override Task SendGroupAsync(string groupName, string methodName, object?[] args,
            CancellationToken cancellationToken = default)
        {
            send.Attempts++;
            return Task.FromException(new InvalidOperationException("Simulated notification failure."));
        }
    }
}
