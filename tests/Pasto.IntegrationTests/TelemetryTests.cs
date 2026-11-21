using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Pasto.Server.Api;
using Pasto.Server.Application.Items;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Domain;
using Pasto.Server.Infrastructure.Persistence;
using Pasto.Server.Observability;
using Xunit;

namespace Pasto.IntegrationTests;

// OpenTelemetry 的 meter 名稱訂閱會跨測試主機，匯出驗收獨立執行。
[CollectionDefinition("HTTP metric export", DisableParallelization = true)]
public sealed class HttpMetricExportCollection;

[Collection("HTTP metric export")]
public sealed class TelemetryTests
{
    [Fact]
    public async Task ExportedHttpMetricsDistinguishOverloadReasonsFromStorageFailure()
    {
        var exporter = new CapturedHttpMetrics();
        var clock = new ManualClock();
        await using var factory = new PastoApiFactory("Host=127.0.0.1;Port=1;Database=pasto_test", configureServices: services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            services.AddSingleton<IRoomCreationStore, FailingRoomStore>();
            services.Configure<HttpWorkOptions>(options =>
            {
                options.JoinLimit = 1;
                options.JoinPerSource = 1;
                options.HighWatermark = 2;
                options.RecoveryBelow = 1;
            });
            services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddReader(new PeriodicExportingMetricReader(exporter)));
        });
        using var client = factory.CreateClient();
        var admission = factory.Services.GetRequiredService<HttpWorkAdmission>();
        using (Assert.IsType<HttpWorkAdmission.Lease>(admission.TryAcquire(HttpWork.Join, "test-source", out _)))
            await AssertProblemAsync(await client.PostAsync("/api/rooms", null), HttpStatusCode.ServiceUnavailable, "Overloaded");
        using (Assert.IsType<HttpWorkAdmission.Lease>(admission.TryAcquire(HttpWork.ReadDelete, "test-source", out _)))
        using (Assert.IsType<HttpWorkAdmission.Lease>(admission.TryAcquire(HttpWork.ReadDelete, "test-source", out _)))
        {
            clock.Advance(TimeSpan.FromSeconds(2));
            await AssertProblemAsync(await client.PostAsync("/api/rooms", null), HttpStatusCode.ServiceUnavailable, "Overloaded");
        }
        clock.Advance(TimeSpan.FromSeconds(10));
        await AssertProblemAsync(await client.PostAsync("/api/rooms", null), HttpStatusCode.ServiceUnavailable, "ServiceUnavailable");

        var points = await exporter.ReadAsync(factory.Services, status: 503, expectedCount: 3);
        Assert.Equal(3, points.Length);
        Assert.All(points, point => Assert.Equal("/api/rooms", point["http.route"]));
        Assert.Contains(points, point => Equals(point.GetValueOrDefault("pasto.admission.reason"), "in_flight"));
        Assert.Contains(points, point => Equals(point.GetValueOrDefault("pasto.admission.reason"), "high_watermark"));
        Assert.Single(points, point => !point.ContainsKey("pasto.admission.reason"));
    }

    [Fact]
    public async Task ExportedHttpMetricsIdentifyPublicDatabaseAndParticipantRateLimits()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await TestRoomCreation.Create(database, new SequenceRoomSecrets("METRIC")).ExecuteAsync();
        var exporter = new CapturedHttpMetrics();
        var settings = new Dictionary<string, string?>();
        foreach (var (profile, limit) in new[] { ("CreatePerIp", 1), ("JoinPerIp", 1), ("JoinGlobal", 1),
            ("ReadPerParticipant", 1), ("ReadGlobal", 2) })
        {
            settings[$"Api:{profile}:TokenLimit"] = limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
            settings[$"Api:{profile}:TokensPerPeriod"] = "1";
            settings[$"Api:{profile}:PeriodSeconds"] = "86400";
        }
        await using var factory = new PastoApiFactory(database.ConnectionString, settings, services =>
            services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddReader(new PeriodicExportingMetricReader(exporter))));
        using var client = factory.CreateClient();
        using var created = await client.PostAsync("/api/rooms", null);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        await AssertProblemAsync(await client.PostAsync("/api/rooms", null), HttpStatusCode.TooManyRequests, "RateLimited");
        using var incomplete = new StringContent("{", Encoding.UTF8, "application/json");
        await AssertProblemAsync(await client.PostAsync("/api/rooms/join", incomplete), HttpStatusCode.BadRequest, "InvalidRequest");
        using var joined = await client.PostAsJsonAsync("/api/rooms/join", new { code = room.Code });
        Assert.Equal(HttpStatusCode.Created, joined.StatusCode);
        await AssertProblemAsync(await client.PostAsJsonAsync("/api/rooms/join", new { code = room.Code }), HttpStatusCode.TooManyRequests, "RateLimited");

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", room.Credential);
        var location = $"/api/rooms/{room.RoomId}/me";
        using var read = await client.GetAsync(location);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        await AssertProblemAsync(await client.GetAsync(location), HttpStatusCode.TooManyRequests, "RateLimited");
        await AssertProblemAsync(await client.GetAsync(location), HttpStatusCode.TooManyRequests, "RateLimited");

        var points = await exporter.ReadAsync(factory.Services, status: 429, expectedCount: 4);
        Assert.Equal(4, points.Length);
        AssertPoint("/api/rooms", "source_rate");
        AssertPoint("/api/rooms/join", "source_rate");
        AssertPoint("/api/rooms/{roomId:guid}/me", "participant_rate");
        AssertPoint("/api/rooms/{roomId:guid}/me", "database_floor");

        void AssertPoint(string route, string reason)
            => Assert.Contains(points, point => Equals(point.GetValueOrDefault("http.route"), route) &&
                Equals(point.GetValueOrDefault("pasto.rate_limit.reason"), reason));
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        using (response)
        {
            Assert.Equal(status, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(code, body.GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task HttpSpans_UseRouteTemplatesAndExcludeRawUrlsAndHeaders()
    {
        var exporter = new CapturedSpans();
        await using var factory = new PastoApiFactory("Host=127.0.0.1;Port=1;Database=pasto_test", configureServices: services =>
            services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(new SimpleActivityExportProcessor(exporter))));
        using var client = factory.CreateClient();
        using var roomLink = await client.GetAsync("/r/LEAK01?access_token=secret-query-sentinel");
        using var hub = new HttpRequestMessage(HttpMethod.Post, "/hubs/room/negotiate?access_token=secret-query-sentinel");
        hub.Headers.Host = "sensitive-host.invalid";
        hub.Headers.UserAgent.ParseAdd("sensitive-user-agent");
        hub.Headers.TryAddWithoutValidation("traceparent", "00-0123456789abcdef0123456789abcdef-0123456789abcdef-01");
        hub.Headers.TryAddWithoutValidation("tracestate", "test=secret-tracestate");
        using var handshake = await client.SendAsync(hub);
        Assert.Equal(HttpStatusCode.NotFound, roomLink.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, handshake.StatusCode);
        Assert.True(factory.Services.GetRequiredService<TracerProvider>().ForceFlush());

        var spans = exporter.Spans.Where(span => span.Kind == ActivityKind.Server).ToArray();
        Assert.Contains(spans, span => span.DisplayName == "POST /hubs/room/negotiate");
        Assert.Contains(spans, span => span.DisplayName == "GET");
        foreach (var span in spans)
        {
            Assert.Null(span.TraceStateString);
            Assert.All(span.TagObjects, tag => Assert.Contains(tag.Key,
                new[] { "http.request.method", "http.response.status_code", "http.route" }));
            var data = string.Join(' ', span.TagObjects.Select(tag => tag.Value).Prepend(span.DisplayName));
            foreach (var sensitive in new[] { "LEAK01", "access_token", "secret-query-sentinel", "sensitive-host", "sensitive-user-agent" })
                Assert.DoesNotContain(sensitive, data);
        }
    }

    [Theory]
    [InlineData("empty", HttpStatusCode.BadRequest)]
    [InlineData("limit", HttpStatusCode.Conflict)]
    [InlineData("unavailable", HttpStatusCode.NotFound)]
    [InlineData("participant", HttpStatusCode.Unauthorized)]
    [InlineData("storage", HttpStatusCode.ServiceUnavailable)]
    [InlineData("busy", HttpStatusCode.ServiceUnavailable)]
    [InlineData("unexpected", HttpStatusCode.InternalServerError)]
    public async Task ItemFailureMeter_OnlyCountsServerFailuresWithoutTags(string failure, HttpStatusCode expectedStatus)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await TestRoomCreation.Create(database, new SequenceRoomSecrets("METRIC")).ExecuteAsync();
        Exception exception = failure switch
        {
            "limit" => new RoomItemLimitReachedException(),
            "unavailable" => new RoomUnavailableException(),
            "participant" => new RoomParticipantMismatchException(),
            "storage" => new NpgsqlException("Test storage failure"),
            "busy" => new RoomBusyException(new TimeoutException()),
            _ => new InvalidOperationException("Test item store failure")
        };
        await using var factory = new PastoApiFactory(database.ConnectionString, configureServices: services =>
            services.AddSingleton<IItemAdditionStore>(new RejectingItemStore(exception)));
        using var client = factory.CreateClient();
        using var metrics = new CapturedMeasurements(factory.Services);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/rooms/{room.RoomId}/items")
        {
            Content = JsonContent.Create(new { content = failure == "empty" ? "" : "test item" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", room.Credential);
        using var failed = await client.SendAsync(request);
        Assert.Equal(expectedStatus, failed.StatusCode);
        if ((int)expectedStatus >= 500)
            Assert.Equal(("pasto.item.add.failures", 1L, 0), Assert.Single(metrics.Entries));
        else
            Assert.Empty(metrics.Entries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CleanupFailureMeter_MatchesMainRoundOnlyEvenWhenMaintenanceFails(bool mainRoundFails)
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var primary = new ControlledCleanupStore(mainRoundFails);
        using var logs = new CleanupMaintenanceLogs();
        await using var factory = new PastoApiFactory(database.ConnectionString,
            new Dictionary<string, string?> { ["Cleanup:Enabled"] = "true" },
            services =>
            {
                services.AddSingleton<IRoomCleanupStore>(primary);
                services.AddSingleton(new RoomCleanupStore(database.CreateContextFactory(new FailReclaimQuery())));
            }, logs);
        using var client = factory.CreateClient();
        using var metrics = new CapturedMeasurements(factory.Services);
        primary.Release.TrySetResult();
        await logs.ReclaimFailed.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var status = factory.Services.GetRequiredService<RoomCleanupStatus>().Read();
        if (mainRoundFails)
        {
            Assert.Equal(1, status.ConsecutiveFailures);
            Assert.Equal(("pasto.cleanup.failures", 1L, 0), Assert.Single(metrics.Entries));
        }
        else
        {
            Assert.Equal(0, status.ConsecutiveFailures);
            Assert.NotNull(status.LastSuccessfulAt);
            Assert.Empty(metrics.Entries);
        }
    }

    private sealed class CapturedSpans : BaseExporter<Activity>
    {
        public ConcurrentQueue<Activity> Spans { get; } = new();
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var span in batch)
                Spans.Enqueue(span);
            return ExportResult.Success;
        }
    }

    private sealed class CapturedHttpMetrics : BaseExporter<Metric>
    {
        private Dictionary<string, object?>[] exported = [];

        public override ExportResult Export(in Batch<Metric> batch)
        {
            var points = new List<Dictionary<string, object?>>();
            foreach (var metric in batch)
            {
                if (metric.Name != "http.server.request.duration") continue;
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var tags = new Dictionary<string, object?>();
                    foreach (var tag in point.Tags) tags.Add(tag.Key, tag.Value);
                    points.Add(tags);
                }
            }
            Volatile.Write(ref exported, points.ToArray());
            return ExportResult.Success;
        }

        internal async Task<Dictionary<string, object?>[]> ReadAsync(IServiceProvider services, int status, int expectedCount)
        {
            var provider = services.GetRequiredService<MeterProvider>();
            var deadline = Stopwatch.StartNew();
            Dictionary<string, object?>[] points;
            do
            {
                Assert.True(provider.ForceFlush());
                points = Volatile.Read(ref exported).Where(point => Equals(point.GetValueOrDefault("http.response.status_code"), status)).ToArray();
                if (points.Length >= expectedCount) break;
                await Task.Delay(10);
            } while (deadline.Elapsed < TimeSpan.FromSeconds(2));
            return points;
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        internal void Advance(TimeSpan time) => Interlocked.Add(ref timestamp, time.Ticks);
    }

    private sealed class FailingRoomStore : IRoomCreationStore
    {
        public Task<Room?> TryCreateAsync(string code, byte[] credentialHash, CancellationToken cancellationToken)
            => throw new NpgsqlException("Test storage failure");
    }

    private sealed class CapturedMeasurements : IDisposable
    {
        private readonly MeterListener listener = new();
        public ConcurrentQueue<(string Name, long Value, int TagCount)> Entries { get; } = new();

        public CapturedMeasurements(IServiceProvider services)
        {
            var meter = services.GetRequiredService<IMeterFactory>().Create(ServerMetrics.MeterName);
            listener.InstrumentPublished = (instrument, activeListener) =>
            {
                if (ReferenceEquals(instrument.Meter, meter))
                    activeListener.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Entries.Enqueue((instrument.Name, value, tags.Length)));
            listener.Start();
        }

        public void Dispose() => listener.Dispose();
    }

    private sealed class RejectingItemStore(Exception exception) : IItemAdditionStore
    {
        public Task<AddedItem> AddAsync(Guid roomId, Guid participantId, string content, int contentBytes,
            CancellationToken cancellationToken) => throw exception;
    }

    private sealed class ControlledCleanupStore(bool fails) : IRoomCleanupStore
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RoomCleanupBacklog> MeasureBacklogAsync(CancellationToken cancellationToken)
        {
            await Release.Task.WaitAsync(cancellationToken);
            return new RoomCleanupBacklog(0, null);
        }

        public Task<int> DeleteBatchAsync(int batchSize, CancellationToken cancellationToken)
            => fails ? throw new InvalidOperationException("Test cleanup failure") : Task.FromResult(0);
    }

    private sealed class FailReclaimQuery : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM room_code_reservations AS reservation", StringComparison.Ordinal))
                throw new NpgsqlException("Test reclamation failure");

            return ValueTask.FromResult(result);
        }
    }

    private sealed class CleanupMaintenanceLogs : ILoggerProvider, ILogger
    {
        public TaskCompletionSource ReclaimFailed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Dispose() { }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (eventId.Id == 6008 && logLevel == LogLevel.Warning)
                ReclaimFailed.TrySetResult();
        }
    }
}
