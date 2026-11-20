using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Pasto.Server.Infrastructure.Persistence;

internal sealed class PostgresReadinessCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT 1");
        command.CommandTimeout = 1;
        await command.ExecuteScalarAsync(cancellationToken);
        return HealthCheckResult.Healthy();
    }
}
