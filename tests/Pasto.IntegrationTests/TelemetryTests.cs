using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Pasto.Server.Application.Items;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Persistence;
using Pasto.Server.Observability;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class TelemetryTests
{
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
