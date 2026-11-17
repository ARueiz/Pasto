using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
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

public sealed class RoomStateApiTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Metadata_ReturnsOnlyStateLimitsAndDatabaseTime_ForOwnerOrMember(bool owner)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var member = await JoinAsync(database, room);
        const string content = "private clipboard payload";
        await AddAsync(database, room, content);
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        await using var verification = database.CreateContext();
        var before = await verification.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        using var response = await SendAsync(client, HttpMethod.Get, room.RoomId, owner ? room.Credential : member.Credential);
        var after = await verification.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var text = await response.Content.ReadAsStringAsync();
        using var body = JsonDocument.Parse(text);
        var state = body.RootElement;
        Assert.Equal("1", state.GetProperty("version").GetString());
        Assert.Equal(1, state.GetProperty("itemCount").GetInt32());
        Assert.Equal(Encoding.UTF8.GetByteCount(content), state.GetProperty("totalBytes").GetInt64());
        Assert.Equal(room.ExpiresAt, state.GetProperty("expiresAt").GetDateTime());
        Assert.InRange(state.GetProperty("serverTime").GetDateTime(), before, after);
        var limits = state.GetProperty("limits");
        Assert.Equal(8192, limits.GetProperty("maxContentBytes").GetInt32());
        Assert.Equal(300, limits.GetProperty("maxItemCount").GetInt32());
        Assert.Equal(1000, limits.GetProperty("maxParticipantCount").GetInt32());
        Assert.False(state.TryGetProperty("items", out _));
        Assert.DoesNotContain(content, text);
        Assert.DoesNotContain(room.Credential, text);
        Assert.DoesNotContain(member.Credential, text);
        Assert.Equal(2, await verification.Participants.CountAsync());
        Assert.Equal(1, (await verification.Rooms.SingleAsync()).Version);
    }

    [Fact]
    public async Task Metadata_PreservesVersionBeyondJavascriptSafeInteger()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        await using (var setup = database.CreateContext())
            await setup.Rooms.Where(x => x.Id == room.RoomId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, 9007199254740993L));
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var response = await SendAsync(client, HttpMethod.Get, room.RoomId, room.Credential);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("9007199254740993", body.RootElement.GetProperty("version").GetString());
    }

    [Theory]
    [InlineData("GET", "missing")]
    [InlineData("GET", "invalid")]
    [InlineData("GET", "crossExists")]
    [InlineData("GET", "crossMissing")]
    [InlineData("DELETE", "missing")]
    [InlineData("DELETE", "invalid")]
    [InlineData("DELETE", "crossExists")]
    [InlineData("DELETE", "crossMissing")]
    public async Task InvalidOrForeignCredential_Is401BeforeTargetStore(string method, string kind)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var target = kind == "crossExists"
            ? (await TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM02")).ExecuteAsync()).RoomId
            : kind == "crossMissing" ? Guid.NewGuid() : room.RoomId;
        await using var factory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
        {
            services.RemoveAll<IRoomStateStore>();
            services.AddScoped<IRoomStateStore>(_ => new UnexpectedStateStore());
            services.RemoveAll<IRoomClosureStore>();
            services.AddScoped<IRoomClosureStore>(_ => new UnexpectedClosureStore());
        });
        using var client = factory.CreateClient();
        using var response = await SendAsync(client, new HttpMethod(method), target,
            kind == "missing" ? null : kind == "invalid" ? "bad" : room.Credential);
        await AssertProblemAsync(response, 401, "ParticipantUnauthorized");
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
    }

    [Theory]
    [InlineData("GET", "closed", false)]
    [InlineData("GET", "closed", true)]
    [InlineData("GET", "expired", false)]
    [InlineData("GET", "expired", true)]
    [InlineData("DELETE", "closed", false)]
    [InlineData("DELETE", "closed", true)]
    [InlineData("DELETE", "expired", false)]
    [InlineData("DELETE", "expired", true)]
    public async Task UnavailableRoom_Is404ForOwnerAndMemberWithoutFurtherChanges(string method, string state, bool owner)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var member = await JoinAsync(database, room);
        await AddAsync(database, room, "text");
        if (state == "closed")
            await new CloseRoom(new RoomClosureStore(database.CreateContextFactory())).ExecuteAsync(room.RoomId, room.ParticipantId);
        else
        {
            await using var setup = database.CreateContext();
            await setup.Rooms.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddHours(-6))
                .SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddHours(-1)));
        }
        await using var verification = database.CreateContext();
        var before = await verification.Rooms.AsNoTracking().SingleAsync();
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var response = await SendAsync(client, new HttpMethod(method), room.RoomId, owner ? room.Credential : member.Credential);
        await AssertProblemAsync(response, 404, "RoomUnavailable");
        var after = await verification.Rooms.AsNoTracking().SingleAsync();
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.DeletedAt, after.DeletedAt);
        Assert.Equal(1, after.ItemCount);
        Assert.Equal(4, after.TotalBytes);
        Assert.Equal(1, await verification.Items.CountAsync());
    }

    [Fact]
    public async Task CreatorClose_Is204PreservesDataAndRejectsFurtherAccess()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var member = await JoinAsync(database, room);
        await AddAsync(database, room, "text");
        var other = await TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM02")).ExecuteAsync();
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var forbidden = await SendAsync(client, HttpMethod.Delete, room.RoomId, member.Credential);
        await AssertProblemAsync(forbidden, 403, "RoomDeleteForbidden");
        using var closed = await SendAsync(client, HttpMethod.Delete, room.RoomId, room.Credential);
        Assert.Equal(HttpStatusCode.NoContent, closed.StatusCode);
        Assert.Empty(await closed.Content.ReadAsByteArrayAsync());
        Assert.True(closed.Headers.CacheControl?.NoStore);
        foreach (var credential in new[] { room.Credential, member.Credential })
        {
            using var read = await SendAsync(client, HttpMethod.Get, room.RoomId, credential);
            using var repeat = await SendAsync(client, HttpMethod.Delete, room.RoomId, credential);
            using var me = await SendAsync(client, HttpMethod.Get, room.RoomId, credential, "/me");
            await AssertProblemAsync(read, 404, "RoomUnavailable");
            await AssertProblemAsync(repeat, 404, "RoomUnavailable");
            await AssertProblemAsync(me, 404, "RoomUnavailable");
        }
        using var join = await client.PostAsJsonAsync("/api/rooms/join", new { code = room.Code });
        await AssertProblemAsync(join, 404, "RoomUnavailable");
        using var untouched = await SendAsync(client, HttpMethod.Get, other.RoomId, other.Credential);
        Assert.Equal(HttpStatusCode.OK, untouched.StatusCode);
        await using var verification = database.CreateContext();
        var persisted = await verification.Rooms.SingleAsync(x => x.Id == room.RoomId);
        Assert.NotNull(persisted.DeletedAt);
        Assert.Equal(2, persisted.Version);
        Assert.Equal(room.ExpiresAt, persisted.ExpiresAt);
        Assert.Equal(1, persisted.ItemCount);
        Assert.Equal(4, persisted.TotalBytes);
        Assert.Equal(3, await verification.Participants.CountAsync());
        Assert.Equal(1, await verification.Items.CountAsync());
        Assert.Equal(room.ExpiresAt.AddHours(24), (await verification.CodeReservations.SingleAsync(x => x.Code == room.Code)).ReusableAfter);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MeAndMetadata_ShareParticipantQuotaButDoNotPreventCreatorClose(bool metadataFirst)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var settings = new Dictionary<string, string?>
        {
            ["Api:ReadPerParticipant:TokenLimit"] = "1",
            ["Api:ReadPerParticipant:TokensPerPeriod"] = "1",
            ["Api:ReadPerParticipant:PeriodSeconds"] = "86400"
        };
        await using var factory = new PastoApiFactory(database.ConnectionString, settings);
        using var client = factory.CreateClient();
        using var first = await SendAsync(client, HttpMethod.Get, room.RoomId, room.Credential, metadataFirst ? "" : "/me");
        using var second = await SendAsync(client, HttpMethod.Get, room.RoomId, room.Credential, metadataFirst ? "/me" : "");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await AssertProblemAsync(second, 429, "RateLimited");
        Assert.NotNull(second.Headers.RetryAfter?.Delta);
        using var close = await SendAsync(client, HttpMethod.Delete, room.RoomId, room.Credential);
        Assert.Equal(HttpStatusCode.NoContent, close.StatusCode);
    }

    [Theory]
    [InlineData("GET", false)]
    [InlineData("GET", true)]
    [InlineData("DELETE", false)]
    [InlineData("DELETE", true)]
    public async Task ProtectedRoutes_ShareReadAdmissionBeforeCredentialLookup(string method, bool global)
    {
        var verifier = new CountingVerifier();
        var prefix = global ? "ReadGlobal" : "ReadPerIp";
        var settings = new Dictionary<string, string?>
        {
            [$"Api:{prefix}:TokenLimit"] = "1",
            [$"Api:{prefix}:TokensPerPeriod"] = "1",
            [$"Api:{prefix}:PeriodSeconds"] = "86400"
        };
        await using var factory = new PastoApiFactory("Host=127.0.0.1;Database=pasto_test", settings, services =>
        {
            services.RemoveAll<IParticipantVerifier>();
            services.AddScoped<IParticipantVerifier>(_ => verifier);
        });
        using var client = factory.CreateClient();
        async Task<int> SendFromAsync(string path, string verb, string ip)
        {
            var response = await factory.Server.SendAsync(context =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
                context.Request.Method = verb;
                context.Request.Path = path;
                context.Request.Headers.Authorization = $"Bearer {new string('A', 22)}";
            });
            Assert.Equal("no-store", response.Response.Headers.CacheControl);
            if (response.Response.StatusCode == 429) Assert.False(string.IsNullOrWhiteSpace(response.Response.Headers.RetryAfter));
            return response.Response.StatusCode;
        }
        var id = Guid.NewGuid();
        Assert.Equal(401, await SendFromAsync($"/api/rooms/{id}/me", "GET", "198.51.100.1"));
        Assert.Equal(429, await SendFromAsync($"/api/rooms/{id}", method, global ? "198.51.100.2" : "198.51.100.1"));
        Assert.Equal(1, verifier.Calls);
    }

    [Fact]
    public async Task Metadata_SeesCommittedStateWhileWriterHoldsUncommittedChanges()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        await AddAsync(database, room, "text");
        var hold = new HoldAfterSaveInterceptor();
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        var writer = new AddItem(new ItemAdditionStore(database.CreateContextFactory(hold)))
            .ExecuteAsync(room.RoomId, room.ParticipantId, "new");
        try
        {
            await hold.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var response = await SendAsync(client, HttpMethod.Get, room.RoomId, room.Credential).WaitAsync(TimeSpan.FromSeconds(5));
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("1", body.RootElement.GetProperty("version").GetString());
            Assert.Equal(1, body.RootElement.GetProperty("itemCount").GetInt32());
            Assert.Equal(4, body.RootElement.GetProperty("totalBytes").GetInt64());
        }
        finally
        {
            hold.Release.TrySetResult();
            await writer;
        }
        using var latest = await SendAsync(client, HttpMethod.Get, room.RoomId, room.Credential);
        using var updated = JsonDocument.Parse(await latest.Content.ReadAsStringAsync());
        Assert.Equal("2", updated.RootElement.GetProperty("version").GetString());
        Assert.Equal(2, updated.RootElement.GetProperty("itemCount").GetInt32());
        Assert.Equal(7, updated.RootElement.GetProperty("totalBytes").GetInt64());
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task RoomDeletedAfterVerification_Is404FromUseCase(string method)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var gate = new PausedVerifier(new ParticipantVerifier(database.CreateContextFactory()));
        await using var factory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
        {
            services.RemoveAll<IParticipantVerifier>();
            services.AddScoped<IParticipantVerifier>(_ => gate);
        });
        using var client = factory.CreateClient();
        var pending = SendAsync(client, new HttpMethod(method), room.RoomId, room.Credential);
        try
        {
            await gate.Verified.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await using var cleanup = database.CreateContext();
            // 模擬驗證與 use case 之間的實體清理；維持正式 FK／cascade，不重寫關房邏輯。
            await cleanup.Rooms.Where(x => x.Id == room.RoomId).ExecuteDeleteAsync();
        }
        finally
        {
            gate.Release.TrySetResult();
            await pending;
        }
        using var response = await pending;
        await AssertProblemAsync(response, 404, "RoomUnavailable");
    }

    [Fact]
    public async Task ConcurrentHttpClose_OnlyOne204AndOneVersionIncrement()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var hold = new HoldAfterSaveInterceptor();
        var attempt = new LockAttemptInterceptor();
        await using var firstFactory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
            services.AddDbContextFactory<PastoDbContext>(options => options.AddInterceptors(hold)));
        await using var secondFactory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
            services.AddDbContextFactory<PastoDbContext>(options => options.AddInterceptors(attempt)));
        using var a = firstFactory.CreateClient();
        using var b = secondFactory.CreateClient();
        var first = SendAsync(a, HttpMethod.Delete, room.RoomId, room.Credential);
        Task<HttpResponseMessage>? second = null;
        try
        {
            await hold.Saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = SendAsync(b, HttpMethod.Delete, room.RoomId, room.Credential);
            await WaitForBlockedBackendAsync(database, await attempt.Attempted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            hold.Release.TrySetResult();
            await Task.WhenAll(first, (Task?)second ?? Task.CompletedTask);
        }
        using var accepted = await first;
        using var rejected = await second!;
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
        await AssertProblemAsync(rejected, 404, "RoomUnavailable");
        await using var verification = database.CreateContext();
        Assert.Equal(1, (await verification.Rooms.SingleAsync()).Version);
    }

    [Theory]
    [InlineData("before")]
    [InlineData("afterCancel")]
    [InlineData("afterDatabase")]
    public async Task HttpCloseFailure_UsesExistingCommitSemantics(string stage)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateRoomAsync(database);
        var failure = new HttpCommitFailureInterceptor(stage);
        await using var factory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
            services.AddDbContextFactory<PastoDbContext>(options => options.AddInterceptors(failure)));
        using var client = factory.CreateClient();
        using var response = await SendAsync(client, HttpMethod.Delete, room.RoomId, room.Credential);
        await AssertProblemAsync(response, 503, "ServiceUnavailable");
        Assert.False(response.Headers.Contains("Retry-After"));
        Assert.Equal(1, failure.Attempts);
        await using var verification = database.CreateContext();
        var persisted = await verification.Rooms.SingleAsync();
        Assert.Equal(stage == "before" ? 0 : 1, persisted.Version);
        Assert.Equal(stage != "before", persisted.DeletedAt is not null);
    }

    private static Task<CreatedRoom> CreateRoomAsync(PostgresTestDatabase database)
        => TestRoomCreation.Create(database, new SequenceRoomSecrets("ROOM01")).ExecuteAsync();
    private static Task<JoinedRoom> JoinAsync(PostgresTestDatabase database, CreatedRoom room)
        => new JoinRoom(new RoomJoinStore(database.CreateContextFactory()), new RoomSecrets()).ExecuteAsync(room.Code);
    private static Task<AddedItem> AddAsync(PostgresTestDatabase database, CreatedRoom room, string text)
        => new AddItem(new ItemAdditionStore(database.CreateContextFactory())).ExecuteAsync(room.RoomId, room.ParticipantId, text);
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, Guid roomId, string? credential, string suffix = "")
    {
        using var request = new HttpRequestMessage(method, $"/api/rooms/{roomId}{suffix}");
        if (credential is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return await client.SendAsync(request);
    }
    private static async Task AssertProblemAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("correlationId").GetString()));
    }
    private sealed class UnexpectedStateStore : IRoomStateStore
    {
        public Task<RoomState> GetAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Unauthenticated request must not query room state.");
    }
    private sealed class UnexpectedClosureStore : IRoomClosureStore
    {
        public Task<ClosedRoom> CloseAsync(Guid roomId, Guid participantId, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Unauthenticated request must not invoke room closure.");
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
    private sealed class PausedVerifier(IParticipantVerifier actual) : IParticipantVerifier
    {
        public TaskCompletionSource Verified { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ParticipantIdentity?> VerifyAsync(string credential, CancellationToken cancellationToken)
        {
            var identity = await actual.VerifyAsync(credential, cancellationToken);
            Assert.NotNull(identity);
            Verified.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            return identity;
        }
    }
}
