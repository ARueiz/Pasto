using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pasto.Server.Application.Items;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Persistence;
using Pasto.Server.Infrastructure.Security;
using Xunit;
using static Pasto.IntegrationTests.TransactionTestHooks;

namespace Pasto.IntegrationTests;

public sealed class ItemWriteApiTests
{
    [Theory]
    [InlineData("  中文\n😀\r\n ")]
    [InlineData("boundary")]
    public async Task AddAndDelete_PreserveContentAndUpdateStatistics(string content)
    {
        if (content == "boundary") content = new string('中', 2730) + "ab";
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateAsync(database);
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var added = await SendAsync(client, HttpMethod.Post, room, "/items", JsonSerializer.Serialize(new { content }));
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        Assert.True(added.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await added.Content.ReadAsStringAsync());
        var item = json.RootElement.GetProperty("item");
        var id = item.GetProperty("id").GetGuid();
        Assert.Equal(content, item.GetProperty("content").GetString());
        Assert.Equal("1", item.GetProperty("createdVersion").GetString());
        Assert.Equal("1", json.RootElement.GetProperty("committedVersion").GetString());
        Assert.True(item.GetProperty("isOwn").GetBoolean());
        Assert.True(item.GetProperty("canDelete").GetBoolean());
        await using (var context = database.CreateContext())
        {
            var stored = await context.Items.SingleAsync();
            Assert.Equal(room.ParticipantId, stored.OwnerParticipantId);
            Assert.Equal(content, stored.Content);
            Assert.Equal(stored.CreatedAt, item.GetProperty("createdAt").GetDateTime());
            var state = await context.Rooms.SingleAsync();
            Assert.Equal(Encoding.UTF8.GetByteCount(content), state.TotalBytes);
            Assert.Equal(1, state.ItemCount);
        }
        using var deleted = await SendAsync(client, HttpMethod.Delete, room, $"/items/{id}");
        using var repeated = await SendAsync(client, HttpMethod.Delete, room, $"/items/{id}");
        foreach (var (response, changed) in new[] { (deleted, true), (repeated, false) })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoStore);
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(changed, result.RootElement.GetProperty("changed").GetBoolean());
            Assert.Equal("2", result.RootElement.GetProperty("committedVersion").GetString());
        }
        await using var verification = database.CreateContext();
        Assert.Empty(await verification.Items.ToListAsync());
        var final = await verification.Rooms.SingleAsync();
        Assert.Equal(0, final.ItemCount);
        Assert.Equal(0, final.TotalBytes);
        Assert.Equal(2, final.Version);
    }

    [Theory]
    [InlineData("{}", "EmptyContent")]
    [InlineData("{\"content\":null}", "EmptyContent")]
    [InlineData("{\"content\":\" \\n\\t\"}", "EmptyContent")]
    [InlineData("{\"content\":\"a\\u0000b\"}", "InvalidRequest")]
    [InlineData("{\"content\":\"\\uD800\"}", "InvalidRequest")]
    [InlineData("{\"content\":42}", "InvalidRequest")]
    [InlineData("{\"content\":\"ok\",\"ownerParticipantId\":\"attacker\"}", "InvalidRequest")]
    [InlineData("{\"content\":\"ok\",\"isCreator\":true}", "InvalidRequest")]
    [InlineData("{", "InvalidRequest")]
    [InlineData("tooLarge", "TextTooLarge")]
    public async Task InvalidContent_IsRejectedWithoutWrites(string payload, string code)
    {
        if (payload == "tooLarge") payload = JsonSerializer.Serialize(new { content = new string('中', 2731) });
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateAsync(database);
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var response = await SendAsync(client, HttpMethod.Post, room, "/items", payload);
        await ProblemAsync(response, 400, code);
        await using var context = database.CreateContext();
        Assert.Empty(await context.Items.ToListAsync());
        Assert.Equal(0, (await context.Rooms.SingleAsync()).Version);
    }

    [Theory]
    [InlineData("POST", "missing")]
    [InlineData("POST", "invalid")]
    [InlineData("POST", "cross")]
    [InlineData("DELETE", "missing")]
    [InlineData("DELETE", "invalid")]
    [InlineData("DELETE", "cross")]
    public async Task IdentityFailures_DoNotWrite(string method, string kind)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateAsync(database);
        var other = await CreateAsync(database);
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        var credential = kind == "missing" ? null : kind == "cross" ? other.Credential : new string('A', 22);
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/rooms/{room.RoomId}/items" +
            (method == "DELETE" ? $"/{Guid.NewGuid()}" : ""));
        if (credential is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        if (method == "POST") request.Content = new StringContent("{\"content\":\"ok\"}", Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        await ProblemAsync(response, kind == "cross" ? 403 : 401, "ParticipantUnauthorized");
        Assert.Equal(kind != "cross", response.Headers.WwwAuthenticate.Any(x => x.Scheme == "Bearer"));
        await using var context = database.CreateContext();
        Assert.Empty(await context.Items.ToListAsync());
        Assert.All(await context.Rooms.ToListAsync(), x => Assert.Equal(0, x.Version));
    }

    [Fact]
    public async Task CreatorCannotDeleteMemberItem_AndOtherRoomItemIsAbsent()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateAsync(database);
        var other = await CreateAsync(database);
        var member = await new JoinRoom(new RoomJoinStore(database.CreateContextFactory()), new RoomSecrets()).ExecuteAsync(room.Code);
        var item = await new AddItem(new ItemAdditionStore(database.CreateContextFactory())).ExecuteAsync(room.RoomId, member.ParticipantId, "member content");
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var forbidden = await SendAsync(client, HttpMethod.Delete, room, $"/items/{item.ItemId}");
        await ProblemAsync(forbidden, 403, "ItemDeleteForbidden");
        using var absent = await SendAsync(client, HttpMethod.Delete, other, $"/items/{item.ItemId}");
        Assert.Equal(HttpStatusCode.OK, absent.StatusCode);
        using var json = JsonDocument.Parse(await absent.Content.ReadAsStringAsync());
        Assert.False(json.RootElement.GetProperty("changed").GetBoolean());
        Assert.Equal("0", json.RootElement.GetProperty("committedVersion").GetString());
        await using var context = database.CreateContext();
        Assert.Single(await context.Items.ToListAsync());
    }

    [Theory]
    [InlineData("POST", false)]
    [InlineData("DELETE", false)]
    [InlineData("POST", true)]
    [InlineData("DELETE", true)]
    public async Task UnavailableRoom_ReturnsContract404(string method, bool expired)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateAsync(database);
        await using (var context = database.CreateContext())
        {
            if (expired) await context.Rooms.Where(x => x.Id == room.RoomId).ExecuteUpdateAsync(set =>
                set.SetProperty(x => x.CreatedAt, DateTime.UtcNow.AddHours(-6)).SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddHours(-1)));
            else await new CloseRoom(new RoomClosureStore(database.CreateContextFactory())).ExecuteAsync(room.RoomId, room.ParticipantId);
        }
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var response = await SendAsync(client, new HttpMethod(method), room,
            method == "POST" ? "/items" : $"/items/{Guid.NewGuid()}", method == "POST" ? "{\"content\":\"ok\"}" : null);
        await ProblemAsync(response, 404, "RoomUnavailable");
    }

    [Fact]
    public async Task ParticipantBuckets_AreSeparate_AndDeletionIsSharedWithClose()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateAsync(database);
        var member = await new JoinRoom(new RoomJoinStore(database.CreateContextFactory()), new RoomSecrets()).ExecuteAsync(room.Code);
        await using var factory = new PastoApiFactory(database.ConnectionString, SmallBuckets());
        using var client = factory.CreateClient();
        using var first = await SendAsync(client, HttpMethod.Post, room, "/items", "{\"content\":\"ok\"}");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var limited = await SendAsync(client, HttpMethod.Post, room, "/items", "{\"content\":\"again\"}");
        await ProblemAsync(limited, 429, "RateLimited");
        using var read = await SendAsync(client, HttpMethod.Get, room, "");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        using var deleted = await SendAsync(client, HttpMethod.Delete, room, $"/items/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        using var close = await SendAsync(client, HttpMethod.Delete, room, "");
        await ProblemAsync(close, 429, "RateLimited");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/rooms/{room.RoomId}/items");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", member.Credential);
        request.Content = new StringContent("{\"content\":\"member\"}", Encoding.UTF8, "application/json");
        using var memberResponse = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, memberResponse.StatusCode);
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("DELETE")]
    public async Task CoarseLimit_RejectsBeforeVerifier(string method)
    {
        var verifier = new CountingVerifier();
        var settings = new Dictionary<string, string?> { ["Api:ReadPerIp:TokenLimit"] = "1", ["Api:ReadPerIp:PeriodSeconds"] = "3600" };
        await using var factory = new PastoApiFactory("Host=127.0.0.1;Database=pasto_test", settings, services =>
        {
            services.RemoveAll<IParticipantVerifier>();
            services.AddScoped<IParticipantVerifier>(_ => verifier);
        });
        using var client = factory.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/rooms/{Guid.NewGuid()}/items" +
                (method == "DELETE" ? $"/{Guid.NewGuid()}" : ""));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", new string(i == 0 ? 'A' : 'B', 22));
            if (method == "POST") request.Content = new StringContent("{\"content\":\"ok\"}", Encoding.UTF8, "application/json");
            using var response = await client.SendAsync(request);
            await ProblemAsync(response, i == 0 ? 401 : 429, i == 0 ? "ParticipantUnauthorized" : "RateLimited");
        }
        Assert.Equal(1, verifier.Calls);
    }

    [Theory]
    [InlineData("POST", "before")]
    [InlineData("POST", "afterCancel")]
    [InlineData("POST", "afterDatabase")]
    [InlineData("DELETE", "before")]
    [InlineData("DELETE", "afterCancel")]
    [InlineData("DELETE", "afterDatabase")]
    public async Task CommitFailure_DoesNotRetryOrMisrepresentRollback(string method, string stage)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateAsync(database);
        var item = method == "DELETE" ? await new AddItem(new ItemAdditionStore(database.CreateContextFactory()))
            .ExecuteAsync(room.RoomId, room.ParticipantId, "initial") : null;
        var hook = new HttpCommitFailureInterceptor(stage);
        await using var factory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
            services.AddDbContextFactory<PastoDbContext>(options => options.AddInterceptors(hook)));
        using var client = factory.CreateClient();
        using var response = await SendAsync(client, new HttpMethod(method), room,
            item is null ? "/items" : $"/items/{item.ItemId}", method == "POST" ? "{\"content\":\"new\"}" : null);
        await ProblemAsync(response, 503, "ServiceUnavailable");
        Assert.Null(response.Headers.RetryAfter);
        Assert.Equal(1, hook.Attempts);
        await using var context = database.CreateContext();
        var changed = stage != "before";
        var expected = method == "POST" ? (changed ? 1 : 0) : (changed ? 0 : 1);
        Assert.Equal(expected, await context.Items.CountAsync());
        var state = await context.Rooms.SingleAsync();
        Assert.Equal(expected, state.ItemCount);
        Assert.Equal((method == "POST" ? 0 : 1) + (changed ? 1 : 0), state.Version);
    }

    [Fact]
    public async Task FullRoom_Returns409_WithoutChangingCounters()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateAsync(database);
        var add = new AddItem(new ItemAdditionStore(database.CreateContextFactory()));
        for (var i = 0; i < 300; i++) await add.ExecuteAsync(room.RoomId, room.ParticipantId, "x");
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var response = await SendAsync(client, HttpMethod.Post, room, "/items", "{\"content\":\"overflow\"}");
        await ProblemAsync(response, 409, "RoomItemLimitReached");
        await using var context = database.CreateContext();
        Assert.Equal(300, await context.Items.CountAsync());
        var state = await context.Rooms.SingleAsync();
        Assert.Equal(300, state.ItemCount);
        Assert.Equal(300, state.TotalBytes);
        Assert.Equal(300, state.Version);
    }

    [Theory]
    [InlineData(32768, false, 400, "EmptyContent")]
    [InlineData(32769, false, 413, "RequestBodyTooLarge")]
    [InlineData(0, true, 413, "RequestBodyTooLarge")]
    public async Task RealKestrel_ItemBodyLimit_IsSeparateFromContentLimit(int bytes, bool escaped, int status, string code)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateAsync(database);
        await using var factory = new PastoApiFactory(database.ConnectionString);
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        // 6000 個 U+0001 原文僅 6000 UTF-8 bytes，JSON 跳脫後超過 32 KiB。
        var payload = escaped ? "{\"content\":\"" + string.Concat(Enumerable.Repeat("\\u0001", 6000)) + "\"}"
            : "{}" + new string(' ', bytes - 2);
        using var response = await SendAsync(client, HttpMethod.Post, room, "/items", payload);
        await ProblemAsync(response, status, code);
        await using var context = database.CreateContext();
        Assert.Empty(await context.Items.ToListAsync());
        Assert.Equal(0, (await context.Rooms.SingleAsync()).Version);
    }

    [Theory]
    [InlineData("Api:AddPerParticipant:TokenLimit")]
    [InlineData("Api:DeletePerParticipant:TokensPerPeriod")]
    public void WriteQuota_InvalidConfiguration_FailsAtStartup(string key)
    {
        using var factory = new PastoApiFactory("Host=127.0.0.1;Database=pasto_test",
            new Dictionary<string, string?> { [key] = "0" });
        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => factory.CreateClient());
    }

    private static Dictionary<string, string?> SmallBuckets() => new()
    {
        ["Api:AddPerParticipant:TokenLimit"] = "1",
        ["Api:AddPerParticipant:PeriodSeconds"] = "3600",
        ["Api:DeletePerParticipant:TokenLimit"] = "1",
        ["Api:DeletePerParticipant:PeriodSeconds"] = "3600"
    };
    private static Task<CreatedRoom> CreateAsync(PostgresTestDatabase database)
        => TestRoomCreation.Create(database, new RoomSecrets()).ExecuteAsync();
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, CreatedRoom room, string suffix, string? json = null)
    {
        using var request = new HttpRequestMessage(method, $"/api/rooms/{room.RoomId}{suffix}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", room.Credential);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }
    private static async Task ProblemAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("correlationId").GetString()));
        if (status == 429) Assert.NotNull(response.Headers.RetryAfter);
    }
    private sealed class CountingVerifier : IParticipantVerifier
    {
        public int Calls { get; private set; }
        public Task<ParticipantIdentity?> VerifyAsync(byte[] credentialHash, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<ParticipantIdentity?>(null);
        }
    }
}
