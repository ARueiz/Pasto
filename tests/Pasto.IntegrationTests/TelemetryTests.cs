using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Pasto.Server.Application.Rooms;
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

    [Fact]
    public async Task CustomMeter_RecordsItemAndCleanupFailuresWithoutTags()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var room = await TestRoomCreation.Create(database, new SequenceRoomSecrets("METRIC")).ExecuteAsync();
        var worker = new FailingCleanupStore();
        await using var factory = new PastoApiFactory(database.ConnectionString,
            new Dictionary<string, string?> { ["Cleanup:Enabled"] = "true" },
            services => services.AddSingleton<IRoomCleanupStore>(worker));
        using var client = factory.CreateClient();
        var meter = factory.Services.GetRequiredService<IMeterFactory>().Create(ServerMetrics.MeterName);
        var measurements = new ConcurrentQueue<(string Name, long Value, int TagCount)>();
        var recordedCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, activeListener) =>
        {
            if (ReferenceEquals(instrument.Meter, meter))
                activeListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            measurements.Enqueue((instrument.Name, value, tags.Length));
            if (instrument.Name == "pasto.cleanup.failures")
                recordedCleanup.TrySetResult();
        });
        listener.Start();
        worker.Release.TrySetResult();

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/rooms/{room.RoomId}/items")
        {
            Content = JsonContent.Create(new { content = "" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", room.Credential);
        using var failed = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        await recordedCleanup.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(("pasto.cleanup.failures", 1L, 0), measurements);
        Assert.Contains(("pasto.item.add.failures", 1L, 0), measurements);
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

    private sealed class FailingCleanupStore : IRoomCleanupStore
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<RoomCleanupBacklog> MeasureBacklogAsync(CancellationToken cancellationToken)
        {
            await Release.Task.WaitAsync(cancellationToken);
            return new RoomCleanupBacklog(0, null);
        }

        public Task<int> DeleteBatchAsync(int batchSize, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Test cleanup failure");
    }
}
