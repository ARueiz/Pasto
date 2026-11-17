using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Pasto.Server.Application.Items;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Persistence;
using Pasto.Server.Infrastructure.Security;
using Xunit;
using static Pasto.IntegrationTests.TransactionTestHooks;

namespace Pasto.IntegrationTests;

public sealed class ParticipantApiTests
{
    [Fact]
    public async Task Join_NormalizesCodeAndIssuesIndependentIdentity_MeRestoresWithoutWrites()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        await new AddItem(new ItemAdditionStore(database.CreateContextFactory())).ExecuteAsync(room.RoomId, room.ParticipantId, "text");
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var joined = await client.PostAsJsonAsync("/api/rooms/join", new { code = " room01 ", participantId = room.ParticipantId, isCreator = true });
        Assert.Equal(HttpStatusCode.Created, joined.StatusCode);
        using var body = JsonDocument.Parse(await joined.Content.ReadAsStringAsync());
        var id = body.RootElement.GetProperty("participantId").GetGuid();
        var credential = body.RootElement.GetProperty("credential").GetString()!;
        Assert.NotEqual(room.ParticipantId, id);
        Assert.NotEqual(room.Credential, credential);
        Assert.Equal(room.RoomId, body.RootElement.GetProperty("roomId").GetGuid());
        Assert.Equal("/r/ROOM01", body.RootElement.GetProperty("joinUrl").GetString());
        Assert.Equal("1", body.RootElement.GetProperty("version").GetString());
        Assert.False(body.RootElement.GetProperty("isCreator").GetBoolean());
        Assert.True(joined.Headers.CacheControl?.NoStore);
        for (var index = 0; index < 2; index++)
        {
            using var me = await GetMeAsync(client, room.RoomId, credential);
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
            Assert.True(me.Headers.CacheControl?.NoStore);
            using var restored = JsonDocument.Parse(await me.Content.ReadAsStringAsync());
            Assert.Equal(id, restored.RootElement.GetProperty("participantId").GetGuid());
            Assert.False(restored.RootElement.GetProperty("isCreator").GetBoolean());
            Assert.False(restored.RootElement.TryGetProperty("credential", out _));
        }
        using var owner = await GetMeAsync(client, room.RoomId, room.Credential);
        using var ownerBody = JsonDocument.Parse(await owner.Content.ReadAsStringAsync());
        Assert.True(ownerBody.RootElement.GetProperty("isCreator").GetBoolean());
        await using var verification = database.CreateContext();
        Assert.Equal(2, await verification.Participants.CountAsync());
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(credential)),
            (await verification.Participants.SingleAsync(x => x.Id == id)).CredentialHash);
        var unchanged = await verification.Rooms.SingleAsync();
        Assert.Equal(1, unchanged.Version);
        Assert.Equal(1, unchanged.ItemCount);
        Assert.Equal(4, unchanged.TotalBytes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bad")]
    [InlineData("ROOM!1")]
    [InlineData("ＲＯＯＭ０１")]
    public async Task InvalidCode_Is400WithoutCallingStore(string? code)
    {
        await using var factory = new PastoApiFactory("Host=127.0.0.1;Database=pasto_test", configureServices: services =>
        {
            services.RemoveAll<IRoomJoinStore>();
            services.AddScoped<IRoomJoinStore>(_ => new UnexpectedJoinStore());
        });
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/rooms/join", new { code });
        await AssertProblemAsync(response, 400, "InvalidRequest");
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("closed")]
    [InlineData("expired")]
    public async Task JoinUnavailable_HasSame404AndDoesNotAddParticipant(string state)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        await SetRoomStateAsync(database, room, state);
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/rooms/join", new { code = state == "missing" ? "MISS01" : room.Code });
        await AssertProblemAsync(response, 404, "RoomUnavailable");
        await using var verification = database.CreateContext();
        Assert.Equal(1, await verification.Participants.CountAsync());
        Assert.Equal(1, await verification.CodeReservations.CountAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("scheme")]
    [InlineData("malformed")]
    [InlineData("unknown")]
    [InlineData("queryOnly")]
    public async Task AuthenticationFailure_HasParticipantCodeAndBearerChallenge(string kind)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/rooms/{room.RoomId}/me" + (kind == "queryOnly" ? "?access_token=ignored" : ""));
        if (kind != "missing" && kind != "queryOnly")
            request.Headers.Authorization = new AuthenticationHeaderValue(kind == "scheme" ? "Basic" : "Bearer",
                kind == "malformed" ? "bad" : new string('A', 22));
        using var response = await client.SendAsync(request);
        await AssertProblemAsync(response, 401, "ParticipantUnauthorized");
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ValidForeignCredential_Is401WithoutInvokingRoomRecovery(bool targetExists)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var owner = await CreateRoomAsync(database);
        var target = targetExists ? (await TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM02")).ExecuteAsync()).RoomId : Guid.NewGuid();
        await using var factory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
        {
            services.RemoveAll<IParticipantRecoveryStore>();
            services.AddScoped<IParticipantRecoveryStore>(_ => new UnexpectedRecoveryStore());
        });
        using var client = factory.CreateClient();
        using var response = await GetMeAsync(client, target, owner.Credential);
        await AssertProblemAsync(response, 401, "ParticipantUnauthorized");
    }

    [Theory]
    [InlineData("closed")]
    [InlineData("expired")]
    public async Task ValidSameRoomCredential_StillIdentifiesButMeIs404(string state)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        await SetRoomStateAsync(database, room, state);
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var identity = await scope.ServiceProvider.GetRequiredService<IParticipantVerifier>().VerifyAsync(room.Credential, default);
        Assert.Equal(room.RoomId, identity!.RoomId); // 驗證器不把房間有效性混入身分判斷。
        using var response = await GetMeAsync(client, room.RoomId, room.Credential);
        await AssertProblemAsync(response, 404, "RoomUnavailable");
    }

    [Fact]
    public async Task ReadAdmission_RejectsBeforeVerifierDatabaseWork_AndPublicRoutesSkipVerifier()
    {
        var verifier = new CountingVerifier();
        var settings = LongBuckets("ReadPerIp", "ReadGlobal");
        settings["Api:ReadPerIp:TokenLimit"] = "1";
        await using var factory = new PastoApiFactory("Host=127.0.0.1;Database=pasto_test", settings, services =>
        {
            services.RemoveAll<IParticipantVerifier>();
            services.AddScoped<IParticipantVerifier>(_ => verifier);
        });
        using var client = factory.CreateClient();
        using var first = await GetMeAsync(client, Guid.NewGuid(), new string('A', 22));
        using var second = await GetMeAsync(client, Guid.NewGuid(), new string('B', 22));
        await AssertProblemAsync(first, 401, "ParticipantUnauthorized");
        await AssertProblemAsync(second, 429, "RateLimited");
        Assert.Equal(1, verifier.Calls);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/rooms/join")
        {
            Content = JsonContent.Create(new { code = "bad" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new string('A', 22));
        using var publicResponse = await client.SendAsync(request);
        await AssertProblemAsync(publicResponse, 400, "InvalidRequest");
        Assert.Equal(1, verifier.Calls);
    }

    [Fact]
    public async Task ParticipantReadQuota_IsIndependentForVerifiedIdentities()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var joined = await new JoinRoom(new RoomJoinStore(database.CreateContextFactory()), new RoomSecrets()).ExecuteAsync(room.Code);
        var settings = LongBuckets("ReadPerParticipant");
        settings["Api:ReadPerParticipant:TokenLimit"] = "1";
        await using var factory = new PastoApiFactory(database.ConnectionString, settings);
        using var client = factory.CreateClient();
        using var first = await GetMeAsync(client, room.RoomId, room.Credential);
        using var second = await GetMeAsync(client, room.RoomId, room.Credential);
        using var member = await GetMeAsync(client, room.RoomId, joined.Credential);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await AssertProblemAsync(second, 429, "RateLimited");
        Assert.Equal(HttpStatusCode.OK, member.StatusCode);
    }

    [Fact]
    public async Task JoinQuota_CountsInvalidRequestsBeforeAnyJoinWork()
    {
        var settings = LongBuckets("JoinPerIp", "JoinGlobal");
        settings["Api:JoinPerIp:TokenLimit"] = "1";
        await using var factory = new PastoApiFactory("Host=127.0.0.1;Database=pasto_test", settings);
        using var client = factory.CreateClient();
        using var first = await client.PostAsJsonAsync("/api/rooms/join", new { code = "bad" });
        using var second = await client.PostAsJsonAsync("/api/rooms/join", new { code = "bad" });
        await AssertProblemAsync(first, 400, "InvalidRequest");
        await AssertProblemAsync(second, 429, "RateLimited");
    }

    [Fact]
    public async Task LastParticipantSlot_TwoHttpRequestsOnlyOneCommits_Then1001stIs409()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        // 全部前置 Participant 經正式 JoinRoom；不另寫測試用 INSERT／容量邏輯。
        var prepare = new JoinRoom(new RoomJoinStore(database.CreateContextFactory()), new RoomSecrets());
        for (var index = 0; index < 998; index++) await prepare.ExecuteAsync(room.Code);
        var hold = new HoldAfterSaveInterceptor();
        var attempt = new LockAttemptInterceptor();
        await using var firstFactory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
            services.AddDbContextFactory<PastoDbContext>(options => options.AddInterceptors(hold)));
        await using var secondFactory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
            services.AddDbContextFactory<PastoDbContext>(options => options.AddInterceptors(attempt)));
        using var firstClient = firstFactory.CreateClient();
        using var secondClient = secondFactory.CreateClient();
        var first = firstClient.PostAsJsonAsync("/api/rooms/join", new { code = room.Code });
        Task<HttpResponseMessage>? second = null;
        try
        {
            await hold.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = secondClient.PostAsJsonAsync("/api/rooms/join", new { code = room.Code });
            await WaitForBlockedBackendAsync(database, await attempt.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            hold.Release.TrySetResult();
            await Task.WhenAll(first, (Task?)second ?? Task.CompletedTask);
        }
        using var firstResponse = await first;
        using var secondResponse = await second!;
        Assert.Equal(HttpStatusCode.Created, firstResponse.StatusCode);
        await AssertProblemAsync(secondResponse, 409, "ParticipantLimitReached");
        using var extra = await secondClient.PostAsJsonAsync("/api/rooms/join", new { code = room.Code });
        await AssertProblemAsync(extra, 409, "ParticipantLimitReached");
        await using var verification = database.CreateContext();
        Assert.Equal(1000, await verification.Participants.CountAsync());
        Assert.Equal(0, (await verification.Rooms.SingleAsync()).Version);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("afterCancel")]
    [InlineData("afterDatabase")]
    public async Task JoinFailure_Has503WithoutRetry_PreservesCommitSemantics(string stage)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var failure = new HttpCommitFailureInterceptor(stage);
        await using var factory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
            services.AddDbContextFactory<PastoDbContext>(options => options.AddInterceptors(failure)));
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/rooms/join", new { code = room.Code });
        await AssertProblemAsync(response, 503, "ServiceUnavailable");
        Assert.False(response.Headers.Contains("Retry-After"));
        Assert.Equal(1, failure.Attempts);
        await using var verification = database.CreateContext();
        Assert.Equal(stage == "before" ? 1 : 2, await verification.Participants.CountAsync());
        Assert.Equal(0, (await verification.Rooms.SingleAsync()).Version);
    }

    [Fact]
    public async Task ReadGlobalAdmission_AppliesAcrossIpsBeforeVerifier()
    {
        var verifier = new CountingVerifier();
        var settings = LongBuckets("ReadGlobal");
        settings["Api:ReadGlobal:TokenLimit"] = "1";
        await using var factory = new PastoApiFactory("Host=127.0.0.1;Database=pasto_test", settings, services =>
        {
            services.RemoveAll<IParticipantVerifier>();
            services.AddScoped<IParticipantVerifier>(_ => verifier);
        });
        using var client = factory.CreateClient();
        async Task<int> SendAsync(string ip)
        {
            var response = await factory.Server.SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                context.Request.Method = "GET";
                context.Request.Path = $"/api/rooms/{Guid.NewGuid()}/me";
                context.Request.Headers.Authorization = $"Bearer {new string('A', 22)}";
            });
            Assert.Equal("no-store", response.Response.Headers.CacheControl);
            return response.Response.StatusCode;
        }
        Assert.Equal(401, await SendAsync("198.51.100.1"));
        Assert.Equal(429, await SendAsync("198.51.100.2"));
        Assert.Equal(1, verifier.Calls);
    }

    [Fact]
    public async Task VerifierStorageFailure_Is503RatherThanInvalidCredential()
    {
        await using var factory = new PastoApiFactory("Host=127.0.0.1;Database=pasto_test", configureServices: services =>
        {
            services.RemoveAll<IParticipantVerifier>();
            services.AddScoped<IParticipantVerifier>(_ => new FailedVerifier());
        });
        using var client = factory.CreateClient();
        using var response = await GetMeAsync(client, Guid.NewGuid(), new string('A', 22));
        await AssertProblemAsync(response, 503, "ServiceUnavailable");
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task JoinWaitingForClose_RechecksRoomAndReturns404()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var hold = new HoldAfterSaveInterceptor();
        var attempt = new LockAttemptInterceptor();
        await using var factory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
            services.AddDbContextFactory<PastoDbContext>(options => options.AddInterceptors(attempt)));
        using var client = factory.CreateClient();
        var close = new CloseRoom(new RoomClosureStore(database.CreateContextFactory(hold))).ExecuteAsync(room.RoomId, room.ParticipantId);
        Task<HttpResponseMessage>? join = null;
        try
        {
            await hold.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            join = client.PostAsJsonAsync("/api/rooms/join", new { code = room.Code });
            await WaitForBlockedBackendAsync(database, await attempt.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            hold.Release.TrySetResult();
            await Task.WhenAll(close, (Task?)join ?? Task.CompletedTask);
        }
        using var response = await join!;
        await AssertProblemAsync(response, 404, "RoomUnavailable");
        await using var verification = database.CreateContext();
        Assert.Equal(1, await verification.Participants.CountAsync());
        Assert.Equal(1, (await verification.Rooms.SingleAsync()).Version);
    }

    [Fact]
    public async Task JoinWaitingPastExpiry_UsesPostLockDatabaseTime()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var attempt = new LockAttemptInterceptor();
        await using var factory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
            services.AddDbContextFactory<PastoDbContext>(options => options.AddInterceptors(attempt)));
        using var client = factory.CreateClient();
        await using (var blocker = database.CreateContext())
        {
            await using var transaction = await blocker.Database.BeginTransactionAsync();
            await blocker.Rooms.FromSqlInterpolated($"SELECT * FROM rooms WHERE id = {room.RoomId} FOR UPDATE").ToListAsync();
            var join = client.PostAsJsonAsync("/api/rooms/join", new { code = room.Code });
            var committed = false;
            try
            {
                var pid = await attempt.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await WaitForBlockedBackendAsync(database, pid);
                await using var observer = database.CreateContext();
                var start = await observer.Database.SqlQuery<DateTime>($"SELECT xact_start AS \"Value\" FROM pg_stat_activity WHERE pid = {pid}").SingleAsync();
                await blocker.Database.ExecuteSqlAsync($"UPDATE rooms SET created_at = clock_timestamp() - INTERVAL '5 hours', expires_at = clock_timestamp() WHERE id = {room.RoomId}");
                var expiry = await blocker.Rooms.Select(x => x.ExpiresAt).SingleAsync();
                Assert.True(start < expiry);
                await transaction.CommitAsync();
                committed = true;
            }
            finally
            {
                if (!committed) await transaction.RollbackAsync();
                await join;
            }
            using var response = await join;
            await AssertProblemAsync(response, 404, "RoomUnavailable");
        }
        await using var verification = database.CreateContext();
        Assert.Equal(1, await verification.Participants.CountAsync());
        Assert.Equal(0, (await verification.Rooms.SingleAsync()).Version);
    }

    private static Task<CreatedRoom> CreateRoomAsync(PostgresTestDatabase database)
        => TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM01")).ExecuteAsync();

    private static async Task SetRoomStateAsync(PostgresTestDatabase database, CreatedRoom room, string state)
    {
        if (state == "closed")
            await new CloseRoom(new RoomClosureStore(database.CreateContextFactory())).ExecuteAsync(room.RoomId, room.ParticipantId);
        if (state == "expired")
        {
            await using var context = database.CreateContext();
            await context.Rooms.Where(x => x.Id == room.RoomId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddHours(-6)).SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddHours(-1)));
        }
    }

    private static async Task<HttpResponseMessage> GetMeAsync(HttpClient client, Guid roomId, string credential)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/rooms/{roomId}/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return await client.SendAsync(request);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("correlationId").GetString()));
        if (status == 429) Assert.NotNull(response.Headers.RetryAfter?.Delta);
    }

    private static Dictionary<string, string?> LongBuckets(params string[] names)
    {
        var values = new Dictionary<string, string?>();
        foreach (var name in names)
        {
            values[$"Api:{name}:PeriodSeconds"] = "86400";
            values[$"Api:{name}:TokensPerPeriod"] = "1";
        }
        return values;
    }

    private sealed class UnexpectedJoinStore : IRoomJoinStore
    {
        public Task<(Pasto.Server.Domain.Room Room, Guid ParticipantId)> JoinAsync(string code, byte[] hash, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Invalid code must not reach join storage.");
    }
    private sealed class UnexpectedRecoveryStore : IParticipantRecoveryStore
    {
        public Task<RecoveredParticipant> GetAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Foreign credentials must not query the target room.");
    }
    private sealed class CountingVerifier : IParticipantVerifier
    {
        public int Calls { get; private set; }
        public Task<ParticipantIdentity?> VerifyAsync(string credential, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<ParticipantIdentity?>(null);
        }
    }
    private sealed class FailedVerifier : IParticipantVerifier
    {
        public Task<ParticipantIdentity?> VerifyAsync(string credential, CancellationToken cancellationToken)
            => throw new NpgsqlException("Simulated identity storage outage.");
    }
}
