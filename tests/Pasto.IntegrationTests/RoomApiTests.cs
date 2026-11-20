using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Pasto.Server.Api;
using Pasto.Server.Application.Items;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Domain;
using Pasto.Server.Infrastructure.Persistence;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class RoomApiTests
{
    private const string StartupConnection = "Host=127.0.0.1;Database=pasto_test";
    private const string SensitiveSentinel = "test-only-sensitive-sentinel";
    [Fact]
    public async Task Create_PersistsAndReturnsSafeContractDespiteForgedHosts()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        using var logs = new CapturedLogs();
        await using var factory = new PastoApiFactory(database.ConnectionString, logs: logs);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/rooms");
        request.Headers.Host = "attacker.invalid";
        request.Headers.Add("X-Forwarded-Host", "other-attacker.invalid");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        AssertNoStore(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var json = body.RootElement;
        var roomId = json.GetProperty("roomId").GetGuid();
        var participantId = json.GetProperty("participantId").GetGuid();
        var code = json.GetProperty("code").GetString()!;
        var credential = json.GetProperty("credential").GetString()!;
        Assert.Equal($"/r/{code}", json.GetProperty("joinUrl").GetString());
        Assert.Equal($"/api/rooms/{roomId}", response.Headers.Location!.OriginalString);
        Assert.True(json.GetProperty("isCreator").GetBoolean());
        Assert.Equal("0", json.GetProperty("version").GetString());
        await using var verification = database.CreateContext();
        var room = await verification.Rooms.SingleAsync();
        var participant = await verification.Participants.SingleAsync();
        Assert.Equal(roomId, room.Id);
        Assert.Equal(participantId, room.CreatorParticipantId);
        Assert.Equal(roomId, participant.RoomId);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(credential)), participant.CredentialHash);
        Assert.Equal(room.ExpiresAt, json.GetProperty("expiresAt").GetDateTime());
        Assert.Equal(TimeSpan.FromHours(5), room.ExpiresAt - room.CreatedAt);
        Assert.DoesNotContain(code, logs.Text);
        Assert.DoesNotContain(credential, logs.Text);
    }

    [Fact]
    public async Task ActualHost_RegistersExpectedLifetimesAndSanitizesDatabaseConfiguration()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString)
        {
            IncludeErrorDetail = true,
            LogParameters = true,
            MinPoolSize = 3,
            MaxPoolSize = 999
        };
        await using var factory = new PastoApiFactory(connection.ConnectionString);
        using var client = factory.CreateClient();
        Assert.Equal(5, factory.Services.GetRequiredService<IOptions<CreateRoomOptions>>().Value.MaxCodeAttempts);
        Assert.Equal(64, factory.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.MaxPoolSize);
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        var configured = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        Assert.Equal(64, configured.MaxPoolSize);
        Assert.Equal(0, configured.MinPoolSize);
        Assert.False(configured.LogParameters);
        Assert.False(configured.IncludeErrorDetail);
        Assert.True(configured.Pooling);
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        Assert.Same(first.ServiceProvider.GetRequiredService<CreateRoom>(), first.ServiceProvider.GetRequiredService<CreateRoom>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<CreateRoom>(), second.ServiceProvider.GetRequiredService<CreateRoom>());
        Assert.NotSame(first.ServiceProvider.GetRequiredService<IRoomCreationStore>(), second.ServiceProvider.GetRequiredService<IRoomCreationStore>());
        var contextFactory = first.ServiceProvider.GetRequiredService<IDbContextFactory<PastoDbContext>>();
        Assert.Same(contextFactory, second.ServiceProvider.GetRequiredService<IDbContextFactory<PastoDbContext>>());
        await using var a = await contextFactory.CreateDbContextAsync();
        await using var b = await contextFactory.CreateDbContextAsync();
        Assert.NotSame(a, b);
    }

    [Theory]
    [InlineData("RoomCreation:MaxCodeAttempts", "0")]
    [InlineData("RoomCreation:MaxCodeAttempts", "21")]
    [InlineData("Database:MaxPoolSize", "0")]
    [InlineData("Database:MaxPoolSize", "257")]
    [InlineData("Api:CreatePerIp:TokenLimit", "0")]
    [InlineData("Api:CreateGlobal:TokensPerPeriod", "0")]
    [InlineData("Api:CreatePerIp:PeriodSeconds", "0")]
    [InlineData("Api:TrustedProxies:0", "not-an-ip")]
    public void InvalidOptions_FailWhenActualHostStarts(string key, string value)
    {
        using var factory = new PastoApiFactory(StartupConnection, new Dictionary<string, string?> { [key] = value });
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public async Task ValidMaxCodeAttempts_IsHonoredByActualHost(int attempts)
    {
        var store = new RejectingStore();
        await using var factory = FakeStoreFactory(store, new Dictionary<string, string?>
        {
            ["RoomCreation:MaxCodeAttempts"] = attempts.ToString()
        });
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/rooms", null);
        await AssertProblemAsync(response, 503, "ServiceUnavailable");
        Assert.Equal(attempts, store.Calls);
    }

    [Theory]
    [InlineData("unavailable", 404, "RoomUnavailable")]
    [InlineData("mismatch", 401, "ParticipantUnauthorized")]
    [InlineData("ownership", 403, "RoomDeleteForbidden")]
    [InlineData("itemOwnership", 403, "ItemDeleteForbidden")]
    [InlineData("busy", 503, "ServiceUnavailable")]
    [InlineData("allocation", 503, "ServiceUnavailable")]
    [InlineData("participants", 409, "ParticipantLimitReached")]
    [InlineData("items", 409, "RoomItemLimitReached")]
    [InlineData("content", 400, "InvalidRequest")]
    [InlineData("database", 503, "ServiceUnavailable")]
    [InlineData("wrappedDatabase", 503, "ServiceUnavailable")]
    [InlineData("efDatabase", 503, "ServiceUnavailable")]
    [InlineData("canceled", 503, "ServiceUnavailable")]
    [InlineData("unexpected", 500, "InternalError")]
    public async Task Errors_AreSanitizedProblemDetailsWithNoStore(string kind, int status, string code)
    {
        Exception failure = kind switch
        {
            "unavailable" => new RoomUnavailableException(),
            "mismatch" => new RoomParticipantMismatchException(),
            "ownership" => new RoomOwnershipException(),
            "itemOwnership" => new ItemOwnershipException(),
            "busy" => new RoomBusyException(new Exception(SensitiveSentinel)),
            "allocation" => new RoomCodeAllocationException(),
            "participants" => new ParticipantLimitReachedException(),
            "items" => new RoomItemLimitReachedException(),
            "content" => new InvalidItemContentException(ItemContentError.InvalidUnicode),
            "database" => new NpgsqlException(SensitiveSentinel),
            "wrappedDatabase" => new InvalidOperationException(SensitiveSentinel, new NpgsqlException(SensitiveSentinel)),
            "efDatabase" => new DbUpdateException(SensitiveSentinel),
            "canceled" => new OperationCanceledException(SensitiveSentinel),
            _ => new InvalidOperationException(SensitiveSentinel)
        };
        using var logs = new CapturedLogs();
        var store = new RejectingStore(failure);
        await using var factory = FakeStoreFactory(store, logs: logs);
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/rooms", null);
        await AssertProblemAsync(response, status, code);
        if (status == 401) Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
        Assert.Equal(1, store.Calls);
        Assert.DoesNotContain(SensitiveSentinel, await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(SensitiveSentinel, logs.Text);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(body.RootElement.GetProperty("correlationId").GetString()!, logs.Text);
        var entry = Assert.Single(logs.Entries, entry => entry.Category == typeof(ApiFailureMiddleware).FullName);
        Assert.Equal(status == 500 ? LogLevel.Error : status == 503 ? LogLevel.Warning : LogLevel.Information, entry.Level);
        if (status == 500)
        {
            Assert.Contains(failure.GetType().FullName!, entry.Text);
            Assert.Contains(nameof(RejectingStore.TryCreateAsync), entry.Text);
            Assert.DoesNotContain(".cs:line", entry.Text);
        }
    }

    [Fact]
    public async Task RealPostgresCollision_ProviderErrorLogsDoNotContainCodesOrCredential()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await TestRoomCreation.Create(database, new SequenceRoomSecrets("TAKEN1")).ExecuteAsync();
        using var logs = new CapturedLogs();
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { IncludeErrorDetail = true, LogParameters = true };
        await using var factory = new PastoApiFactory(connection.ConnectionString, configureServices: services =>
        {
            services.RemoveAll<IRoomSecrets>();
            services.AddSingleton<IRoomSecrets>(new SequenceRoomSecrets("TAKEN1", "FRESH1"));
        }, logs: logs);
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/rooms", null);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var credential = body.RootElement.GetProperty("credential").GetString()!;
        Assert.Single(logs.Entries, entry => entry.Category == typeof(CreateRoom).FullName
            && entry.Level == LogLevel.Warning && entry.Text.Contains("Room code collision", StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Entries, entry => entry.Category.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal));
        Assert.DoesNotContain("TAKEN1", logs.Text);
        Assert.DoesNotContain("FRESH1", logs.Text);
        Assert.DoesNotContain(credential, logs.Text);
    }

    [Theory]
    [InlineData("/missing", 404)]
    [InlineData("/api/rooms", 405)]
    public async Task RoutingErrors_AlsoReturnProblemDetailsAndNoStore(string path, int status)
    {
        await using var factory = new PastoApiFactory(StartupConnection);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        await AssertProblemAsync(response, status, "InvalidRequest");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UntrustedForwardedFor_CannotBypassPerIpBucket(bool configureDifferentProxy)
    {
        var store = new RejectingStore();
        var settings = RateSettings(1, 10);
        if (configureDifferentProxy) settings["Api:TrustedProxies:0"] = "10.0.0.1";
        await using var factory = FakeStoreFactory(store, settings);
        var first = await SendFromAsync(factory, "198.51.100.1", "203.0.113.1");
        var second = await SendFromAsync(factory, "198.51.100.1", "203.0.113.2");
        Assert.Equal(503, first.Response.StatusCode);
        AssertRateLimited(second);
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task TrustedProxy_UsesForwardedClientIp()
    {
        var store = new RejectingStore();
        var settings = RateSettings(1, 10);
        settings["Api:TrustedProxies:0"] = "10.0.0.1";
        await using var factory = FakeStoreFactory(store, settings);
        Assert.Equal(503, (await SendFromAsync(factory, "10.0.0.1", "203.0.113.1")).Response.StatusCode);
        Assert.Equal(503, (await SendFromAsync(factory, "10.0.0.1", "203.0.113.2")).Response.StatusCode);
        AssertRateLimited(await SendFromAsync(factory, "10.0.0.1", "203.0.113.1"));
        Assert.Equal(2, store.Calls);
    }

    [Fact]
    public async Task PerIpBucket_IsIndependentAndNormalizesMappedIpv4()
    {
        var store = new RejectingStore();
        await using var factory = FakeStoreFactory(store, RateSettings(1, 10));
        Assert.Equal(503, (await SendFromAsync(factory, "198.51.100.1")).Response.StatusCode);
        Assert.Equal(503, (await SendFromAsync(factory, "198.51.100.2")).Response.StatusCode);
        AssertRateLimited(await SendFromAsync(factory, "::ffff:198.51.100.1"));
        Assert.Equal(2, store.Calls);
    }

    [Fact]
    public async Task GlobalBucket_CountsFailuresAcrossDifferentIps()
    {
        var store = new RejectingStore();
        await using var factory = FakeStoreFactory(store, RateSettings(10, 1));
        Assert.Equal(503, (await SendFromAsync(factory, "198.51.100.1")).Response.StatusCode);
        AssertRateLimited(await SendFromAsync(factory, "198.51.100.2"));
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task GlobalBucket_ConcurrentRequestsDoNotExceedBurst()
    {
        var store = new RejectingStore();
        await using var factory = FakeStoreFactory(store, RateSettings(10, 1));
        using var client = factory.CreateClient();
        var responses = await Task.WhenAll(SendFromAsync(factory, "198.51.100.1"), SendFromAsync(factory, "198.51.100.2"));
        Assert.Single(responses, response => response.Response.StatusCode == 503);
        AssertRateLimited(Assert.Single(responses, response => response.Response.StatusCode == 429));
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task RateLimitResponse_HasProblemBodyNoStoreAndRetryAfter()
    {
        var store = new RejectingStore();
        await using var factory = FakeStoreFactory(store, RateSettings(1, 10));
        using var client = factory.CreateClient();
        using var first = await client.PostAsync("/api/rooms", null);
        using var rejected = await client.PostAsync("/api/rooms", null);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        AssertNoStore(rejected);
        Assert.NotNull(rejected.Headers.RetryAfter?.Delta);
        using var body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
        Assert.Equal("RateLimited", body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("correlationId").GetString()));
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task CancellationBeforeCommit_Is503AndRollsBackWholeRoom()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var failure = new CancelBeforeHttpCommitInterceptor();
        await using var factory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
            services.AddDbContextFactory<PastoDbContext>(options => options.AddInterceptors(failure)));
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/rooms", null);
        await AssertProblemAsync(response, 503, "ServiceUnavailable");
        Assert.True(failure.ReachedCommit);
        await using var verification = database.CreateContext();
        Assert.False(await verification.Rooms.AnyAsync());
        Assert.False(await verification.Participants.AnyAsync());
        Assert.False(await verification.CodeReservations.AnyAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitCompletedButResponseFailed_Is503WithoutRetryOrRollbackClaim(bool cancel)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var failure = new FailAfterHttpCommitInterceptor(cancel);
        await using var factory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
            services.AddDbContextFactory<PastoDbContext>(options => options.AddInterceptors(failure)));
        using var client = factory.CreateClient();
        using var response = await client.PostAsync("/api/rooms", null);
        await AssertProblemAsync(response, 503, "ServiceUnavailable");
        Assert.Equal(1, failure.Commits);
        await using var verification = database.CreateContext();
        Assert.Equal(1, await verification.Rooms.CountAsync());
        Assert.Equal(1, await verification.Participants.CountAsync());
        Assert.Equal(1, await verification.CodeReservations.CountAsync());
    }

    private static PastoApiFactory FakeStoreFactory(RejectingStore store,
        Dictionary<string, string?>? settings = null, ILoggerProvider? logs = null)
    {
        settings ??= new();
        settings.TryAdd("RoomCreation:MaxCodeAttempts", "1");
        return new PastoApiFactory(StartupConnection, settings, services =>
        {
            services.RemoveAll<IRoomCreationStore>();
            services.AddScoped<IRoomCreationStore>(_ => store);
        }, logs);
    }

    private static Dictionary<string, string?> RateSettings(int perIp, int global) => new()
    {
        ["Api:CreatePerIp:TokenLimit"] = perIp.ToString(),
        ["Api:CreatePerIp:TokensPerPeriod"] = "1",
        ["Api:CreateGlobal:TokenLimit"] = global.ToString(),
        ["Api:CreateGlobal:TokensPerPeriod"] = "1",
        ["Api:CreatePerIp:PeriodSeconds"] = "86400",
        ["Api:CreateGlobal:PeriodSeconds"] = "86400"
    };

    private static Task<Microsoft.AspNetCore.Http.HttpContext> SendFromAsync(PastoApiFactory factory, string ip, string? forwarded = null)
        => factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
            context.Request.Method = "POST";
            context.Request.Path = "/api/rooms";
            if (forwarded is not null) context.Request.Headers["X-Forwarded-For"] = forwarded;
        });

    private static void AssertRateLimited(Microsoft.AspNetCore.Http.HttpContext response)
    {
        Assert.Equal(429, response.Response.StatusCode);
        Assert.Equal("no-store", response.Response.Headers.CacheControl);
        Assert.True(int.Parse(response.Response.Headers.RetryAfter.ToString()) > 0);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode);
        AssertNoStore(response);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(code, body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("correlationId").GetString()));
        Assert.Equal(status, body.RootElement.GetProperty("status").GetInt32());
        Assert.False(response.Headers.Contains("Retry-After"));
    }

    private sealed class RejectingStore(Exception? failure = null) : IRoomCreationStore
    {
        private int calls;
        public int Calls => calls;
        public Task<Room?> TryCreateAsync(string code, byte[] credentialHash, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            if (failure is not null) throw failure;
            return Task.FromResult<Room?>(null);
        }
    }

    private sealed class FailAfterHttpCommitInterceptor(bool cancel) : DbTransactionInterceptor
    {
        public int Commits { get; private set; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Commits++;
            if (cancel) throw new OperationCanceledException();
            throw new NpgsqlException("Simulated response loss after COMMIT.");
        }
    }

    private sealed class CancelBeforeHttpCommitInterceptor : DbTransactionInterceptor
    {
        public bool ReachedCommit { get; private set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            ReachedCommit = true;
            throw new OperationCanceledException();
        }
    }

    private static void AssertNoStore(HttpResponseMessage response)
        => Assert.True(response.Headers.CacheControl?.NoStore);

    private sealed class CapturedLogs : ILoggerProvider
    {
        private readonly ConcurrentQueue<(string Category, LogLevel Level, string Text)> entries = new();
        public IEnumerable<(string Category, LogLevel Level, string Text)> Entries => entries;
        public string Text => string.Join('\n', entries.Select(entry => entry.Text));
        public ILogger CreateLogger(string categoryName) => new Capture(categoryName, entries);
        public void Dispose() { }
        private sealed class Capture(string category, ConcurrentQueue<(string Category, LogLevel Level, string Text)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
                => entries.Enqueue((category, logLevel, formatter(state, exception) + (exception?.ToString() ?? "")));
        }
    }
}
