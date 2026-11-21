using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pasto.Server.Api;
using Pasto.Server.Application.Items;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Persistence;
using Pasto.Server.Infrastructure.Security;
using Xunit;
using static Pasto.IntegrationTests.TransactionTestHooks;

namespace Pasto.IntegrationTests;

public sealed class HttpWorkAdmissionTests
{
    [Fact]
    public void WatermarkTracksContinuousIntervalsAndOnlyPausesPublicWork()
    {
        var clock = new ManualClock();
        var limiter = new HttpWorkAdmission(Options.Create(new HttpWorkOptions
        {
            GlobalLimit = 4,
            JoinLimit = 4,
            JoinPerSource = 4,
            ReadDeleteLimit = 4,
            ReadDeletePerSource = 4,
            HighWatermark = 3,
            RecoveryBelow = 2
        }), clock);
        using var first = Take(HttpWork.ReadDelete);
        using var second = Take(HttpWork.ReadDelete);
        using var third = Take(HttpWork.ReadDelete);
        clock.Advance(TimeSpan.FromSeconds(1));
        using (Take(HttpWork.Join)) { }
        third.Dispose();
        clock.Advance(TimeSpan.FromSeconds(3));
        using var fourth = Take(HttpWork.ReadDelete);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Null(limiter.TryAcquire(HttpWork.Join, "source", out var retryAfter));
        Assert.Equal(5, retryAfter);
        using (Take(HttpWork.ReadDelete))
        {
            Assert.Null(limiter.TryAcquire(HttpWork.ReadDelete, "source", out retryAfter));
            Assert.Equal(1, retryAfter);
        }
        fourth.Dispose();
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Null(limiter.TryAcquire(HttpWork.Join, "source", out _)); // Exactly the recovery watermark.
        second.Dispose();
        clock.Advance(TimeSpan.FromSeconds(9));
        using (Take(HttpWork.ReadDelete)) { } // Returning to the watermark resets the recovery window.
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Null(limiter.TryAcquire(HttpWork.Join, "source", out _));
        clock.Advance(TimeSpan.FromSeconds(1));
        using var recovered = Take(HttpWork.Join);

        HttpWorkAdmission.Lease Take(HttpWork work) => Assert.IsType<HttpWorkAdmission.Lease>(limiter.TryAcquire(work, "source", out _));
    }

    [Fact]
    public void FullWorkSourceTableFallsBackToGlobalAndClassLimits()
    {
        var limiter = new HttpWorkAdmission(Options.Create(new HttpWorkOptions
        {
            SourceCapacity = 1,
            JoinLimit = 3,
            JoinPerSource = 1
        }), new ManualClock());
        using var first = limiter.TryAcquire(HttpWork.Join, "first", out _);
        Assert.NotNull(first);
        Assert.Null(limiter.TryAcquire(HttpWork.Join, "first", out var retry));
        Assert.Equal(1, retry);
        using var otherClass = limiter.TryAcquire(HttpWork.Add, "first", out _);
        Assert.NotNull(otherClass);
        using var untracked1 = limiter.TryAcquire(HttpWork.Join, "other", out _);
        using var untracked2 = limiter.TryAcquire(HttpWork.Join, "other", out _);
        Assert.NotNull(untracked1);
        Assert.NotNull(untracked2);
        Assert.Null(limiter.TryAcquire(HttpWork.Join, "third", out _));
        first.Dispose();
        first.Dispose();
        using var replacement = limiter.TryAcquire(HttpWork.Join, "first", out _);
        Assert.NotNull(replacement);
        Assert.Null(limiter.TryAcquire(HttpWork.Join, "third", out _));
    }

    [Theory]
    [InlineData((int)SourceRateProfile.Create)]
    [InlineData((int)SourceRateProfile.Join)]
    [InlineData((int)SourceRateProfile.Database)]
    public void FullSourceRateTableLeavesNewSourcesSubjectOnlyToCorrespondingGlobalBucket(int profileValue)
    {
        var profile = (SourceRateProfile)profileValue;
        var clock = new ManualClock();
        var sourceBucket = new BucketOptions { TokenLimit = 1, TokensPerPeriod = 1, PeriodSeconds = 1800 };
        var globalBucket = new BucketOptions { TokenLimit = 3, TokensPerPeriod = 3, PeriodSeconds = 1800 };
        var limiter = new SourceRateLimits(Options.Create(new ApiOptions
        {
            SourceRateCapacity = 1,
            CreatePerIp = sourceBucket,
            JoinPerIp = sourceBucket,
            ReadPerIp = sourceBucket,
            CreateGlobal = globalBucket,
            JoinGlobal = globalBucket,
            ReadGlobal = globalBucket
        }), clock);
        Assert.Null(limiter.TryTake(profile, "tracked"));
        Assert.Equal(1800, limiter.TryTake(profile, "tracked"));
        Assert.Null(limiter.TryTake(profile, "untracked"));
        Assert.Null(limiter.TryTake(profile, "untracked"));
        Assert.Equal(1800, limiter.TryTake(profile, "other"));
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1200, limiter.TryTake(profile, "tracked"));
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Null(limiter.TryTake(profile, "untracked"));
        Assert.Equal(1800, limiter.TryTake(profile, "untracked"));
    }

    [Fact]
    public void FullParticipantTableFailsOpenAndReclaimsOnlyAfterAllBucketsRefill()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var clock = new ManualClock();
        var logs = new CapacityLogger();
        var gate = new ParticipantRateLimits(Options.Create(new ApiOptions
        {
            ParticipantRateCapacity = 1,
            AddPerParticipant = new BucketOptions { PeriodSeconds = 1800, TokenLimit = 1, TokensPerPeriod = 1 }
        }), clock, logs);
        Assert.Null(gate.TryTake(first, ParticipantRateProfile.Add));
        Assert.Null(gate.TryTake(first, ParticipantRateProfile.Read));
        Assert.Equal(1800, gate.TryTake(first, ParticipantRateProfile.Add));
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Null(gate.TryTake(second, ParticipantRateProfile.Add));
        Assert.Null(gate.TryTake(second, ParticipantRateProfile.Add));
        Assert.Single(logs.Events);
        clock.Advance(TimeSpan.FromSeconds(59));
        Assert.Null(gate.TryTake(second, ParticipantRateProfile.Read));
        Assert.Single(logs.Events);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(gate.TryTake(second, ParticipantRateProfile.Delete));
        Assert.Equal(new long[] { 1, 3 }, logs.Events);
        clock.Advance(TimeSpan.FromMinutes(19));
        Assert.Null(gate.TryTake(second, ParticipantRateProfile.Add));
        Assert.Equal(1800, gate.TryTake(second, ParticipantRateProfile.Add));
    }

    [Fact]
    public async Task RoomAddQuotaSkipsFailedSaveAndKeepsTokenIfCommitFails()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db);
        var quota = new RoomAddQuota(new ManualClock());
        var normal = new AddItem(new ItemAdditionStore(db.CreateContextFactory(), quota));
        for (var index = 0; index < 19; index++)
            await normal.ExecuteAsync(room.RoomId, room.ParticipantId, "text");

        var afterSave = new FailAfterSaveInterceptor(null);
        var failingSave = new AddItem(new ItemAdditionStore(db.CreateContextFactory(afterSave), quota));
        await Assert.ThrowsAsync<InjectedFailureException>(() => failingSave.ExecuteAsync(room.RoomId, room.ParticipantId, "text"));
        Assert.Equal(1, afterSave.Saves);

        var beforeCommit = new FailBeforeCommitInterceptor();
        var failingCommit = new AddItem(new ItemAdditionStore(db.CreateContextFactory(beforeCommit), quota));
        await Assert.ThrowsAsync<InjectedFailureException>(() => failingCommit.ExecuteAsync(room.RoomId, room.ParticipantId, "text"));
        Assert.True(beforeCommit.ReachedCommit);
        await Assert.ThrowsAsync<RoomAddRateLimitedException>(() => normal.ExecuteAsync(room.RoomId, room.ParticipantId, "text"));
        await using var dbContext = db.CreateContext();
        Assert.Equal(19, await dbContext.Items.CountAsync(item => item.RoomId == room.RoomId));
    }

    [Fact]
    public async Task SlowJoinUploadKeepsItsClassSlotAvailable()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db);
        await using var factory = Kestrel(db, new Dictionary<string, string?>
        {
            ["Api:HttpWork:JoinLimit"] = "1",
            ["Api:HttpWork:JoinPerSource"] = "1"
        });
        using var client = factory.CreateClient();
        using var body = new SlowJoinContent(room.Code);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        var pending = client.PostAsync("/api/rooms/join", body);
        try
        {
            await body.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var other = await client.PostAsJsonAsync("/api/rooms/join", new { code = room.Code });
            Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        }
        finally
        {
            body.Release.TrySetResult();
        }
        using var response = await pending;
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task SlowSnapshotDownloadKeepsItsClassSlotAvailable()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db);
        var seed = new AddItem(new ItemAdditionStore(db.CreateContextFactory()));
        for (var index = 0; index < 300; index++)
            await seed.ExecuteAsync(room.RoomId, room.ParticipantId, new string('\u0001', 8192));
        await using var factory = Kestrel(db, new Dictionary<string, string?>
        {
            ["Api:HttpWork:ReadDeleteLimit"] = "1",
            ["Api:HttpWork:ReadDeletePerSource"] = "1"
        });
        using var client = factory.CreateClient();
        using var slow = new TcpClient { ReceiveBufferSize = 1024 };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var baseAddress = client.BaseAddress!;
        await slow.ConnectAsync(baseAddress.Host, baseAddress.Port, timeout.Token);
        await using var stream = slow.GetStream();
        var path = $"/api/rooms/{room.RoomId}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(
            $"GET {path}/items HTTP/1.1\r\nHost: {baseAddress.Authority}\r\nAuthorization: Bearer {room.Credential}\r\n\r\n"), timeout.Token);
        var headers = new StringBuilder();
        var buffer = new byte[1];
        while (!headers.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            Assert.Equal(1, await stream.ReadAsync(buffer, timeout.Token));
            headers.Append((char)buffer[0]);
        }
        Assert.StartsWith("HTTP/1.1 200", headers.ToString());
        using var metadata = new HttpRequestMessage(HttpMethod.Get, path);
        metadata.Headers.Authorization = new AuthenticationHeaderValue("Bearer", room.Credential);
        using var response = await client.SendAsync(metadata, timeout.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task RoomAddBurstAppliesOnlyToAddAndRefillsEveryHalfSecond()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db);
        var another = await CreateRoomAsync(db);
        var clock = new ManualClock();
        await using var factory = new PastoApiFactory(db.ConnectionString,
            new Dictionary<string, string?> { ["Api:AddPerParticipant:TokenLimit"] = "100" },
            services => services.AddSingleton<TimeProvider>(clock));
        using var client = factory.CreateClient();
        var path = $"/api/rooms/{room.RoomId}/items";
        Guid itemId = default;
        for (var index = 0; index < 20; index++)
        {
            using var added = await AddAsync(room, path);
            Assert.Equal(HttpStatusCode.Created, added.StatusCode);
            if (index == 0)
                itemId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("item").GetProperty("id").GetGuid();
        }
        await ExpectLimitedAsync();
        using var delete = new HttpRequestMessage(HttpMethod.Delete, $"{path}/{itemId}");
        delete.Headers.Authorization = new AuthenticationHeaderValue("Bearer", room.Credential);
        using var deleted = await client.SendAsync(delete);
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.True((await deleted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("changed").GetBoolean());
        using var otherAdded = await AddAsync(another, $"/api/rooms/{another.RoomId}/items");
        Assert.Equal(HttpStatusCode.Created, otherAdded.StatusCode);

        clock.Advance(TimeSpan.FromMilliseconds(499));
        await ExpectLimitedAsync();
        clock.Advance(TimeSpan.FromMilliseconds(1));
        using var replenished = await AddAsync(room, path);
        Assert.Equal(HttpStatusCode.Created, replenished.StatusCode);
        await ExpectLimitedAsync();

        async Task<HttpResponseMessage> AddAsync(CreatedRoom target, string location)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, location)
            {
                Content = JsonContent.Create(new { content = "text" })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", target.Credential);
            return await client.SendAsync(request);
        }

        async Task ExpectLimitedAsync()
        {
            using var response = await AddAsync(room, path);
            Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
            Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
            Assert.Equal("RateLimited", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task IdleWebSocketSurvivesMinimumResponseRateAndStillReceivesRoomChanged()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db);
        await using var factory = Kestrel(db);
        using var client = factory.CreateClient();
        var minimum = factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MinResponseDataRate;
        Assert.Equal(8192, minimum!.BytesPerSecond);
        Assert.Equal(TimeSpan.FromSeconds(15), minimum.GracePeriod);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(55));
        var token = await RoomHubTests.NegotiateAsync(client, room.Credential, timeout.Token);
        var uri = new UriBuilder(client.BaseAddress!)
        {
            Scheme = "ws",
            Path = "/hubs/room",
            Query = $"id={Uri.EscapeDataString(token)}&access_token={room.Credential}"
        };
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(uri.Uri, timeout.Token);
        var hub = new RoomHubTests.HubMessages(socket, timeout.Token);
        await hub.HandshakeAsync();
        Assert.False((await hub.SubscribeAsync(room.RoomId)).TryGetProperty("error", out _));
        // 沒有應用程式工作；只送 SignalR protocol keepalive，以測試正常閒置的連線。
        for (var index = 0; index < 4; index++)
        {
            await Task.Delay(TimeSpan.FromSeconds(8), timeout.Token);
            await hub.PingAsync();
        }
        using var add = new HttpRequestMessage(HttpMethod.Post, $"/api/rooms/{room.RoomId}/items")
        {
            Content = JsonContent.Create(new { content = "room changed after idle" })
        };
        add.Headers.Authorization = new AuthenticationHeaderValue("Bearer", room.Credential);
        using var response = await client.SendAsync(add, timeout.Token);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await hub.ExpectHintAsync(room.RoomId, "1");
    }

    private static Task<CreatedRoom> CreateRoomAsync(PostgresTestDatabase db)
        => TestRoomCreation.Create(db, new RoomSecrets()).ExecuteAsync();

    private static PastoApiFactory Kestrel(PostgresTestDatabase db, Dictionary<string, string?>? settings = null)
    {
        var factory = new PastoApiFactory(db.ConnectionString, settings);
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        return factory;
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan time) => timestamp += time.Ticks;
    }

    private sealed class CapacityLogger : ILogger<ParticipantRateLimits>
    {
        internal List<long> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Equal(6104, eventId.Id);
            Assert.Equal(LogLevel.Warning, logLevel);
            var fields = Assert.IsAssignableFrom<IReadOnlyList<KeyValuePair<string, object?>>>(state);
            Events.Add(Assert.IsType<long>(Assert.Single(fields, field => field.Key == "SkippedRequestCount").Value));
        }
    }

    private sealed class SlowJoinContent(string code) : HttpContent
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync("{\"code\":"u8.ToArray());
            await stream.FlushAsync();
            Started.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(code) + "}"));
        }
    }
}
