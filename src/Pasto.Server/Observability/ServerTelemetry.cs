using System.Diagnostics;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Pasto.Server.Api;

namespace Pasto.Server.Observability;

internal static class ServerTelemetry
{
    public static void AddPastoTelemetry(this WebApplicationBuilder builder)
    {
        var exportEnabled = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        builder.Services.AddSingleton<ServerMetrics>();
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServerLogging.ServiceName,
                serviceVersion: ServerLogging.ApplicationVersion))
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation(options =>
                {
                    options.RecordException = false;
                    // 本階段僅收 HTTP 請求。Hub 呼叫需另外驗證 client 傳入的名稱與參數不會外流。
                    options.EnableAspNetCoreSignalRSupport = false;
                }).AddProcessor(new SafeHttpTraceProcessor());
                if (exportEnabled)
                    tracing.AddOtlpExporter();
            })
            .WithMetrics(metrics =>
            {
                metrics.AddMeter(ServerMetrics.MeterName, "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Http.Connections")
                    .AddView("http.server.request.duration", new MetricStreamConfiguration
                    {
                        TagKeys = ["http.request.method", "http.response.status_code", "http.route"]
                    })
                    .AddView("signalr.server.active_connections", new MetricStreamConfiguration
                    {
                        TagKeys = ["signalr.transport", "signalr.connection.status"]
                    })
                    .AddView("signalr.server.connection.duration", new MetricStreamConfiguration
                    {
                        TagKeys = ["signalr.transport", "signalr.connection.status"]
                    });
                if (exportEnabled)
                    metrics.AddOtlpExporter();
            });
    }

    private sealed class SafeHttpTraceProcessor : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity activity)
        {
            // 只保留固定欄位與路由樣板。任何 URL、header、使用者輸入或例外描述皆排除在匯出前。
            foreach (var key in activity.TagObjects.Select(tag => tag.Key).Where(key =>
                key is not ("http.request.method" or "http.response.status_code" or "http.route")).ToArray())
                activity.SetTag(key, null);

            var method = activity.GetTagItem("http.request.method") as string ?? "HTTP";
            activity.DisplayName = activity.GetTagItem("http.route") is string route ? $"{method} {route}" : method;
            activity.TraceStateString = null;
            activity.SetStatus(activity.Status);
        }
    }
}
