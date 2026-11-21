using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Persistence;
using Pasto.Server.Infrastructure.Security;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class RoomHubTests
{
    private const string HubPath = "/hubs/room";
    internal const string NegotiatePath = HubPath + "/negotiate?negotiateVersion=1";

    [Fact]
    public async Task UnexpectedHubFailure_UsesOneSanitizedApplicationEvent()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        using var logs = new HubFailureLogs();
        await using var factory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
            services.AddSingleton<IRoomStateStore>(new FailedRoomState()), logs: logs);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await ConnectAsync(factory, room.Credential, deadline.Token);
        var hub = new HubMessages(socket, deadline.Token);
        await hub.HandshakeAsync();
        var response = await hub.SubscribeAsync(room.RoomId);
        Assert.True(response.TryGetProperty("error", out _));
        var entry = Assert.Single(logs.Errors);
        Assert.Equal(6101, entry.Id);
        Assert.Contains(nameof(FailedRoomState), entry.Text);
        Assert.DoesNotContain(FailedRoomState.SensitiveMessage, entry.Text);
        Assert.DoesNotContain(room.Credential, entry.Text);
        Assert.DoesNotContain(room.Code, entry.Text);
    }

    [Fact]
    public async Task Subscriptions_AreSameRoomAndReceiveOnlyCommittedVersionHints()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var other = await CreateRoomAsync(database);
        await using var verification = database.CreateContext();
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
        await member.ExpectClosedAsync(room.RoomId, "3");
        Assert.True(await verification.Rooms.AnyAsync(x => x.Id == room.RoomId && x.DeletedAt != null));
        using var unavailable = await SendAsync(client, room, HttpMethod.Get, "");
        Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
        using var resubscribeSocket = await ConnectAsync(factory, room.Credential, cancellation);
        var resubscriber = new HubMessages(resubscribeSocket, cancellation);
        await resubscriber.HandshakeAsync();
        Assert.Contains("RoomUnavailable", (await resubscriber.SubscribeAsync(room.RoomId)).GetProperty("error").GetString());

        using var outsiderAdd = await SendAsync(client, other, HttpMethod.Post, "/items", "other text");
        Assert.Equal(HttpStatusCode.Created, outsiderAdd.StatusCode);
        // An incorrectly broadcast hint would arrive ahead of the other room's own hint.
        await outsider.ExpectHintAsync(other.RoomId, "1");
    }

    [Fact]
    public async Task Close_AbortsSubscriptionInProgressEvenWhenHintFails()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var state = new HoldSecondStateRead(new RoomStateStore(database.CreateContextFactory()));
        var send = new FailedSend();
        await using var factory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
        {
            services.AddSingleton<IRoomStateStore>(state);
            services.AddSingleton(send);
            services.AddSingleton(typeof(HubLifetimeManager<>), typeof(FailingHubLifetimeManager<>));
        });
        using var client = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await ConnectAsync(factory, room.Credential, deadline.Token);
        var member = new HubMessages(socket, deadline.Token);
        await member.HandshakeAsync();
        await member.StartSubscribeAsync(room.RoomId);
        await state.Waiting.Task.WaitAsync(deadline.Token);

        using var closed = await SendAsync(client, room, HttpMethod.Delete, "");
        Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);
        Assert.Equal(1, send.Attempts);
        await state.Interrupted.Task.WaitAsync(deadline.Token);
        await member.ExpectClosedAsync(room.RoomId, "1");
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("closed")]
    [InlineData("missing")]
    public async Task Worker_AbortsOnlySubscriptionsToUnavailableRooms(string state)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var other = await CreateRoomAsync(database);
        var hold = new HoldMainCleanup();
        await using var factory = new PastoApiFactory(database.ConnectionString,
            new Dictionary<string, string?> { ["Cleanup:Enabled"] = "true" },
            services => services.AddSingleton<IRoomCleanupStore>(hold));
        using var client = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await hold.Waiting.Task.WaitAsync(deadline.Token);
        using var socket = await ConnectAsync(factory, room.Credential, deadline.Token);
        using var otherSocket = await ConnectAsync(factory, other.Credential, deadline.Token);
        var member = new HubMessages(socket, deadline.Token);
        var outsider = new HubMessages(otherSocket, deadline.Token);
        await member.HandshakeAsync();
        await outsider.HandshakeAsync();
        Assert.False((await member.SubscribeAsync(room.RoomId)).TryGetProperty("error", out _));
        Assert.False((await outsider.SubscribeAsync(other.RoomId)).TryGetProperty("error", out _));
        await using var setup = database.CreateContext();
        if (state == "expired")
        {
            await setup.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE rooms SET created_at = clock_timestamp() - interval '5 hours',
                                 expires_at = clock_timestamp() - interval '1 second'
                WHERE id = {room.RoomId}
                """);
        }
        else
        {
            await new CloseRoom(new RoomClosureStore(database.CreateContextFactory())).ExecuteAsync(room.RoomId, room.ParticipantId);
            if (state == "missing")
                Assert.Equal(1, await new RoomCleanupStore(database.CreateContextFactory()).DeleteBatchAsync(1, default));
        }

        hold.Resume.TrySetResult();
        await member.ExpectClosedAsync(room.RoomId, "1");
        using var added = await SendAsync(client, other, HttpMethod.Post, "/items", "still connected");
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        await outsider.ExpectHintAsync(other.RoomId, "1");
    }

    [Fact]
    public async Task TrackingAtCapacity_SubscriptionsStayLiveAndWorkerReportsOneSummary()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var trackedRoom = await CreateRoomAsync(database);
        var overflowRoom = await CreateRoomAsync(database);
        var nextRoom = await CreateRoomAsync(database);
        var hold = new HoldMainCleanup();
        var logs = new TrackingLogger();
        var registry = new RoomConnections(logs, capacity: 1);
        await using var factory = new PastoApiFactory(database.ConnectionString,
            new Dictionary<string, string?> { ["Cleanup:Enabled"] = "true" },
            services =>
            {
                services.AddSingleton(registry);
                services.AddSingleton<IRoomCleanupStore>(hold);
            });
        using var client = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var cancellation = deadline.Token;
        await hold.Waiting.Task.WaitAsync(cancellation);
        using var trackedSocket = await ConnectAsync(factory, trackedRoom.Credential, cancellation);
        using var firstSocket = await ConnectAsync(factory, overflowRoom.Credential, cancellation);
        using var secondSocket = await ConnectAsync(factory, overflowRoom.Credential, cancellation);
        var tracked = new HubMessages(trackedSocket, cancellation);
        var first = new HubMessages(firstSocket, cancellation);
        var second = new HubMessages(secondSocket, cancellation);
        await tracked.HandshakeAsync();
        await first.HandshakeAsync();
        await second.HandshakeAsync();
        Assert.False((await tracked.SubscribeAsync(trackedRoom.RoomId)).TryGetProperty("error", out _));
        Assert.False((await first.SubscribeAsync(overflowRoom.RoomId)).TryGetProperty("error", out _));
        Assert.False((await second.SubscribeAsync(overflowRoom.RoomId)).TryGetProperty("error", out _));
        Assert.Equal(new[] { trackedRoom.RoomId }, registry.GetRoomIds());
        Assert.False(logs.Events.Reader.TryRead(out _));

        using var added = await SendAsync(client, overflowRoom, HttpMethod.Post, "/items", "still connected");
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        await first.ExpectHintAsync(overflowRoom.RoomId, "1");
        await second.ExpectHintAsync(overflowRoom.RoomId, "1");

        using var trackedClose = await SendAsync(client, trackedRoom, HttpMethod.Delete, "");
        Assert.Equal(HttpStatusCode.NoContent, trackedClose.StatusCode);
        await tracked.ExpectClosedAsync(trackedRoom.RoomId, "1");
        while (registry.GetRoomIds().Length > 0)
            await Task.Delay(10, cancellation);

        Assert.False((await first.SubscribeAsync(overflowRoom.RoomId)).TryGetProperty("error", out _));
        Assert.Empty(registry.GetRoomIds());
        using var nextSocket = await ConnectAsync(factory, nextRoom.Credential, cancellation);
        var next = new HubMessages(nextSocket, cancellation);
        await next.HandshakeAsync();
        Assert.False((await next.SubscribeAsync(nextRoom.RoomId)).TryGetProperty("error", out _));
        Assert.Equal(new[] { nextRoom.RoomId }, registry.GetRoomIds());

        using var overflowClose = await SendAsync(client, overflowRoom, HttpMethod.Delete, "");
        Assert.Equal(HttpStatusCode.NoContent, overflowClose.StatusCode);
        await first.ExpectHintAsync(overflowRoom.RoomId, "2");
        await second.ExpectHintAsync(overflowRoom.RoomId, "2");
        Assert.Contains("RoomUnavailable", (await first.SubscribeAsync(overflowRoom.RoomId)).GetProperty("error").GetString());
        Assert.Contains("RoomUnavailable", (await second.SubscribeAsync(overflowRoom.RoomId)).GetProperty("error").GetString());
        Assert.Equal(new[] { nextRoom.RoomId }, registry.GetRoomIds());

        hold.Resume.TrySetResult();
        var summary = await logs.Events.Reader.ReadAsync(cancellation);
        Assert.Equal((6009, "RoomConnectionTrackingFull", LogLevel.Warning, 2L), summary);
        // 摘要取出既有累計值，下一次檢查不會重複記錄。
        registry.ReportUntrackedSubscriptions();
        Assert.False(logs.Events.Reader.TryRead(out _));
    }

    [Fact]
    public void Tracking_ConcurrentRegistrationsRespectCapacityAndMissingUntrackDoesNotEvict()
    {
        var registry = new RoomConnections(NullLogger<RoomConnections>.Instance, capacity: 1);
        var rooms = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid()).ToArray();
        var results = new RoomTrackingResult[rooms.Length];
        Parallel.For(0, rooms.Length, index =>
            results[index] = registry.TryTrack(index.ToString(), rooms[index], default, () => { }));

        var trackedIndex = Array.IndexOf(results, RoomTrackingResult.Tracked);
        Assert.Single(results, result => result == RoomTrackingResult.Tracked);
        Assert.Equal(31, results.Count(result => result == RoomTrackingResult.AtCapacity));
        var id = trackedIndex.ToString();
        var room = rooms[trackedIndex];
        Assert.Equal(RoomTrackingResult.Tracked, registry.TryTrack(id, room, default, () => { }));
        Assert.Equal(RoomTrackingResult.RoomMismatch, registry.TryTrack(id, Guid.NewGuid(), default, () => { }));
        Assert.Equal(RoomTrackingResult.ConnectionAborted,
            registry.TryTrack(id, room, new CancellationToken(true), () => { }));
        registry.Untrack("not registered");
        Assert.Equal(new[] { room }, registry.GetRoomIds());
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

    internal static async Task<WebSocket> ConnectAsync(PastoApiFactory factory, string credential, CancellationToken cancellation)
    {
        using var client = factory.CreateClient();
        var token = await NegotiateAsync(client, credential, cancellation);
        return await ConnectNegotiatedAsync(factory, credential, token, cancellation);
    }

    internal static async Task<string> NegotiateAsync(HttpClient client, string credential, CancellationToken cancellation = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, NegotiatePath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        using var response = await client.SendAsync(request, cancellation);
        return await ReadConnectionTokenAsync(response, cancellation);
    }

    internal static async Task<string> ReadConnectionTokenAsync(HttpResponseMessage response, CancellationToken cancellation = default)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellation);
        Assert.Equal("WebSockets", Assert.Single(body.GetProperty("availableTransports").EnumerateArray())
            .GetProperty("transport").GetString());
        return body.GetProperty("connectionToken").GetString()!;
    }

    internal static Task<WebSocket> ConnectNegotiatedAsync(PastoApiFactory factory, string credential, string token,
        CancellationToken cancellation, string origin = "http://localhost")
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers.Origin = origin;
        return client.ConnectAsync(new Uri($"ws://localhost{HubPath}?id={Uri.EscapeDataString(token)}&access_token={credential}"), cancellation);
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, CreatedRoom room, HttpMethod method,
        string suffix, string? content = null)
    {
        using var request = new HttpRequestMessage(method, $"/api/rooms/{room.RoomId}{suffix}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", room.Credential);
        if (content is not null) request.Content = JsonContent.Create(new { content });
        return await client.SendAsync(request);
    }

    internal sealed class HubMessages(WebSocket socket, CancellationToken cancellation)
    {
        private readonly Queue<string> frames = new();
        private int invocation;

        public async Task HandshakeAsync()
        {
            await SendAsync(new { protocol = "json", version = 1 });
            Assert.Empty((await ReadAsync()).EnumerateObject());
        }

        internal Task PingAsync() => SendAsync(new { type = 6 });

        public async Task<JsonElement> SubscribeAsync(Guid roomId)
        {
            var invocationId = await StartSubscribeAsync(roomId);
            var response = await ReadAsync();
            Assert.Equal(3, response.GetProperty("type").GetInt32());
            Assert.Equal(invocationId, response.GetProperty("invocationId").GetString());
            return response;
        }

        public async Task<string> StartSubscribeAsync(Guid roomId)
        {
            var invocationId = (++invocation).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await SendAsync(new { type = 1, target = "SubscribeRoom", invocationId, arguments = new[] { roomId } });
            return invocationId;
        }

        public async Task ExpectHintAsync(Guid roomId, string version)
        {
            AssertHint(await ReadAsync(), roomId, version);
        }

        public async Task ExpectClosedAsync(Guid roomId, string version)
        {
            var response = await ReadAsync();
            if (response.GetProperty("type").GetInt32() == 1)
            {
                AssertHint(response, roomId, version);
                response = await ReadAsync();
            }
            Assert.Equal(7, response.GetProperty("type").GetInt32());
        }

        private static void AssertHint(JsonElement response, Guid roomId, string version)
        {
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

        internal async Task<JsonElement> ReadAsync()
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
                    if (received.MessageType == WebSocketMessageType.Close)
                        return JsonSerializer.SerializeToElement(new { type = 7 });
                    Assert.Equal(WebSocketMessageType.Text, received.MessageType);
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                foreach (var frame in Encoding.UTF8.GetString(message.ToArray()).Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
                    frames.Enqueue(frame);
            }
        }
    }

    private sealed class HoldSecondStateRead(IRoomStateStore store) : IRoomStateStore
    {
        private int reads;
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Interrupted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RoomState> GetAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref reads) == 2)
            {
                Waiting.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    Interrupted.TrySetResult();
                    throw;
                }
            }
            return await store.GetAsync(roomId, participantId, cancellationToken);
        }
    }

    private sealed class HoldMainCleanup : IRoomCleanupStore
    {
        public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RoomCleanupBacklog> MeasureBacklogAsync(CancellationToken cancellationToken)
        {
            Waiting.TrySetResult();
            await Resume.Task.WaitAsync(cancellationToken);
            return new RoomCleanupBacklog(0, null);
        }

        public Task<int> DeleteBatchAsync(int batchSize, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class FailedRoomState : IRoomStateStore
    {
        public const string SensitiveMessage = "raw hub exception content";
        public Task<RoomState> GetAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken)
            => throw new InvalidOperationException(SensitiveMessage);
    }

    private sealed class HubFailureLogs : ILoggerProvider, ILogger
    {
        public System.Collections.Concurrent.ConcurrentQueue<(int Id, string Text)> Errors { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Dispose() { }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error)
                Errors.Enqueue((eventId.Id, formatter(state, exception)));
        }
    }

    private sealed class TrackingLogger : ILogger<RoomConnections>
    {
        public Channel<(int Id, string? Name, LogLevel Level, long Count)> Events { get; }
            = Channel.CreateUnbounded<(int, string?, LogLevel, long)>();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id != 6009)
                return;

            var entries = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(state);
            var count = Assert.IsType<long>(entries.Single(entry => entry.Key == "UntrackedSubscriptions").Value);
            Events.Writer.TryWrite((eventId.Id, eventId.Name, logLevel, count));
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
