using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Pasto.Server.Application.Items;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Persistence;
using Pasto.Server.Infrastructure.Security;
using Xunit;
using Xunit.Abstractions;

namespace Pasto.IntegrationTests;

public sealed class ItemSnapshotApiTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AuthorCode_UsesCanonicalUuidByteOrder()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var roomId = Guid.Parse("00112233-4455-4677-8899-aabbccddeeff");
        var participantId = Guid.Parse("fedcba98-7654-4c32-90ab-cdef10203040");
        var credential = new RoomSecrets().GenerateCredential();
        var now = DateTime.UtcNow;
        var expires = now.AddHours(1);
        await using (var setup = database.CreateContext())
        {
            await using var transaction = await setup.Database.BeginTransactionAsync();
            await setup.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO room_code_reservations (code, reusable_after) VALUES ('FIX001', {expires.AddHours(24)});
                INSERT INTO rooms (id, code, creator_participant_id, created_at, expires_at)
                VALUES ({roomId}, 'FIX001', {participantId}, {now}, {expires});
                INSERT INTO room_participants (id, room_id, credential_hash, created_at)
                VALUES ({participantId}, {roomId}, {credential.Hash}, {now});
                """);
            await transaction.CommitAsync();
        }
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var response = await ReadAsync(client, roomId, credential.Value, "/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var participant = await response.Content.ReadFromJsonAsync<JsonElement>();
        // 此向量用 Node.js SHA-256 對去除連字號後的 32 個十六進位位元組獨立計算。
        Assert.Equal("0653C", participant.GetProperty("authorCode").GetString());
    }

    [Fact]
    public async Task AuthorCode_MatchesIdentityAndSnapshotsForOwnerAndMember()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var create = await client.PostAsync("/api/rooms", null);
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var owner = await create.Content.ReadFromJsonAsync<JsonElement>();
        var roomId = owner.GetProperty("roomId").GetGuid();
        using var join = await client.PostAsJsonAsync("/api/rooms/join", new { code = owner.GetProperty("code").GetString() });
        Assert.Equal(HttpStatusCode.Created, join.StatusCode);
        var member = await join.Content.ReadFromJsonAsync<JsonElement>();
        var participants = new[] { (Identity: owner, IsCreator: true), (Identity: member, IsCreator: false) };
        var authors = new Dictionary<Guid, (string? Code, bool IsCreator)>();
        var add = CreateAdd(database);
        foreach (var (identity, isCreator) in participants)
        {
            var code = identity.GetProperty("authorCode").GetString();
            Assert.Matches("^[0-9A-F]{5}$", code!);
            using var response = await ReadAsync(client, roomId, identity.GetProperty("credential").GetString(), "/me");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var recovered = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(code, recovered.GetProperty("authorCode").GetString());
            Assert.Equal(isCreator, recovered.GetProperty("isCreator").GetBoolean());
            var added = await add.ExecuteAsync(roomId, identity.GetProperty("participantId").GetGuid(), "author test");
            authors.Add(added.ItemId, (code, isCreator));
        }
        foreach (var (identity, _) in participants)
        {
            using var response = await ReadAsync(client, roomId, identity.GetProperty("credential").GetString());
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var snapshot = await response.Content.ReadFromJsonAsync<JsonElement>();
            var items = snapshot.GetProperty("items");
            Assert.Equal(authors.Count, items.GetArrayLength());
            foreach (var item in items.EnumerateArray())
            {
                var expected = authors[item.GetProperty("id").GetGuid()];
                Assert.Equal(expected.Code, item.GetProperty("authorCode").GetString());
                Assert.Equal(expected.IsCreator, item.GetProperty("authorIsCreator").GetBoolean());
                Assert.False(item.TryGetProperty("ownerParticipantId", out _));
                Assert.False(item.TryGetProperty("authorColor", out _));
            }
        }
    }

    [Fact]
    public async Task EmptyRoom_ReturnsAnEmptySnapshot()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateAsync(database);
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var response = await ReadAsync(client, room.RoomId, room.Credential);
        using var snapshot = await ParseSnapshotAsync(response, room, "0", 0);
    }

    [Fact]
    public async Task Snapshot_SortsTiesPreservesLargeVersionsAndUsesOnlyItemOwnership()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateAsync(database);
        var other = await CreateAsync(database, "ROOM02");
        var member = await new JoinRoom(new RoomJoinStore(database.CreateContextFactory()), new RoomSecrets()).ExecuteAsync(room.Code);
        var add = CreateAdd(database);
        await add.ExecuteAsync(other.RoomId, other.ParticipantId, "another room");
        var ownerItem = await add.ExecuteAsync(room.RoomId, room.ParticipantId, "  owner\n");
        var memberItem = await add.ExecuteAsync(room.RoomId, member.ParticipantId, "member");
        const long highVersion = 9007199254740993L;
        await using (var setup = database.CreateContext())
        {
            // Production writes allocate distinct versions. Seed a tie to verify the secondary ordering.
            await setup.Items.Where(x => x.RoomId == room.RoomId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedVersion, highVersion));
            await setup.Rooms.Where(x => x.Id == room.RoomId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Version, highVersion));
        }
        var newest = await add.ExecuteAsync(room.RoomId, room.ParticipantId, "newest");
        var expectedOrder = new[] { newest.ItemId }.Concat(new[] { ownerItem.ItemId, memberItem.ItemId }.OrderDescending());
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        foreach (var (credential, isOwner) in new[] { (room.Credential, true), (member.Credential, false) })
        {
            using var response = await ReadAsync(client, room.RoomId, credential);
            using var snapshot = await ParseSnapshotAsync(response, room, "9007199254740994", 3);
            var items = snapshot.RootElement.GetProperty("items").EnumerateArray().ToArray();
            Assert.Equal(expectedOrder, items.Select(item => item.GetProperty("id").GetGuid()));
            foreach (var item in items)
            {
                var id = item.GetProperty("id").GetGuid();
                var owned = isOwner == (id != memberItem.ItemId);
                Assert.Equal(owned, item.GetProperty("isOwn").GetBoolean());
                Assert.Equal(owned, item.GetProperty("canDelete").GetBoolean());
                Assert.Equal(id == newest.ItemId ? "9007199254740994" : "9007199254740993",
                    item.GetProperty("createdVersion").GetString());
            }
            var saved = items.Single(item => item.GetProperty("id").GetGuid() == ownerItem.ItemId);
            Assert.Equal("  owner\n", saved.GetProperty("content").GetString());
            Assert.Equal(ownerItem.CreatedAt, saved.GetProperty("createdAt").GetDateTime());
        }
        using var foreign = await ReadAsync(client, other.RoomId, room.Credential);
        await AssertProblemAsync(foreign, 403, "ParticipantUnauthorized");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InactiveRoom_RejectsAuthorizedRead(bool close)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateAsync(database);
        await CreateAdd(database).ExecuteAsync(room.RoomId, room.ParticipantId, "private");
        if (close)
            await new CloseRoom(new RoomClosureStore(database.CreateContextFactory())).ExecuteAsync(room.RoomId, room.ParticipantId);
        else
        {
            await using var setup = database.CreateContext();
            var now = DateTime.UtcNow;
            await setup.Rooms.ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, now.AddHours(-6))
                .SetProperty(x => x.ExpiresAt, now.AddHours(-1)));
        }
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        using var response = await ReadAsync(client, room.RoomId, room.Credential);
        await AssertProblemAsync(response, 404, "RoomUnavailable");
        using var anonymous = await ReadAsync(client, room.RoomId, null);
        await AssertProblemAsync(anonymous, 401, "ParticipantUnauthorized");
    }

    [Theory]
    [InlineData("ReadPerParticipant", "/me", true)]
    [InlineData("ReadPerIp", "", false)]
    [InlineData("ReadGlobal", "", false)]
    public async Task Snapshot_SharesReadQuota(string bucket, string suffix, bool authenticated)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateAsync(database);
        var settings = new Dictionary<string, string?>
        {
            [$"Api:{bucket}:TokenLimit"] = "1",
            [$"Api:{bucket}:TokensPerPeriod"] = "1",
            [$"Api:{bucket}:PeriodSeconds"] = "86400"
        };
        await using var factory = new PastoApiFactory(database.ConnectionString, settings);
        using var client = factory.CreateClient();
        using var first = await ReadAsync(client, room.RoomId, authenticated ? room.Credential : new string('A', 22), suffix);
        Assert.Equal(authenticated ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, first.StatusCode);
        using var second = await ReadAsync(client, room.RoomId, authenticated ? room.Credential : new string('B', 22));
        await AssertProblemAsync(second, 429, "RateLimited");
        Assert.NotNull(second.Headers.RetryAfter?.Delta);
    }

    [Fact]
    public async Task ConcurrentCommitsBetweenReads_DoNotMixRoomAndItemSnapshots()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateAsync(database);
        var add = CreateAdd(database);
        var original = await add.ExecuteAsync(room.RoomId, room.ParticipantId, "original");
        var pause = new PauseBeforeItems();
        await using var factory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
            services.AddDbContextFactory<PastoDbContext>(options => options.AddInterceptors(pause)));
        using var client = factory.CreateClient();
        var pending = ReadAsync(client, room.RoomId, room.Credential);
        AddedItem added;
        try
        {
            await pause.Reached.Task.WaitAsync(TimeSpan.FromSeconds(8));
            added = await add.ExecuteAsync(room.RoomId, room.ParticipantId, "new");
            await new DeleteOwnItem(new ItemDeletionStore(database.CreateContextFactory()))
                .ExecuteAsync(room.RoomId, room.ParticipantId, original.ItemId);
        }
        finally
        {
            pause.Release.TrySetResult();
        }
        using var existing = await pending;
        using var first = await ParseSnapshotAsync(existing, room, "1", 1);
        Assert.Equal(original.ItemId, first.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
        using var refreshed = await ReadAsync(client, room.RoomId, room.Credential);
        using var next = await ParseSnapshotAsync(refreshed, room, "3", 1);
        Assert.Equal(added.ItemId, next.RootElement.GetProperty("items")[0].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task FullRoom_ReturnsAllMaxSizeItemsAndHasMatchingOrderIndex()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await CreateAsync(database);
        var content = new string('\u4e00', 2730) + "ab";
        Assert.Equal(8192, Encoding.UTF8.GetByteCount(content));
        var add = CreateAdd(database);
        var expected = new List<Guid>();
        for (var index = 0; index < 300; index++)
            expected.Add((await add.ExecuteAsync(room.RoomId, room.ParticipantId, content)).ItemId);

        var plan = new ItemQueryPlan();
        await using var factory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
            services.AddDbContextFactory<PastoDbContext>(options => options.AddInterceptors(plan)));
        using var client = factory.CreateClient();
        using var response = await ReadAsync(client, room.RoomId, room.Credential);
        using var snapshot = await ParseSnapshotAsync(response, room, "300", 300);
        var items = snapshot.RootElement.GetProperty("items").EnumerateArray().ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            Assert.Equal(expected[299 - index], items[index].GetProperty("id").GetGuid());
            Assert.Equal(content, items[index].GetProperty("content").GetString());
        }
        var bodyBytes = (await response.Content.ReadAsByteArrayAsync()).Length;
        Assert.True(bodyBytes >= 300 * 8192);
        output.WriteLine($"Snapshot body: {bodyBytes} bytes. Default plan: {plan.DefaultPlan}");
        output.WriteLine($"Index eligibility plan: {plan.IndexPlan}");
        Assert.NotNull(plan.IndexPlan);
        Assert.Contains("Index Scan using ix_items_order", plan.IndexPlan);
        Assert.DoesNotContain("Sort", plan.IndexPlan);
    }

    private static Task<CreatedRoom> CreateAsync(PostgresTestDatabase database, string code = "ROOM01")
        => TestRoomCreation.Create(database, new SequenceRoomSecrets(code)).ExecuteAsync();

    private static AddItem CreateAdd(PostgresTestDatabase database)
        => new(new ItemAdditionStore(database.CreateContextFactory()));

    private static async Task<HttpResponseMessage> ReadAsync(HttpClient client, Guid roomId, string? credential, string suffix = "/items")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/rooms/{roomId}{suffix}");
        if (credential is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return await client.SendAsync(request);
    }

    private static async Task<JsonDocument> ParseSnapshotAsync(HttpResponseMessage response, CreatedRoom room, string version, int count)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var root = document.RootElement;
        Assert.Equal(version, root.GetProperty("snapshotVersion").GetString());
        Assert.Equal(count, root.GetProperty("itemCount").GetInt32());
        Assert.Equal(count, root.GetProperty("items").GetArrayLength());
        Assert.Equal(room.ExpiresAt, root.GetProperty("expiresAt").GetDateTime());
        return document;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(code, problem.RootElement.GetProperty("code").GetString());
    }

    private static bool IsItemQuery(DbCommand command)
        => command.CommandText.Contains("FROM clipboard_items AS", StringComparison.Ordinal);

    private sealed class PauseBeforeItems : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (IsItemQuery(command))
            {
                Assert.NotNull(command.Transaction);
                Assert.Equal(IsolationLevel.RepeatableRead, command.Transaction.IsolationLevel);
                await using var readOnly = command.Connection!.CreateCommand();
                readOnly.Transaction = command.Transaction;
                readOnly.CommandText = "SHOW transaction_read_only";
                Assert.Equal("on", await readOnly.ExecuteScalarAsync(cancellationToken));
                Reached.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(8), cancellationToken);
            }
            return result;
        }
    }

    private sealed class ItemQueryPlan : DbCommandInterceptor
    {
        public string? DefaultPlan { get; private set; }
        public string? IndexPlan { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (!IsItemQuery(command)) return result;
            await using var explain = command.Connection!.CreateCommand();
            explain.Transaction = command.Transaction;
            explain.CommandText = "EXPLAIN (COSTS OFF) " + command.CommandText;
            foreach (DbParameter parameter in command.Parameters)
                explain.Parameters.Add(((ICloneable)parameter).Clone());
            DefaultPlan = await ExplainAsync(explain, cancellationToken);

            // This isolated schema has one room. Check index eligibility separately from its cost-based plan.
            await using var settings = command.Connection.CreateCommand();
            settings.Transaction = command.Transaction;
            settings.CommandText = "SET LOCAL enable_seqscan = off; SET LOCAL enable_bitmapscan = off";
            await settings.ExecuteNonQueryAsync(cancellationToken);
            IndexPlan = await ExplainAsync(explain, cancellationToken);
            settings.CommandText = "SET LOCAL enable_seqscan = DEFAULT; SET LOCAL enable_bitmapscan = DEFAULT";
            await settings.ExecuteNonQueryAsync(cancellationToken);
            return result;
        }

        private static async Task<string> ExplainAsync(DbCommand command, CancellationToken cancellationToken)
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var plan = new StringBuilder();
            while (await reader.ReadAsync(cancellationToken)) plan.AppendLine(reader.GetString(0));
            return plan.ToString();
        }
    }
}
