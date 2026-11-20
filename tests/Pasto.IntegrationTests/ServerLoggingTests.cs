using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pasto.Server.Api;
using Serilog;
using Xunit;

namespace Pasto.IntegrationTests;

[CollectionDefinition("Console logging", DisableParallelization = true)]
public sealed class ConsoleLoggingCollection;

[Collection("Console logging")]
public sealed class ServerLoggingTests
{
    [Fact]
    public void ProductionConsole_EmitsOneSafeJsonEventWithCorrelation()
    {
        const string secret = "S3CR3T-room-token-body";
        var original = Console.Out;
        using var output = new StringWriter();
        try
        {
            Console.SetOut(output);
            var environment = new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = Environments.Production };
            var configuration = new ConfigurationBuilder().Build();
            var setup = new LoggerConfiguration();
            ServerLogging.Configure(configuration, environment, setup);
            using var logger = setup.CreateLogger();
            using var factory = new Serilog.Extensions.Logging.SerilogLoggerFactory(logger);
            var application = factory.CreateLogger(typeof(RoomHubFailureFilter).FullName!);
            using var activity = new Activity("request").SetIdFormat(ActivityIdFormat.W3C).Start();
            application.LogWarning(new EventId(6009, "RoomConnectionTrackingFull"), "Safe event {Count}", 2);
            logger.ForContext("SourceContext", "Microsoft.AspNetCore.Hosting.Diagnostics")
                .Error("GET /r/{Secret}?access_token={Secret}", secret, secret);
            logger.ForContext("SourceContext", "Pasto.Server.Test").Error(new InvalidOperationException(secret), "Raw failure");

            var line = Assert.Single(output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
            using var json = JsonDocument.Parse(line);
            var entry = json.RootElement;
            Assert.Equal(6009, entry.GetProperty("EventId").GetProperty("Id").GetInt32());
            Assert.Equal("Safe event 2", entry.GetProperty("@m").GetString());
            Assert.Equal("Warning", entry.GetProperty("@l").GetString());
            Assert.Equal(TimeSpan.Zero, entry.GetProperty("@t").GetDateTimeOffset().Offset);
            Assert.Equal(activity.TraceId.ToString(), entry.GetProperty("@tr").GetString());
            Assert.Equal(activity.SpanId.ToString(), entry.GetProperty("@sp").GetString());
            Assert.Equal("Pasto.Server", entry.GetProperty("ServiceName").GetString());
            Assert.Equal("Production", entry.GetProperty("Environment").GetString());
            Assert.False(string.IsNullOrWhiteSpace(entry.GetProperty("ApplicationVersion").GetString()));
            Assert.DoesNotContain(secret, line);
        }
        finally
        {
            Console.SetOut(original);
        }
    }
}
