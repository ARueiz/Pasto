using System.Reflection;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Pasto.Server.Api;

internal static class ServerLogging
{
    public const string ServiceName = "Pasto.Server";
    public static string ApplicationVersion { get; } = typeof(Program).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";

    public static void Configure(IConfiguration configuration, IHostEnvironment environment, LoggerConfiguration logger)
    {
        var level = configuration.GetValue<LogEventLevel?>("Logging:PastoMinimumLevel") ?? LogEventLevel.Information;
        logger.MinimumLevel.Is(level)
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .MinimumLevel.Override("Microsoft.AspNetCore.Hosting.Diagnostics", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .Filter.ByIncludingOnly(IsSafeSource)
            .Enrich.WithProperty("ServiceName", ServiceName)
            .Enrich.WithProperty("Environment", environment.EnvironmentName)
            .Enrich.WithProperty("ApplicationVersion", ApplicationVersion);

        if (environment.IsDevelopment())
            logger.WriteTo.Console();
        else
            logger.WriteTo.Console(new RenderedCompactJsonFormatter());
    }

    // 框架日誌可包含原始 URL、SQL 或例外內容。只收已查核的應用程式與程序生命週期事件。
    private static bool IsSafeSource(LogEvent logEvent) =>
        logEvent.Exception is null &&
        logEvent.Properties.TryGetValue("SourceContext", out var source) &&
        source is ScalarValue { Value: string category } &&
        (category.StartsWith("Pasto.Server.", StringComparison.Ordinal) || category == "Microsoft.Hosting.Lifetime");
}
