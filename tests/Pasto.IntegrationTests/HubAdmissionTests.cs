using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pasto.Server.Api;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Security;
using Xunit;
using static Pasto.IntegrationTests.RoomHubTests;

namespace Pasto.IntegrationTests;

public sealed class HubAdmissionTests
{
    private const string Hub = "/hubs/room";
    private const string Negotiate = NegotiatePath;
    private static readonly string Credential = new('A', 22);

    [Theory]
    [InlineData("global")]
    [InlineData("source")]
    [InlineData("room")]
    [InlineData("participant")]
    public async Task ConcurrentAcquisitionAndDuplicateReleasePreserveEachLimit(string dimension)
    {
        var quota = new HubConnectionQuota(Options.Create(new HubAdmissionOptions
        {
            GlobalConnections = dimension == "global" ? 1 : 12,
            SourceConnections = dimension == "source" ? 1 : 12,
            RoomConnections = dimension == "room" ? 1 : 12,
            ParticipantConnections = dimension == "participant" ? 1 : 12
        }));
        var room = Guid.NewGuid();
        var shared = new ParticipantIdentity(Guid.NewGuid(), room);
        var contenders = Enumerable.Range(0, 12).Select(index =>
            (Source: dimension == "source" ? "same" : $"source-{index}",
                Identity: dimension == "participant" ? shared : new ParticipantIdentity(Guid.NewGuid(),
                    dimension == "room" ? room : Guid.NewGuid()))).ToArray();
        var winners = await ConcurrentlyAsync(contenders.Select(contender => (Func<HubConnectionQuota.Lease?>)(() =>
            quota.TryAcquire(contender.Source, contender.Identity, default))));
        var winner = Assert.IsType<HubConnectionQuota.Lease>(Assert.Single(winners.OfType<HubConnectionQuota.Lease>()));
        Assert.All(contenders, contender => Assert.False(quota.HasCapacity(contender.Source, contender.Identity)));

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(winner.Dispose)));
        using var replacement = quota.TryAcquire(contenders[0].Source, contenders[0].Identity, default);
        Assert.NotNull(replacement);
        var afterReplacement = await ConcurrentlyAsync(contenders.Select(contender => (Func<HubConnectionQuota.Lease?>)(() =>
        {
            winner.Dispose();
            return quota.TryAcquire(contender.Source, contender.Identity, default);
        })));
        Assert.All(afterReplacement, lease => Assert.Null(lease));
    }

    [Fact]
    public async Task NegotiateIgnoresMissingAndMalformedCredentialsAndUsesBothVerifiedBuckets()
    {
        var clock = new ManualClock();
        await using var factory = FakeFactory(clock);
        using var first = Client(factory, "2001:db8:0:1::1");
        using var samePrefix = Client(factory, "2001:db8:0:1::2");
        using var other = Client(factory, "2001:db8:0:2::1");

        for (var index = 0; index < 205; index++)
        {
            await ExpectAsync(first, null, HttpStatusCode.Unauthorized);
            await ExpectAsync(other, "invalid", HttpStatusCode.Unauthorized);
        }
        for (var index = 0; index < 100; index++)
            await ExpectAsync(first, Credential, HttpStatusCode.OK);
        await ExpectAsync(samePrefix, Credential, HttpStatusCode.TooManyRequests, "RateLimited", 1);
        await ExpectAsync(samePrefix, Credential, HttpStatusCode.TooManyRequests, "RateLimited", 1,
            path: Hub + "/negotiate/?negotiateVersion=1");
        for (var index = 0; index < 100; index++)
            await ExpectAsync(other, Credential, HttpStatusCode.OK);
        using var third = Client(factory, "198.51.100.3");
        await ExpectAsync(third, Credential, HttpStatusCode.TooManyRequests, "RateLimited", 1);

        clock.Advance(TimeSpan.FromMilliseconds(10));
        await ExpectAsync(samePrefix, Credential, HttpStatusCode.TooManyRequests, "RateLimited", 1);
        await ExpectAsync(third, Credential, HttpStatusCode.OK);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        await ExpectAsync(samePrefix, Credential, HttpStatusCode.OK);
    }

    [Fact]
    public async Task FullNegotiateSourceTableUsesOnlyGlobalBucketAndReclaimsIdleEntries()
    {
        var clock = new ManualClock();
        await using var factory = FakeFactory(clock, new Dictionary<string, string?> { ["Api:Hub:NegotiateSourceCapacity"] = "1" });
        using var first = Client(factory, "198.51.100.1");
        using var untracked = Client(factory, "198.51.100.2");
        await ExpectAsync(first, Credential, HttpStatusCode.OK);
        for (var index = 0; index < 199; index++)
            await ExpectAsync(untracked, Credential, HttpStatusCode.OK);
        await ExpectAsync(untracked, Credential, HttpStatusCode.TooManyRequests, "RateLimited", 1);

        clock.Advance(TimeSpan.FromMinutes(10));
        for (var index = 0; index < 100; index++)
            await ExpectAsync(untracked, Credential, HttpStatusCode.OK);
        await ExpectAsync(untracked, Credential, HttpStatusCode.TooManyRequests, "RateLimited", 1);
        await ExpectAsync(first, Credential, HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("")]
    [InlineData("&id=")]
    [InlineData("&id=one&id=two")]
    public async Task WebSocketWithoutNegotiatedIdIsRejectedWithoutOccupyingCapacity(string idQuery)
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db);
        var probe = new LifecycleProbe();
        await using var factory = LifecycleFactory(db, probe);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var websocket = factory.Server.CreateWebSocketClient();
        websocket.ConfigureRequest = request => request.Headers.Origin = "http://localhost";
        var rejection = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var socket = await websocket.ConnectAsync(new Uri($"ws://localhost{Hub}?access_token={room.Credential}{idQuery}"),
                deadline.Token);
        });
        Assert.Contains("400", rejection.Message);
        Assert.Equal(0, probe.HubConnects);
        Assert.False(probe.TransportEnded.Task.IsCompleted);

        using var client = factory.CreateClient();
        var id = await NegotiateAsync(client, room.Credential);
        using var accepted = await ConnectNegotiatedAsync(factory, room.Credential, id, deadline.Token);
        var hub = new RoomHubTests.HubMessages(accepted, deadline.Token);
        await hub.HandshakeAsync();
        Assert.False((await hub.SubscribeAsync(room.RoomId)).TryGetProperty("error", out _));
        Assert.Equal(1, probe.HubConnects);
    }

    [Fact]
    public async Task WebSocketWithMismatchedOriginIsForbiddenBeforeConsumingNegotiatedId()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db);
        await using var factory = new PastoApiFactory(db.ConnectionString);
        using var client = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var token = await NegotiateAsync(client, room.Credential, deadline.Token);
        var rejection = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var rejected = await ConnectNegotiatedAsync(factory, room.Credential, token, deadline.Token,
                origin: "http://unrelated.example");
        });
        Assert.Contains("403", rejection.Message);

        using var socket = await ConnectNegotiatedAsync(factory, room.Credential, token, deadline.Token);
        var hub = new HubMessages(socket, deadline.Token);
        await hub.HandshakeAsync();
        Assert.False((await hub.SubscribeAsync(room.RoomId)).TryGetProperty("error", out _));
    }

    [Fact]
    public async Task NegotiatedCapacityRaceClosesWithKnownErrorAndRejectedConnectionCannotReleaseSlot()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db);
        var probe = new LifecycleProbe();
        await using var factory = LifecycleFactory(db, probe);
        using var client = factory.CreateClient();
        var firstId = await NegotiateAsync(client, room.Credential);
        var secondId = await NegotiateAsync(client, room.Credential);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var first = await ConnectNegotiatedAsync(factory, room.Credential, firstId, deadline.Token);
        var winner = new RoomHubTests.HubMessages(first, deadline.Token);
        await winner.HandshakeAsync();
        Assert.False((await winner.SubscribeAsync(room.RoomId)).TryGetProperty("error", out _));

        using var second = await ConnectNegotiatedAsync(factory, room.Credential, secondId, deadline.Token);
        var rejected = new RoomHubTests.HubMessages(second, deadline.Token);
        await rejected.HandshakeAsync();
        await AssertCloseAsync(rejected, "Connection closed with an error. HubException: HubConnectionLimitReached");
        await probe.TransportEnded.Task.WaitAsync(deadline.Token);
        Assert.Equal(1, probe.HubConnects);
        Assert.Equal(0, probe.HubDisconnects);
        await ExpectAsync(client, room.Credential, HttpStatusCode.TooManyRequests, "HubConnectionLimitReached", 45);

        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "", deadline.Token);
        await probe.HubEnded.Task.WaitAsync(deadline.Token);
        await ExpectAsync(client, room.Credential, HttpStatusCode.OK);
    }

    [Fact]
    public async Task FailedOnConnectedReleasesAcquiredSlotWithoutHubDisconnectCallback()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db);
        var probe = new LifecycleProbe(failFirst: true);
        await using var factory = LifecycleFactory(db, probe);
        using var client = factory.CreateClient();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var id = await NegotiateAsync(client, room.Credential);
        using var failed = await ConnectNegotiatedAsync(factory, room.Credential, id, deadline.Token);
        var messages = new RoomHubTests.HubMessages(failed, deadline.Token);
        await messages.HandshakeAsync();
        await AssertCloseAsync(messages, "Connection closed with an error.");
        await probe.TransportEnded.Task.WaitAsync(deadline.Token);
        Assert.Equal(1, probe.HubConnects);
        Assert.Equal(0, probe.HubDisconnects);

        var nextId = await NegotiateAsync(client, room.Credential);
        using var recovered = await ConnectNegotiatedAsync(factory, room.Credential, nextId, deadline.Token);
        var hub = new RoomHubTests.HubMessages(recovered, deadline.Token);
        await hub.HandshakeAsync();
        Assert.False((await hub.SubscribeAsync(room.RoomId)).TryGetProperty("error", out _));
    }

    [Fact]
    public async Task SubscribeRefillsPerConnectionAndExcessDoesNotReachRoomReads()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db);
        var clock = new ManualClock();
        var probe = new LifecycleProbe();
        await using var factory = new PastoApiFactory(db.ConnectionString, configureServices: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.PostConfigure<HubOptions<RoomHub>>(options => options.AddFilter(probe));
        });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var first = await RoomHubTests.ConnectAsync(factory, room.Credential, deadline.Token);
        var hub = new RoomHubTests.HubMessages(first, deadline.Token);
        await hub.HandshakeAsync();
        for (var index = 0; index < 3; index++)
            Assert.False((await hub.SubscribeAsync(room.RoomId)).TryGetProperty("error", out _));
        await AssertSubscriptionLimitedAsync(hub, room.RoomId);
        clock.Advance(TimeSpan.FromSeconds(5));
        await AssertSubscriptionLimitedAsync(hub, room.RoomId);
        Assert.Equal(3, probe.HubInvocations);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False((await hub.SubscribeAsync(room.RoomId)).TryGetProperty("error", out _));
        await AssertSubscriptionLimitedAsync(hub, room.RoomId);

        using var second = await RoomHubTests.ConnectAsync(factory, room.Credential, deadline.Token);
        var other = new RoomHubTests.HubMessages(second, deadline.Token);
        await other.HandshakeAsync();
        Assert.False((await other.SubscribeAsync(room.RoomId)).TryGetProperty("error", out _));
        Assert.Equal(5, probe.HubInvocations);
    }

    [Fact]
    public async Task NegotiationAdvertisesOnlyWebSocketsAndRejectsOtherTransports()
    {
        await using var factory = FakeFactory(new ManualClock());
        using var client = factory.CreateClient();
        var sseId = await NegotiateAsync(client, Credential);
        var pollingId = await NegotiateAsync(client, Credential);
        using var sse = await SendAsync(client, HttpMethod.Get, $"{Hub}?id={sseId}", Credential, "text/event-stream");
        using var polling = await SendAsync(client, HttpMethod.Get, $"{Hub}?id={pollingId}", Credential);
        Assert.Equal(HttpStatusCode.NotFound, sse.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, polling.StatusCode);
        using var queryOnly = await SendAsync(client, HttpMethod.Get, $"{Hub}?id={sseId}&access_token={Credential}",
            null, "text/event-stream");
        Assert.Equal(HttpStatusCode.Unauthorized, queryOnly.StatusCode);
    }

    private static async Task<T[]> ConcurrentlyAsync<T>(IEnumerable<Func<T>> work)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = work.Select(action => Task.Run(async () =>
        {
            await start.Task;
            return action();
        })).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static Task<CreatedRoom> CreateRoomAsync(PostgresTestDatabase db)
        => TestRoomCreation.Create(db, new RoomSecrets()).ExecuteAsync();

    private static PastoApiFactory FakeFactory(ManualClock clock, Dictionary<string, string?>? settings = null)
    {
        settings ??= new();
        settings["Api:TrustedProxies:0"] = "127.0.0.1";
        return new PastoApiFactory("Host=127.0.0.1;Database=pasto_test", settings, services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IParticipantVerifier, FixedVerifier>();
        });
    }

    private static PastoApiFactory LifecycleFactory(PostgresTestDatabase db, LifecycleProbe probe)
        => new(db.ConnectionString, new Dictionary<string, string?>
        {
            ["Api:Hub:GlobalConnections"] = "1",
            ["Api:Hub:ClientTimeoutSeconds"] = "45"
        }, services =>
        {
            services.AddSingleton(probe);
            services.AddSingleton<HubLifetimeManager<RoomHub>, ProbeLifetimeManager>();
            services.PostConfigure<HubOptions<RoomHub>>(options => options.AddFilter(probe));
        });

    private static HttpClient Client(PastoApiFactory factory, string source)
    {
        var client = new HttpClient(factory.Server.CreateHandler(context => context.Connection.RemoteIpAddress = IPAddress.Loopback))
        {
            BaseAddress = new Uri("http://localhost")
        };
        client.DefaultRequestHeaders.Add("X-Forwarded-For", source);
        return client;
    }

    private static async Task AssertCloseAsync(RoomHubTests.HubMessages messages, string error)
    {
        var close = await messages.ReadAsync();
        Assert.Equal(7, close.GetProperty("type").GetInt32());
        Assert.Equal(error, close.GetProperty("error").GetString());
        Assert.False(close.TryGetProperty("allowReconnect", out var reconnect) && reconnect.GetBoolean());
    }

    private static async Task AssertSubscriptionLimitedAsync(RoomHubTests.HubMessages messages, Guid room)
        => Assert.EndsWith("HubException: SubscribeRateLimited", (await messages.SubscribeAsync(room)).GetProperty("error").GetString());

    private static async Task ExpectAsync(HttpClient client, string? credential, HttpStatusCode status, string? code = null,
        int seconds = 0, string path = Negotiate)
    {
        using var response = await SendAsync(client, HttpMethod.Post, path, credential);
        Assert.Equal(status, response.StatusCode);
        if (code is null) return;
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal(TimeSpan.FromSeconds(seconds), response.Headers.RetryAfter?.Delta);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal(seconds, body.GetProperty("retryAfterSeconds").GetInt32());
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, string? credential, string? accept = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (credential is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        if (accept is not null) request.Headers.Accept.ParseAdd(accept);
        return await client.SendAsync(request);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance(TimeSpan interval) => Interlocked.Add(ref timestamp, interval.Ticks);
    }

    private sealed class FixedVerifier : IParticipantVerifier
    {
        private readonly ParticipantIdentity identity = new(Guid.NewGuid(), Guid.NewGuid());
        public Task<ParticipantIdentity?> VerifyAsync(byte[] hash, CancellationToken cancellationToken)
            => Task.FromResult<ParticipantIdentity?>(identity);
    }

    private sealed class LifecycleProbe(bool failFirst = false) : IHubFilter
    {
        private int hubConnects;
        private int hubDisconnects;
        private int hubInvocations;
        internal int HubConnects => Volatile.Read(ref hubConnects);
        internal int HubDisconnects => Volatile.Read(ref hubDisconnects);
        internal int HubInvocations => Volatile.Read(ref hubInvocations);
        internal TaskCompletionSource TransportEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource HubEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task OnConnectedAsync(HubLifetimeContext context, Func<HubLifetimeContext, Task> next)
        {
            var attempt = Interlocked.Increment(ref hubConnects);
            await next(context);
            if (failFirst && attempt == 1) throw new InvalidOperationException("Injected startup failure.");
        }

        public async Task OnDisconnectedAsync(HubLifetimeContext context, Exception? exception, Func<HubLifetimeContext, Exception?, Task> next)
        {
            Interlocked.Increment(ref hubDisconnects);
            await next(context, exception);
            HubEnded.TrySetResult();
        }

        public ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
        {
            Interlocked.Increment(ref hubInvocations);
            return next(context);
        }
    }

    private sealed class ProbeLifetimeManager(ILogger<DefaultHubLifetimeManager<RoomHub>> logger, LifecycleProbe probe)
        : DefaultHubLifetimeManager<RoomHub>(logger)
    {
        public override async Task OnDisconnectedAsync(HubConnectionContext connection)
        {
            await base.OnDisconnectedAsync(connection);
            probe.TransportEnded.TrySetResult();
        }
    }
}
