using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Pasto.IntegrationTests;

internal sealed class PastoApiFactory(string connectionString,
    IDictionary<string, string?>? settings = null, Action<IServiceCollection>? configureServices = null,
    ILoggerProvider? logs = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Pasto", connectionString);
        builder.UseSetting("Cleanup:Enabled", "false");
        if (settings is not null)
            foreach (var (key, value) in settings) builder.UseSetting(key, value);
        builder.ConfigureLogging(logging =>
        {
            logging.ClearProviders();
            if (logs is not null) logging.AddProvider(logs);
        });
        if (configureServices is not null) builder.ConfigureServices(configureServices);
    }
}
