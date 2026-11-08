using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pasto.Server.Infrastructure.Persistence;

namespace Pasto.IntegrationTests;

internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly string connectionString;
    private readonly string schemaName = $"pasto_it_{Guid.NewGuid():N}";

    private PostgresTestDatabase(string configuredConnection)
    {
        var builder = new NpgsqlConnectionStringBuilder(configuredConnection);
        if (builder.Host is not ("127.0.0.1" or "localhost" or "::1") ||
            builder.Database is null ||
            !builder.Database.EndsWith("_test", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Integration tests require a loopback host and a dedicated database ending in '_test'.");
        }

        builder.SearchPath = schemaName;
        builder.Pooling = false;
        builder.Timeout = 5;
        builder.CommandTimeout = 10;
        builder.IncludeErrorDetail = false;
        builder.LogParameters = false;
        connectionString = builder.ConnectionString;
    }

    public string SchemaName => schemaName;
    public string ServerVersion { get; private set; } = "";

    public static async Task<PostgresTestDatabase> CreateAsync()
    {
        var configuredConnection = Environment.GetEnvironmentVariable("PASTO_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(configuredConnection))
        {
            throw new InvalidOperationException(
                "Set PASTO_TEST_CONNECTION_STRING to a local dedicated test database. See docs/testing.md.");
        }

        var database = new PostgresTestDatabase(configuredConnection);
        await using var connection = new NpgsqlConnection(database.connectionString);
        await connection.OpenAsync();
        database.ServerVersion = connection.PostgreSqlVersion.ToString();
        // schema 名稱僅由固定前綴與 GUID 產生，不接受外部輸入。
        // 先建立隔離空間，再讓正式 Migrator 管理自己的交易與 migration lock。
        await using var create = new NpgsqlCommand(
            $"CREATE SCHEMA \"{database.schemaName}\"", connection);
        await create.ExecuteNonQueryAsync();
        try
        {
            await using var context = database.CreateContext();
            await context.Database.MigrateAsync();
            return database;
        }
        catch (Exception initializationError)
        {
            // Migrate 失敗時，await using 尚未接管 database，必須在這裡清自己的 schema。
            try
            {
                await database.DisposeAsync();
            }
            catch (Exception cleanupError)
            {
                throw new AggregateException("Migration and test schema cleanup failed.", initializationError, cleanupError);
            }

            throw;
        }
    }

    public PastoDbContext CreateContext()
    {
        // All unqualified table names resolve only to this test's isolated schema.
        var options = new DbContextOptionsBuilder<PastoDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new PastoDbContext(options);
    }

    public async ValueTask DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        // Only the generated schema is removed; never call EnsureDeleted on the database.
        await using var cleanup = new NpgsqlCommand($"DROP SCHEMA \"{schemaName}\" CASCADE", connection);
        await cleanup.ExecuteNonQueryAsync();
    }
}
