using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Infrastructure.Persistence;

namespace Pasto.Server.Api;

internal static class HealthEndpoints
{
    public static void MapPastoHealth(this WebApplication app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false })
            .AllowAnonymous().DisableRateLimiting();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") })
            .AllowAnonymous().DisableRateLimiting();
        app.MapGet("/health/cleanup", (RoomCleanupStatus cleanup, IOptions<RoomCleanupOptions> options) =>
        {
            var snapshot = cleanup.Read();
            return Results.Ok(new
            {
                options.Value.Enabled,
                snapshot.LastSuccessfulAt,
                snapshot.ConsecutiveFailures
            });
        }).AllowAnonymous().DisableRateLimiting();
    }
}
