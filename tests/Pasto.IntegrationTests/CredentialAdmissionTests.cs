using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pasto.Server.Api;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Persistence;
using Pasto.Server.Infrastructure.Security;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class CredentialAdmissionTests
{
    private const string SourceA = "2001:db8:1:2::1";
    private const string SamePrefix = "2001:db8:1:2::2";
    private const string SourceB = "2001:db8:1:3::1";
    private const string Negotiate = "/hubs/room/negotiate";
    private const string MoveToCooldown = "CredentialFailureRateLimited";

    [Fact]
    public async Task MissesSharePrefixQuotaAndOnlyDatabaseCandidatesUseTheSharedFloor()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db);
        var queries = new VerifierQueries();
        var settings = Settings(failureBurst: 2, sourceBurst: 3, globalBurst: 8);
        await using var factory = Factory(db, queries, settings);
        var path = $"/api/rooms/{room.RoomId}";
        using var client = factory.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            await ExpectAsync(await SendAsync(factory, SourceA, "GET", path + "/me"), 401, "ParticipantUnauthorized");
            await ExpectAsync(await SendAsync(factory, SourceA, "GET", path + "/me", "short"), 401, "ParticipantUnauthorized");
            await ExpectAsync(await SendAsync(factory, SourceA, "GET", path + "/me", new string('.', 22)), 401, "ParticipantUnauthorized");
        }
        Assert.Equal(0, queries.Count);
        await ExpectAsync(await SendAsync(factory, SourceA, "GET", path + "/me", Fake(1)), 401, "ParticipantUnauthorized");
        await ExpectAsync(await SendAsync(factory, SamePrefix, "POST", Negotiate, Fake(2)), 401, "ParticipantUnauthorized");
        await ExpectAsync(await SendAsync(factory, SourceA, "GET", path + "/me", Fake(3)), 429, MoveToCooldown);
        await ExpectAsync(await SendAsync(factory, SamePrefix, "GET", path + "/me", room.Credential), 429, MoveToCooldown);
        await ExpectAsync(await SendAsync(factory, SamePrefix, "POST", Negotiate, Fake(1)), 401, "ParticipantUnauthorized");
        Assert.Equal(2, queries.Count);

        await ExpectAsync(await SendAsync(factory, SourceB, "GET", path + "/me", room.Credential), 200);
        await ExpectAsync(await SendAsync(factory, SourceB, "GET", path, room.Credential), 200);
        await ExpectAsync(await SendAsync(factory, SourceB, "POST", Negotiate, room.Credential), 200);
        await ExpectAsync(await SendAsync(factory, SourceB, "GET", path + "/items", room.Credential), 429, "RateLimited");
        Assert.Equal(5, queries.Count);

    }

    [Fact]
    public async Task DifferentPrefixCanUseEveryProtectedOperationAfterAnotherSourceRunsOut()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db);
        var queries = new VerifierQueries();
        await using var factory = Factory(db, queries, Settings(failureBurst: 1));
        using var client = factory.CreateClient();
        var path = $"/api/rooms/{room.RoomId}";

        await ExpectAsync(await SendAsync(factory, SourceA, "GET", path, Fake(1)), 401, "ParticipantUnauthorized");
        await ExpectAsync(await SendAsync(factory, SourceA, "GET", path, Fake(2)), 429, MoveToCooldown);
        await ExpectAsync(await SendAsync(factory, SourceB, "GET", path + "/me", room.Credential), 200);
        await ExpectAsync(await SendAsync(factory, SourceB, "GET", path, room.Credential), 200);
        await ExpectAsync(await SendAsync(factory, SourceB, "GET", path + "/items", room.Credential), 200);
        await ExpectAsync(await SendAsync(factory, SourceB, "POST", path + "/items", room.Credential, new { content = "different prefix" }), 201);
        await using var context = db.CreateContext();
        var item = await context.Items.SingleAsync();
        await ExpectAsync(await SendAsync(factory, SourceB, "DELETE", path + $"/items/{item.Id}", room.Credential), 200);
        await ExpectAsync(await SendAsync(factory, SourceB, "DELETE", path, room.Credential), 204);
        Assert.Equal(7, queries.Count);
    }

    [Fact]
    public async Task CreateAndJoinUseIpv6PrefixesForTheirSeparateSourceBuckets()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db);
        var settings = Settings();
        foreach (var key in new[] { "CreatePerIp", "JoinPerIp" })
        {
            settings[$"Api:{key}:TokenLimit"] = "1";
            settings[$"Api:{key}:TokensPerPeriod"] = "1";
            settings[$"Api:{key}:PeriodSeconds"] = "86400";
        }
        await using var factory = new PastoApiFactory(db.ConnectionString, settings);
        await ExpectAsync(await SendAsync(factory, SourceA, "POST", "/api/rooms"), 201);
        await ExpectAsync(await SendAsync(factory, SamePrefix, "POST", "/api/rooms"), 429, "RateLimited");
        await ExpectAsync(await SendAsync(factory, SourceB, "POST", "/api/rooms"), 201);
        var body = new { code = room.Code };
        await ExpectAsync(await SendAsync(factory, SourceA, "POST", "/api/rooms/join", body: body), 201);
        await ExpectAsync(await SendAsync(factory, SamePrefix, "POST", "/api/rooms/join", body: body), 429, "RateLimited");
        await ExpectAsync(await SendAsync(factory, SourceB, "POST", "/api/rooms/join", body: body), 201);
    }

    [Fact]
    public async Task MappedIpv4SharesFailuresAndWebsocketUpgradeIsRejectedBeforeLookup()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db);
        var queries = new VerifierQueries();
        await using var factory = Factory(db, queries, Settings(failureBurst: 1));
        using var client = factory.CreateClient();
        using var negotiated = await SendAsync(factory, "192.0.2.8", "POST", RoomHubTests.NegotiatePath, room.Credential);
        var id = Uri.EscapeDataString(await RoomHubTests.ReadConnectionTokenAsync(negotiated));
        await ExpectAsync(await SendAsync(factory, "192.0.2.8", "POST", Negotiate, Fake(1)), 401, "ParticipantUnauthorized");
        await ExpectAsync(await SendAsync(factory, "::ffff:192.0.2.8", "POST", Negotiate, Fake(2)), 429, MoveToCooldown);
        var ws = factory.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request =>
        {
            request.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
            request.Headers["X-Forwarded-For"] = "::ffff:192.0.2.8";
        };
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ws.ConnectAsync(new Uri($"ws://localhost/hubs/room?id={id}&access_token={Fake(3)}"), cancel.Token));
        Assert.Contains("429", error.Message);
        Assert.Equal(2, queries.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WebsocketUpgradeSharesSourceAndGlobalDatabaseFloor(bool global)
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db);
        var queries = new VerifierQueries();
        var settings = Settings(sourceBurst: global ? 10 : 2, globalBurst: global ? 2 : 10);
        await using var factory = Factory(db, queries, settings);
        using var client = factory.CreateClient();
        using var negotiated = await SendAsync(factory, SourceA, "POST", RoomHubTests.NegotiatePath, room.Credential);
        var id = Uri.EscapeDataString(await RoomHubTests.ReadConnectionTokenAsync(negotiated));
        var location = $"/api/rooms/{Guid.NewGuid()}/me";
        await ExpectAsync(await SendAsync(factory, SourceA, "GET", location, Fake(1)), 401);
        var source = global ? SourceB : SamePrefix;
        await ExpectAsync(await SendAsync(factory, source, "POST", Negotiate, Fake(2)), 429, "RateLimited");
        var ws = factory.Server.CreateWebSocketClient();
        ws.ConfigureRequest = request =>
        {
            request.HttpContext.Connection.RemoteIpAddress = IPAddress.Loopback;
            request.Headers["X-Forwarded-For"] = source;
        };
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ws.ConnectAsync(new Uri($"ws://localhost/hubs/room?id={id}&access_token={Fake(3)}"), cancel.Token));
        Assert.Contains("429", error.Message);
        Assert.Equal(2, queries.Count);
    }

    [Fact]
    public async Task CrossRoomReadsAndWritesSpendOnlyTheParticipantsReadQuota()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db);
        var other = await CreateRoomAsync(db);
        var settings = Settings();
        settings["Api:ReadPerParticipant:TokenLimit"] = "2";
        settings["Api:ReadPerParticipant:PeriodSeconds"] = "86400";
        settings["Api:AddPerParticipant:TokenLimit"] = "1";
        settings["Api:DeletePerParticipant:TokenLimit"] = "1";
        await using var factory = Factory(db, new VerifierQueries(), settings);
        using var client = factory.CreateClient();
        var foreign = $"/api/rooms/{other.RoomId}";
        var own = $"/api/rooms/{room.RoomId}";
        await ExpectAsync(await SendAsync(factory, SourceA, "GET", foreign + "/me", room.Credential), 403, "ParticipantUnauthorized");
        await ExpectAsync(await SendAsync(factory, SourceA, "POST", foreign + "/items", room.Credential, new { content = "foreign" }),
            403, "ParticipantUnauthorized");
        await ExpectAsync(await SendAsync(factory, SourceA, "DELETE", foreign, room.Credential), 429, "RateLimited");
        await ExpectAsync(await SendAsync(factory, SourceA, "GET", own, room.Credential), 429, "RateLimited");
        await ExpectAsync(await SendAsync(factory, SourceA, "POST", own + "/items", room.Credential, new { content = "own" }), 201);
        await using var context = db.CreateContext();
        var item = await context.Items.SingleAsync();
        Assert.Equal(room.RoomId, item.RoomId);
        await ExpectAsync(await SendAsync(factory, SourceA, "DELETE", own + $"/items/{item.Id}", room.Credential), 200);
    }

    [Fact]
    public async Task ClearedRoomsRepeatedlyReturn401AndNewCredentialOnSameNatCanReadAndWrite()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var old = await CreateRoomAsync(db);
        var latest = await CreateRoomAsync(db);
        await using (var context = db.CreateContext())
            await context.Rooms.Where(room => room.Id == old.RoomId).ExecuteDeleteAsync();
        const int coldRequests = 4;
        var queries = new VerifierQueries(coldRequests);
        await using var factory = Factory(db, queries, Settings(failureBurst: 2));
        using var client = factory.CreateClient();
        const string nat = "192.0.2.8";
        var oldPath = $"/api/rooms/{old.RoomId}";
        async Task RequestWithOldCredentialAsync(int index)
        {
            var (method, endpoint) = index % 2 == 0 ? ("GET", oldPath + "/me") : ("POST", Negotiate);
            await ExpectAsync(await SendAsync(factory, nat, method, endpoint, old.Credential), 401, "ParticipantUnauthorized");
        }
        var initial = Task.WhenAll(Enumerable.Range(0, coldRequests).Select(RequestWithOldCredentialAsync));
        try
        {
            await queries.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            queries.Release.TrySetResult();
        }
        await initial;
        await Task.WhenAll(Enumerable.Range(0, 60).Select(RequestWithOldCredentialAsync));
        Assert.Equal(coldRequests, queries.Count);
        var path = $"/api/rooms/{latest.RoomId}";
        await ExpectAsync(await SendAsync(factory, nat, "GET", path + "/me", latest.Credential), 200);
        await ExpectAsync(await SendAsync(factory, nat, "POST", path + "/items", latest.Credential, new { content = "new room" }), 201);
        Assert.Equal(coldRequests + 2, queries.Count);
    }

    [Fact]
    public async Task ConcurrentMissesThatPassThePrecheckStillCannotExceedTheSourceFloor()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        const int floor = 4;
        var queries = new VerifierQueries(floor);
        await using var factory = Factory(db, queries, Settings(failureBurst: 1, sourceBurst: floor));
        using var client = factory.CreateClient();
        var requests = Enumerable.Range(0, 12)
            .Select(index => SendAsync(factory, SourceA, "POST", Negotiate, Fake(index))).ToArray();
        try
        {
            await queries.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(floor, queries.Count);
        }
        finally
        {
            queries.Release.TrySetResult();
        }
        var responses = await Task.WhenAll(requests);
        Assert.Equal(floor, queries.Count);
        Assert.Equal(floor, responses.Count(response => response.StatusCode == HttpStatusCode.Unauthorized));
        Assert.Equal(requests.Length - floor, responses.Count(response => response.StatusCode == HttpStatusCode.TooManyRequests));
        foreach (var response in responses) response.Dispose();
        await ExpectAsync(await SendAsync(factory, SourceA, "POST", Negotiate, Fake(99)), 429, MoveToCooldown);
        Assert.Equal(floor, queries.Count);
    }

    [Fact]
    public void FailureCacheAndSourceCapacityAreBoundedAndUseMonotonicExpiry()
    {
        var clock = new ManualClock();
        var options = Options.Create(new ApiOptions { CredentialFailuresPerSource = new() { TokenLimit = 1, TokensPerPeriod = 1, PeriodSeconds = 1 } });
        var limiter = new ParticipantCredentialFailures(options, clock, NullLogger<ParticipantCredentialFailures>.Instance,
            cacheCapacity: 2, sourceCapacity: 1);
        PreparedCredential Credential(int id, string ip) => new(new byte[32], Fake(id), ip);
        var first = Credential(1, SourceA);
        var second = Credential(2, SourceB);
        limiter.RecordMiss(first);
        limiter.RecordMiss(second);
        Assert.Null(limiter.Check(first, out var cached));
        Assert.True(cached);
        Assert.Null(limiter.Check(Credential(3, "192.0.2.8"), out cached));
        Assert.False(cached);
        Assert.Null(limiter.Check(second, out cached));
        Assert.True(cached);
        Assert.Null(limiter.Check(Credential(3, SourceB), out cached));
        Assert.False(cached);
        clock.Advance(TimeSpan.FromSeconds(1));
        var third = Credential(3, SourceA);
        limiter.RecordMiss(third);
        Assert.Equal(1, limiter.Check(first, out cached));
        Assert.False(cached);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Null(limiter.Check(second, out cached));
        Assert.False(cached);
        clock.Advance(TimeSpan.FromMinutes(6));
        limiter.RecordMiss(Credential(4, SourceB));
        Assert.Null(limiter.Check(Credential(5, "192.0.2.8"), out cached));
        Assert.False(cached);
        Assert.Equal(1, limiter.Check(Credential(6, SourceB), out cached));
    }

    [Fact]
    public async Task FullFailureSourceTableLeavesNewSourcesSubjectToDatabaseFloorOnly()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(db);
        var queries = new VerifierQueries();
        var clock = new ManualClock();
        using var logs = new ThrottleLogs(6102);
        const int sourceFloor = 6;
        const int globalFloor = 16;
        await using var factory = new PastoApiFactory(db.ConnectionString,
            Settings(failureBurst: 1, sourceBurst: sourceFloor, globalBurst: globalFloor), services =>
            {
                services.AddDbContextFactory<PastoDbContext>(options => options.AddInterceptors(queries));
                services.AddSingleton(serviceProvider => new ParticipantCredentialFailures(
                    serviceProvider.GetRequiredService<IOptions<ApiOptions>>(), clock,
                    serviceProvider.GetRequiredService<ILogger<ParticipantCredentialFailures>>(), sourceCapacity: 2));
            }, logs);
        var path = $"/api/rooms/{room.RoomId}";
        await ExpectAsync(await SendAsync(factory, SourceA, "GET", path + "/me", Fake(1)), 401, "ParticipantUnauthorized");
        await ExpectAsync(await SendAsync(factory, SourceB, "POST", Negotiate, Fake(2)), 401, "ParticipantUnauthorized");
        Assert.Empty(logs.Events);

        const string untrackedSource = "2001:db8:1:4::1";
        for (var i = 0; i < sourceFloor; i++)
            await ExpectAsync(await SendAsync(factory, untrackedSource, "POST", Negotiate, Fake(3 + i)), 401, "ParticipantUnauthorized");
        Assert.Equal(2 + sourceFloor, queries.Count);
        await ExpectAsync(await SendAsync(factory, untrackedSource, "POST", Negotiate, Fake(3)), 401, "ParticipantUnauthorized");
        await ExpectAsync(await SendAsync(factory, "2001:db8:1:4::2", "POST", Negotiate, Fake(9)), 429, "RateLimited");
        await ExpectAsync(await SendAsync(factory, SamePrefix, "GET", path + "/me", room.Credential), 429, MoveToCooldown);
        Assert.Equal(2 + sourceFloor, queries.Count);
        Assert.Single(logs.Events);

        clock.Advance(TimeSpan.FromSeconds(59));
        const string nextSource = "2001:db8:1:5::1";
        await ExpectAsync(await SendAsync(factory, nextSource, "POST", Negotiate, Fake(10)), 401, "ParticipantUnauthorized");
        Assert.Single(logs.Events);
        clock.Advance(TimeSpan.FromSeconds(1));
        await ExpectAsync(await SendAsync(factory, "2001:db8:1:6::1", "POST", Negotiate, Fake(11)), 401, "ParticipantUnauthorized");

        const string validSource = "2001:db8:1:7::1";
        await ExpectAsync(await SendAsync(factory, validSource, "GET", path + "/me", room.Credential), 200);
        await ExpectAsync(await SendAsync(factory, validSource, "GET", path + "/items", room.Credential), 200);
        await ExpectAsync(await SendAsync(factory, validSource, "POST", Negotiate, room.Credential), 200);
        await ExpectAsync(await SendAsync(factory, validSource, "POST", path + "/items", room.Credential, new { content = "same room" }), 201);
        await using var context = db.CreateContext();
        var item = await context.Items.SingleAsync();
        await ExpectAsync(await SendAsync(factory, validSource, "DELETE", path + $"/items/{item.Id}", room.Credential), 200);
        await ExpectAsync(await SendAsync(factory, validSource, "DELETE", path, room.Credential), 204);
        Assert.Equal(globalFloor, queries.Count);
        await ExpectAsync(await SendAsync(factory, "2001:db8:1:8::1", "POST", Negotiate, Fake(12)), 429, "RateLimited");
        Assert.Equal(globalFloor, queries.Count);

        Assert.Collection(logs.Events,
            entry => AssertSkippedMissSummary(entry, 1),
            entry => AssertSkippedMissSummary(entry, 7));
    }

    private static void AssertSkippedMissSummary((EventId Event, LogLevel Level, string Message) entry, int count)
    {
        Assert.Equal("ParticipantCredentialFailureSourceCapacityReached", entry.Event.Name);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal($"Credential failure source table was full. Skipped recording {count} misses since the previous report.", entry.Message);
    }

    [Fact]
    public async Task CredentialRejectionsEmitSafeHttpMetricsAndThrottledLogs()
    {
        await using var db = await PostgresTestDatabase.CreateAsync();
        using var logs = new ThrottleLogs();
        await using var factory = new PastoApiFactory(db.ConnectionString, Settings(failureBurst: 1), logs: logs);
        using var client = factory.CreateClient();
        var measurements = new ConcurrentQueue<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Name == "http.server.request.duration" &&
                ReferenceEquals(factory.Services.GetRequiredService<IMeterFactory>(), instrument.Meter.Scope))
                meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, _, tags, _) =>
        {
            foreach (var (key, value) in tags)
                if (key == "pasto.rate_limit.reason" && Equals(value, "credential_failure"))
                    measurements.Enqueue(tags.ToArray());
        });
        listener.Start();
        await ExpectAsync(await SendAsync(factory, SourceA, "POST", Negotiate, Fake(1)), 401);
        await ExpectAsync(await SendAsync(factory, SourceA, "POST", Negotiate, Fake(2)), 429, MoveToCooldown);
        await ExpectAsync(await SendAsync(factory, SourceA, "POST", Negotiate, Fake(3)), 429, MoveToCooldown);
        Assert.True(SpinWait.SpinUntil(() => measurements.Count >= 2, TimeSpan.FromSeconds(2)));
        Assert.Equal(2, measurements.Count);
        foreach (var measurement in measurements)
        {
            Assert.Contains(new("http.response.status_code", 429), measurement);
            Assert.Contains(new("http.route", Negotiate), measurement);
            Assert.DoesNotContain(SourceA, string.Join(" ", measurement));
            Assert.DoesNotContain(Fake(2), string.Join(" ", measurement));
        }
        var message = Assert.Single(logs.Events).Message;
        Assert.DoesNotContain(SourceA, message);
        Assert.DoesNotContain(Fake(2), message);
        Assert.DoesNotContain(Convert.ToHexString(ParticipantCredential.HashValue(Fake(2))), message);
    }

    private static string Fake(int index) => index.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(22, 'X');
    private static Task<CreatedRoom> CreateRoomAsync(PostgresTestDatabase db)
        => TestRoomCreation.Create(db, new RoomSecrets()).ExecuteAsync();

    private static Dictionary<string, string?> Settings(int failureBurst = 2, int sourceBurst = 60, int globalBurst = 100)
    {
        var settings = new Dictionary<string, string?> { ["Api:TrustedProxies:0"] = "127.0.0.1" };
        foreach (var (key, burst) in new[] { ("CredentialFailuresPerSource", failureBurst), ("ReadPerIp", sourceBurst), ("ReadGlobal", globalBurst) })
        {
            settings[$"Api:{key}:TokenLimit"] = burst.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings[$"Api:{key}:TokensPerPeriod"] = "1";
            settings[$"Api:{key}:PeriodSeconds"] = "86400";
        }
        return settings;
    }

    private static PastoApiFactory Factory(PostgresTestDatabase db, VerifierQueries queries, Dictionary<string, string?> settings)
        => new(db.ConnectionString, settings, services =>
            services.AddDbContextFactory<PastoDbContext>(options => options.AddInterceptors(queries)));

    private static async Task<HttpResponseMessage> SendAsync(PastoApiFactory factory, string source, string method, string path,
        string? credential = null, object? body = null)
    {
        using var client = new HttpClient(factory.Server.CreateHandler(context =>
            context.Connection.RemoteIpAddress = IPAddress.Loopback))
        {
            BaseAddress = new Uri("http://localhost")
        };
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        request.Headers.Add("X-Forwarded-For", source);
        if (credential is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        if (body is not null) request.Content = JsonContent.Create(body);
        return await client.SendAsync(request);
    }

    private static async Task ExpectAsync(HttpResponseMessage response, int status, string? code = null)
    {
        using (response)
        {
            Assert.Equal(status, (int)response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            if (status == 429) Assert.True(response.Headers.RetryAfter?.Delta > TimeSpan.Zero);
            if (code is null) return;
            var document = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(code, document.GetProperty("code").GetString());
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => timestamp;
        internal void Advance(TimeSpan time) => timestamp += time.Ticks;
    }

    private sealed class VerifierQueries(int holdAt = int.MaxValue) : DbCommandInterceptor
    {
        private int count;
        internal int Count => Volatile.Read(ref count);
        internal TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.Ordinal) &&
                command.CommandText.Contains("credential_hash =", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref count) >= holdAt) Held.TrySetResult();
                if (holdAt != int.MaxValue) await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    private sealed class ThrottleLogs(int eventIdToCapture = 6100) : ILoggerProvider, ILogger
    {
        internal ConcurrentQueue<(EventId Event, LogLevel Level, string Message)> Events { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == eventIdToCapture) Events.Enqueue((eventId, logLevel, formatter(state, exception)));
        }
        public void Dispose() { }
    }
}
