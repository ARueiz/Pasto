using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Pasto.Server.Infrastructure.Persistence;

internal static class DeploymentDatabase
{
    private const int ConnectionAttempts = 6;

    internal static IReadOnlyList<string> RuntimeProductTables { get; } = Array.AsReadOnly(new[]
    {
        "public.rooms", "public.room_participants", "public.clipboard_items", "public.room_code_reservations"
    });

    private static readonly string RuntimeGrantsSql = $"""
        GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE {string.Join(", ", RuntimeProductTables)} TO pasto_runtime;
        GRANT SELECT ON TABLE public."__EFMigrationsHistory" TO pasto_runtime;
        """;

    public static string GetConnectionString(IConfiguration configuration, bool migrate)
    {
        var host = configuration["Deployment:DatabaseHost"];
        var password = configuration["Deployment:DatabasePassword"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Set Deployment__DatabaseHost and Deployment__DatabasePassword.");

        return new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Database = "pasto",
            Username = migrate ? "pasto_migrator" : "pasto_runtime",
            Password = password,
            SearchPath = "public",
            GssEncryptionMode = GssEncryptionMode.Disable,
            Timeout = 3,
            IncludeErrorDetail = false
        }.ConnectionString;
    }

    public static async Task<bool> PrepareAsync(IDbContextFactory<PastoDbContext> factory, ILogger logger,
        bool migrate, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken);
            var connection = context.Database.GetDbConnection();
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await connection.OpenAsync(cancellationToken);
                    break;
                }
                catch (Exception exception) when (attempt < ConnectionAttempts
                    && exception is NpgsqlException { IsTransient: true } or TimeoutException)
                {
                    logger.LogWarning("Deployment database unavailable on attempt {Attempt} of {MaxAttempts}.",
                        attempt, ConnectionAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                }
            }

            // Serialize the preflight, EF migration, and grants. Disposing the non-pooled migration connection releases the lock.
            if (migrate)
                await context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(807685301);", cancellationToken);

            var known = context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal);
            var applied = (await context.Database.GetAppliedMigrationsAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
            if (applied.Except(known).Any() || (!migrate && !applied.SetEquals(known)))
            {
                logger.LogCritical("Deployment database schema is incompatible: {MissingCount} missing and {UnknownCount} unknown migrations.",
                    known.Except(applied).Count(), applied.Except(known).Count());
                return false;
            }

            if (migrate)
            {
                await context.Database.MigrateAsync(cancellationToken);
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                await context.Database.ExecuteSqlRawAsync(RuntimeGrantsSql, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation("Deployment database migrations and runtime grants are ready.");
            }
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogCritical("Deployment database preparation failed in {Mode} mode: {ExceptionType}, SQL state {SqlState}.",
                migrate ? "migration" : "startup", exception.GetType().Name, (exception as PostgresException)?.SqlState);
            return false;
        }
    }
}
