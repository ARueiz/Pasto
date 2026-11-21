using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pasto.Server.Api;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Persistence;
using Pasto.Server.Infrastructure.Security;
using Xunit;
using static Pasto.IntegrationTests.TransactionTestHooks;

namespace Pasto.IntegrationTests;

public sealed class PublicJoinAdmissionTests
{
    private const string JoinPath = "/api/rooms/join";
    private const string SourceA = "198.51.100.1";
    private const string SourceB = "198.51.100.2";
    private const string MissingCode = "MISS00";

    [Fact]
    public async Task SameClosedLinkAcrossOneNatDoesNotStartCooldown()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var closed = await CreateRoomAsync(db, "CLSD01");
        var open = await CreateRoomAsync(db, "OPEN01");
        await new CloseRoom(new RoomClosureStore(db.CreateContextFactory())).ExecuteAsync(closed.RoomId, closed.ParticipantId);
        await using var factory = Factory(db, new ManualClock(), sourceBurst: 31);
        using var client = Client(factory, SourceA);
        for (var index = 0; index < 30; index++)
            await ExpectJoinAsync(client, index % 2 == 0 ? " clsd01 " : closed.Code, 404, "RoomUnavailable");
        await ExpectJoinAsync(client, open.Code, 201);
    }

    [Fact]
    public async Task DistinctCodesShareOnlyTheTrustedIpv6PrefixAndCooldownDoesNotApplyToCreationOrProtectedRest()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db, "OPEN01");
        var clock = new ManualClock();
        await using var factory = Factory(db, clock);
        using var first = Client(factory, "2001:db8:0:1::1");
        using var samePrefix = Client(factory, "2001:db8:0:1::2");
        using var otherPrefix = Client(factory, "2001:db8:0:2::1");
        for (var index = 0; index < 20; index++)
            await ExpectJoinAsync(index % 2 == 0 ? first : samePrefix, Miss(index), 404);

        await ExpectJoinAsync(samePrefix, room.Code, 429, "RateLimited", 60);
        await ExpectJoinAsync(otherPrefix, room.Code, 201);
        using var created = await first.PostAsync("/api/rooms", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await AssertProtectedRestAsync(first, room);
    }

    [Fact]
    public async Task RepeatsAndExpiredWindowEntriesDoNotConsumeDistinctSlots()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db, "OPEN01");
        var clock = new ManualClock();
        await using var factory = Factory(db, clock, sourceBurst: 40);
        using var client = Client(factory, SourceA);
        await ExpectJoinAsync(client, "  invalid-code ", 400, "InvalidRequest");
        clock.Advance(TimeSpan.FromMinutes(9));
        for (var index = 0; index < 10; index++)
            await ExpectJoinAsync(client, "INVALID-code", 400);
        for (var index = 0; index < 18; index++)
            await ExpectJoinAsync(client, Miss(index), 404);
        await ExpectJoinAsync(client, room.Code, 201);

        clock.Advance(TimeSpan.FromMinutes(1));
        await ExpectJoinAsync(client, Miss(18), 404);
        await ExpectJoinAsync(client, room.Code, 201);
        await ExpectJoinAsync(client, "invalid-code", 400);
        await ExpectJoinAsync(client, Miss(19), 429, "RateLimited", 60);
    }

    [Fact]
    public void TwentiethDistinctFailureStartsCooldownEvenAfterRepeatedCode()
    {
        var cooldown = new JoinSourceCooldown(new ManualClock(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<JoinSourceCooldown>.Instance);
        for (var index = 0; index < 30; index++)
            cooldown.RecordFailure(SourceA, " invalid ");
        for (var index = 0; index < 18; index++)
            cooldown.RecordFailure(SourceA, Miss(index));
        Assert.Null(cooldown.Check(SourceA));
        cooldown.RecordFailure(SourceA, "INVALID");
        Assert.Null(cooldown.Check(SourceA));
        cooldown.RecordFailure(SourceA, Miss(18));
        Assert.Equal(60, cooldown.Check(SourceA));
        Assert.Null(cooldown.Check(SourceB));
    }

    [Fact]
    public void CooldownBackoffCapsAndResetsAfterIdle()
    {
        var clock = new ManualClock();
        var cooldown = new JoinSourceCooldown(clock, Microsoft.Extensions.Logging.Abstractions.NullLogger<JoinSourceCooldown>.Instance);
        var nextCode = 0;
        for (var index = 0; index < 20; index++)
            cooldown.RecordFailure(SourceA, Miss(nextCode++));
        Assert.Equal(60, cooldown.Check(SourceA));
        Assert.Null(cooldown.RecordFailure(SourceA, Miss(nextCode)));
        clock.Advance(TimeSpan.FromSeconds(60));

        foreach (var delay in new[] { 120, 240, 300 })
        {
            Assert.Null(cooldown.RecordFailure(SourceA, Miss(19)));
            Assert.Null(cooldown.Check(SourceA));
            Assert.Equal(delay, cooldown.RecordFailure(SourceA, Miss(nextCode++)));
            Assert.Equal(delay, cooldown.Check(SourceA));
            clock.Advance(TimeSpan.FromSeconds(delay - 1));
            Assert.Equal(1, cooldown.Check(SourceA));
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        // t=720：最初那批已過期，視窗內剩 t=180 與 t=420 的兩筆。
        for (var index = 0; index < 17; index++)
            Assert.Null(cooldown.RecordFailure(SourceA, Miss(nextCode++)));
        Assert.Equal(300, cooldown.RecordFailure(SourceA, Miss(nextCode)));
        Assert.Equal(300, cooldown.Check(SourceA));

        clock.Advance(TimeSpan.FromMinutes(10));
        for (var index = 0; index < 20; index++)
            cooldown.RecordFailure(SourceA, Miss(index));
        Assert.Equal(60, cooldown.Check(SourceA));
    }

    [Fact]
    public async Task FirstNewLookupMissAfterCooldownImmediatelyReturns120SecondRetry()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var clock = new ManualClock();
        await using var factory = Factory(db, clock);
        using var client = Client(factory, SourceA);
        for (var index = 0; index < 20; index++)
            await ExpectJoinAsync(client, Miss(index), 404, "RoomUnavailable");
        await ExpectJoinAsync(client, Miss(20), 429, "RateLimited", 60);

        clock.Advance(TimeSpan.FromSeconds(60));
        await ExpectJoinAsync(client, Miss(0), 404, "RoomUnavailable");
        await ExpectJoinAsync(client, Miss(20), 429, "RateLimited", 120);
        await ExpectJoinAsync(client, Miss(21), 429, "RateLimited", 120);
    }

    [Fact]
    public async Task FullSourceTableFailsOpenWithoutEvictingCoolingSourceAndLogsOnlyCounts()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db, "OPEN01");
        var clock = new ManualClock();
        using var logs = new CapacityLog();
        await using var factory = Factory(db, clock, services => services.AddSingleton(services =>
            new JoinSourceCooldown(clock, services.GetRequiredService<ILogger<JoinSourceCooldown>>(), capacity: 1)), logs, sourceBurst: 22);
        using var known = Client(factory, SourceA);
        using var newSource = Client(factory, SourceB);
        using var anotherSource = Client(factory, "198.51.100.3");
        for (var index = 0; index < 20; index++)
            await ExpectJoinAsync(known, Miss(index), 404);
        await ExpectJoinAsync(known, room.Code, 429, "RateLimited", 60);

        for (var index = 0; index < 21; index++)
            await ExpectJoinAsync(newSource, Miss(index), 404);
        await ExpectJoinAsync(newSource, room.Code, 201);
        await ExpectJoinAsync(newSource, room.Code, 429, "RateLimited");
        clock.Advance(TimeSpan.FromSeconds(59));
        await ExpectJoinAsync(anotherSource, MissingCode, 404);
        await ExpectJoinAsync(known, room.Code, 429, "RateLimited", 1);
        Assert.Single(logs.Events);

        clock.Advance(TimeSpan.FromSeconds(1));
        await ExpectJoinAsync(anotherSource, MissingCode, 404);
        Assert.Collection(logs.Events, entry => AssertCapacityWarning(entry, 1), entry => AssertCapacityWarning(entry, 22));
        clock.Advance(TimeSpan.FromMinutes(10));
        var cooldown = factory.Services.GetRequiredService<JoinSourceCooldown>();
        for (var index = 0; index < 20; index++)
            cooldown.RecordFailure(SourceB, Miss(index));
        Assert.Equal(60, cooldown.Check(SourceB));
    }

    [Fact]
    public async Task OnlyLookupMissesDrivePressureAndProtectedRestAndHubRemainAvailable()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db, "OPEN01");
        var clock = new ManualClock();
        await using var factory = Factory(db, clock, sourceBurst: 650, globalBurst: 650);
        using var first = Client(factory, SourceA);
        using var blocked = Client(factory, SourceB);
        for (var index = 0; index < 300; index++)
            await ExpectJoinAsync(first, "invalid-format", 400);
        for (var index = 0; index < 20; index++)
            await ExpectJoinAsync(blocked, $"invalid-{index}", 400);
        for (var index = 0; index < 300; index++)
            await ExpectJoinAsync(blocked, MissingCode, 429, "RateLimited", 60);
        for (var index = 0; index < 6; index++)
            await ExpectJoinAsync(first, room.Code, 201);

        using var second = Client(factory, "203.0.113.2");
        for (var index = 0; index < 299; index++)
            await ExpectJoinAsync(index % 2 == 0 ? first : second, MissingCode, 404);
        for (var index = 0; index < 6; index++)
            await ExpectJoinAsync(first, room.Code, 201);
        await ExpectJoinAsync(second, MissingCode, 404);

        var measurements = new ConcurrentQueue<KeyValuePair<string, object?>[]>();
        using var listener = ObserveRejections(factory, measurements);
        for (var index = 0; index < 5; index++)
            await ExpectJoinAsync(first, room.Code, 201);
        await ExpectJoinAsync(second, room.Code, 429, "RateLimited", 1);
        Assert.True(SpinWait.SpinUntil(() => !measurements.IsEmpty, TimeSpan.FromSeconds(2)));
        AssertSafeMeasurement(Assert.Single(measurements), room.Code);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var socket = await RoomHubTests.ConnectAsync(factory, room.Credential, deadline.Token);
        var hub = new RoomHubTests.HubMessages(socket, deadline.Token);
        await hub.HandshakeAsync();
        Assert.False((await hub.SubscribeAsync(room.RoomId)).TryGetProperty("error", out _));
        await AssertProtectedRestAsync(first, room);
        await hub.ExpectHintAsync(room.RoomId, "1");
        await hub.ExpectClosedAsync(room.RoomId, "2");
    }

    [Fact]
    public void PressureLingersUntilWindowFallsThenRecoversAtTenPerSecondAndRelapseRestartsMinimum()
    {
        var clock = new ManualClock();
        var pressure = new JoinPressureLimiter(clock);
        RecordMisses(pressure, 299);
        Assert.Null(pressure.Check());
        clock.Advance(TimeSpan.FromSeconds(10));
        RecordMisses(pressure, 1);
        AssertBurst(pressure, 5);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        AssertBurst(pressure, 1);
        clock.Advance(TimeSpan.FromSeconds(49.5));
        RecordMisses(pressure, 300);
        clock.Advance(TimeSpan.FromSeconds(10));
        AssertBurst(pressure, 5);
        clock.Advance(TimeSpan.FromSeconds(50));
        AssertBurst(pressure, 20);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        AssertBurst(pressure, 1);

        clock.Advance(TimeSpan.FromSeconds(30));
        RecordMisses(pressure, 300);
        AssertBurst(pressure, 5);
        clock.Advance(TimeSpan.FromSeconds(59));
        AssertBurst(pressure, 5);
        clock.Advance(TimeSpan.FromSeconds(1));
        AssertBurst(pressure, 20);
        clock.Advance(TimeSpan.FromSeconds(59));
        AssertBurst(pressure, 20);
        clock.Advance(TimeSpan.FromSeconds(1));
        for (var index = 0; index < 60; index++)
            Assert.Null(pressure.Check());
    }

    [Fact]
    public async Task DefaultJoinBucketsAllowThirtyParticipantsFromOneSourceWithOverloadRetry()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db, "OPEN01");
        var otherRoom = await CreateRoomAsync(db, "OPEN02");
        var hold = new HoldFirstJoinChecks();
        await using var factory = Factory(db, new ManualClock(),
            services => services.AddDbContextFactory<PastoDbContext>(options => options.AddInterceptors(hold)));
        using var client = Client(factory, SourceA);
        using var otherSource = Client(factory, SourceB);
        var started = Enumerable.Range(0, 8).Select(_ => client.PostAsJsonAsync(JoinPath, new { code = room.Code })).ToArray();
        try
        {
            await hold.AllEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(Enumerable.Range(0, 22).Select(_ => ExpectJoinAsync(client, room.Code, 503, "Overloaded", 1)));
            await using var context = db.CreateContext();
            Assert.Equal(1, await context.Participants.CountAsync(participant => participant.RoomId == room.RoomId));
            await ExpectJoinAsync(otherSource, otherRoom.Code, 201);
        }
        finally
        {
            hold.Release.TrySetResult();
        }
        await AssertDistinctJoinsAsync(started);
        await AssertDistinctJoinsAsync(Enumerable.Range(0, 22).Select(RetryOverloadAsync));
        await using var verification = db.CreateContext();
        Assert.Equal(31, await verification.Participants.CountAsync(participant => participant.RoomId == room.RoomId));
        await ExpectJoinAsync(client, room.Code, 429, "RateLimited");

        async Task<HttpResponseMessage> RetryOverloadAsync(int index)
        {
            var jitter = new Random(index);
            for (var attempt = 1; ; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1) + TimeSpan.FromMilliseconds(jitter.Next(500)));
                var result = await client.PostAsJsonAsync(JoinPath, new { code = room.Code });
                if (result.StatusCode != HttpStatusCode.ServiceUnavailable || attempt == 3) return result;
                using (result)
                {
                    Assert.Equal(TimeSpan.FromSeconds(1), result.Headers.RetryAfter?.Delta);
                    Assert.Equal("Overloaded", (await result.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
                }
            }
        }
    }

    [Fact]
    public async Task ConcurrentRoomJoinsUseExactlyThirtySlotsAndRestoreOneEveryTwoSeconds()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db, "OPEN01");
        var other = await CreateRoomAsync(db, "OPEN02");
        var clock = new ManualClock();
        await using var factory = Factory(db, clock, services => services.Configure<HttpWorkOptions>(options =>
        {
            options.JoinLimit = 48;
            options.JoinPerSource = 30;
        }));
        using var first = Client(factory, SourceA);
        using var second = Client(factory, SourceB);
        await AssertDistinctJoinsAsync(Enumerable.Range(0, 30).Select(index =>
            (index % 2 == 0 ? first : second).PostAsJsonAsync(JoinPath, new { code = room.Code })));

        await ExpectJoinAsync(first, " open01 ", 429, "RateLimited", 2);
        await ExpectJoinAsync(first, other.Code, 201);
        using var restored = await SendAuthorizedAsync(first, room, HttpMethod.Get, "/me");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);
        clock.Advance(TimeSpan.FromSeconds(1));
        await ExpectJoinAsync(second, room.Code, 429, "RateLimited", 1);
        clock.Advance(TimeSpan.FromSeconds(1));
        var contenders = await Task.WhenAll(first.PostAsJsonAsync(JoinPath, new { code = room.Code }),
            second.PostAsJsonAsync(JoinPath, new { code = room.Code }));
        try
        {
            Assert.Equal(new[] { HttpStatusCode.Created, HttpStatusCode.TooManyRequests },
                contenders.Select(response => response.StatusCode).Order());
        }
        finally
        {
            foreach (var response in contenders) response.Dispose();
        }
        await using var context = db.CreateContext();
        Assert.Equal(32, await context.Participants.CountAsync(p => p.RoomId == room.RoomId));
        Assert.Equal(2, await context.Participants.CountAsync(p => p.RoomId == other.RoomId));
    }

    [Fact]
    public async Task FailedSaveDoesNotChargeButCommitAttemptKeepsItsToken()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db, "OPEN01");
        var clock = new ManualClock();
        var quota = new RoomJoinQuota(clock);
        var save = new FailAfterSaveInterceptor(null);
        var commit = new FailBeforeCommitInterceptor();
        var saveStore = new RoomJoinStore(db.CreateContextFactory(save), quota);
        var commitStore = new RoomJoinStore(db.CreateContextFactory(commit), quota);
        var store = new RoomJoinStore(db.CreateContextFactory(), quota);
        var secrets = new RoomSecrets();
        for (var index = 0; index < 29; index++)
            await new JoinRoom(store, secrets).ExecuteAsync(room.Code);
        await Assert.ThrowsAsync<InjectedFailureException>(() => new JoinRoom(saveStore, secrets).ExecuteAsync(room.Code));
        await Assert.ThrowsAsync<InjectedFailureException>(() => new JoinRoom(commitStore, secrets).ExecuteAsync(room.Code));
        Assert.True(commit.ReachedCommit);
        await Assert.ThrowsAsync<RoomJoinRateLimitedException>(() => new JoinRoom(store, secrets).ExecuteAsync(room.Code));
        await using var context = db.CreateContext();
        Assert.Equal(30, await context.Participants.CountAsync(p => p.RoomId == room.RoomId));
    }

    [Fact]
    public async Task LastRoomTokenIsChargedBeforeTheWaitingTransactionCanCheck()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db, "OPEN01");
        var quota = new RoomJoinQuota(new ManualClock());
        var secrets = new RoomSecrets();
        var join = new JoinRoom(new RoomJoinStore(db.CreateContextFactory(), quota), secrets);
        for (var index = 0; index < 29; index++)
            await join.ExecuteAsync(room.Code);

        var hold = new HoldAfterSaveInterceptor();
        var attempt = new LockAttemptInterceptor();
        var first = new JoinRoom(new RoomJoinStore(db.CreateContextFactory(hold), quota), secrets);
        var second = new JoinRoom(new RoomJoinStore(db.CreateContextFactory(attempt), quota), secrets);
        var winningJoin = first.ExecuteAsync(room.Code);
        Task<JoinedRoom> waitingJoin;
        try
        {
            await hold.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            waitingJoin = second.ExecuteAsync(room.Code);
            var pid = await attempt.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitForBlockedBackendAsync(db, pid);
        }
        finally
        {
            hold.Release.TrySetResult();
        }

        await winningJoin;
        var rejected = await Assert.ThrowsAsync<RoomJoinRateLimitedException>(() => waitingJoin);
        Assert.Equal(2, rejected.RetryAfterSeconds);
        await using var context = db.CreateContext();
        Assert.Equal(31, await context.Participants.CountAsync(p => p.RoomId == room.RoomId));
    }

    [Fact]
    public async Task ExpiredAndFullRoomsDoNotChargeAndRecoveringCredentialsConsumesNoJoinSlot()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db, "OPEN01");
        await using var factory = Factory(db, new ManualClock(), sourceBurst: 40);
        using var client = Client(factory, SourceA);
        for (var index = 0; index < 29; index++)
            await ExpectJoinAsync(client, room.Code, 201);
        using var restored = await SendAuthorizedAsync(client, room, HttpMethod.Get, "/me");
        Assert.Equal(HttpStatusCode.OK, restored.StatusCode);

        await using var context = db.CreateContext();
        var rooms = context.Rooms.Where(roomRow => roomRow.Id == room.RoomId);
        var original = await rooms.AsNoTracking().SingleAsync();
        var expiredAt = original.CreatedAt.AddMinutes(-1);
        var expiredCreatedAt = expiredAt.AddHours(-1);
        await rooms.ExecuteUpdateAsync(set => set.SetProperty(row => row.CreatedAt, expiredCreatedAt)
            .SetProperty(row => row.ExpiresAt, expiredAt));
        await ExpectJoinAsync(client, room.Code, 404, "RoomUnavailable");
        await rooms.ExecuteUpdateAsync(set => set.SetProperty(row => row.CreatedAt, original.CreatedAt)
            .SetProperty(row => row.ExpiresAt, original.ExpiresAt));

        // Seed temporary participants to exercise the lifetime ceiling independently of Join quota.
        var fixtureTime = original.CreatedAt.AddHours(1);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO room_participants (id, room_id, credential_hash, created_at)
            SELECT gen_random_uuid(), {room.RoomId}, decode(md5(i::text) || md5(i::text), 'hex'), {fixtureTime}
            FROM generate_series(1, 970) AS i
            """);
        await ExpectJoinAsync(client, room.Code, 409, "ParticipantLimitReached");
        await context.Participants.Where(participant => participant.RoomId == room.RoomId && participant.CreatedAt == fixtureTime)
            .ExecuteDeleteAsync();
        await ExpectJoinAsync(client, room.Code, 201);
        await ExpectJoinAsync(client, room.Code, 429, "RateLimited", 2);
        Assert.Equal(31, await context.Participants.CountAsync(p => p.RoomId == room.RoomId));
    }

    private static async Task AssertProtectedRestAsync(HttpClient client, CreatedRoom room)
    {
        using var read = await SendAuthorizedAsync(client, room, HttpMethod.Get, "");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var write = await SendAuthorizedAsync(client, room, HttpMethod.Post, "/items", new { content = "pasto" });
        Assert.Equal(HttpStatusCode.Created, write.StatusCode);
        using var close = await SendAuthorizedAsync(client, room, HttpMethod.Delete, "");
        Assert.Equal(HttpStatusCode.NoContent, close.StatusCode);
    }

    private static async Task<HttpResponseMessage> SendAuthorizedAsync(HttpClient client, CreatedRoom room,
        HttpMethod method, string suffix, object? body = null)
    {
        using var request = new HttpRequestMessage(method, $"/api/rooms/{room.RoomId}{suffix}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", room.Credential);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static PastoApiFactory Factory(PostgresTestDatabase db, ManualClock clock,
        Action<IServiceCollection>? configure = null, ILoggerProvider? logs = null, int? sourceBurst = null, int? globalBurst = null)
    {
        var settings = new Dictionary<string, string?> { ["Api:TrustedProxies:0"] = "127.0.0.1" };
        if (sourceBurst is int perSource)
            settings["Api:JoinPerIp:TokenLimit"] = perSource.ToString(CultureInfo.InvariantCulture);
        if (globalBurst is int global)
            settings["Api:JoinGlobal:TokenLimit"] = global.ToString(CultureInfo.InvariantCulture);
        return new PastoApiFactory(db.ConnectionString, settings, services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            configure?.Invoke(services);
        }, logs);
    }

    private static HttpClient Client(PastoApiFactory factory, string source)
    {
        var client = new HttpClient(factory.Server.CreateHandler(context => context.Connection.RemoteIpAddress = IPAddress.Loopback))
        {
            BaseAddress = new Uri("http://localhost")
        };
        client.DefaultRequestHeaders.Add("X-Forwarded-For", source);
        return client;
    }

    private static string Miss(int index) => $"M{index:D5}";

    private static Task<CreatedRoom> CreateRoomAsync(PostgresTestDatabase db, string code)
        => TestRoomCreation.Create(db, new SequenceRoomSecrets(code)).ExecuteAsync();

    private static async Task ExpectJoinAsync(HttpClient client, string code, int status, string? error = null, int? retry = null)
    {
        using var response = await client.PostAsJsonAsync(JoinPath, new { code });
        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        if (status == 429) Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
        if (retry is int seconds) Assert.Equal(TimeSpan.FromSeconds(seconds), response.Headers.RetryAfter?.Delta);
        if (error is not null)
            Assert.Equal(error, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }

    private static async Task AssertDistinctJoinsAsync(IEnumerable<Task<HttpResponseMessage>> joins)
    {
        var responses = await Task.WhenAll(joins);
        try
        {
            var participantIds = new HashSet<Guid>();
            foreach (var response in responses)
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                Assert.True(participantIds.Add(body.GetProperty("participantId").GetGuid()));
            }
        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    private static void RecordMisses(JoinPressureLimiter pressure, int count)
    {
        for (var index = 0; index < count; index++)
            pressure.RecordLookupMiss();
    }

    private static void AssertBurst(JoinPressureLimiter pressure, int count)
    {
        for (var index = 0; index < count; index++)
            Assert.Null(pressure.Check());
        Assert.Equal(1, pressure.Check());
    }

    private static MeterListener ObserveRejections(PastoApiFactory factory, ConcurrentQueue<KeyValuePair<string, object?>[]> measurements)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Name == "http.server.request.duration" &&
                ReferenceEquals(factory.Services.GetRequiredService<IMeterFactory>(), instrument.Meter.Scope))
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (var (key, value) in tags)
                if (key == "pasto.rate_limit.reason" && Equals(value, "join_pressure"))
                    measurements.Enqueue(tags.ToArray());
        });
        listener.Start();
        return listener;
    }

    private static void AssertSafeMeasurement(KeyValuePair<string, object?>[] measurement, string code)
    {
        Assert.Contains(new("http.route", JoinPath), measurement);
        Assert.Contains(new("http.response.status_code", 429), measurement);
        var tags = string.Join(" ", measurement);
        Assert.DoesNotContain(code, tags);
        Assert.DoesNotContain(Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(code.AsSpan()))[..16]), tags);
        Assert.DoesNotContain(SourceA, tags);
    }

    private static void AssertCapacityWarning((EventId Event, LogLevel Level, string Message, Exception? Exception) entry, int skipped)
    {
        Assert.Equal("JoinCooldownSourceCapacityReached", entry.Event.Name);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.Equal($"Join cooldown source table was full. Skipped recording {skipped} misses since the previous report.", entry.Message);
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref timestamp, duration.Ticks);
    }

    private sealed class HoldFirstJoinChecks : DbCommandInterceptor
    {
        private int arrivals;
        internal TaskCompletionSource AllEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
            {
                var current = Interlocked.Increment(ref arrivals);
                if (current == 8) AllEntered.TrySetResult();
                if (current <= 8) await Release.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            }
            return result;
        }
    }

    private sealed class CapacityLog : ILoggerProvider, ILogger
    {
        internal ConcurrentQueue<(EventId Event, LogLevel Level, string Message, Exception? Exception)> Events { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Dispose() { }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 6103) Events.Enqueue((eventId, logLevel, formatter(state, exception), exception));
        }
    }
}
