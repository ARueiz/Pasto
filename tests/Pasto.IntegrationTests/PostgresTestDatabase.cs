using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pasto.IntegrationTests.Persistence;

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
        await using var transaction = await connection.BeginTransactionAsync();
        // schemaName is generated internally from a fixed prefix and a GUID, never user input.
        await using var create = new NpgsqlCommand(
            $"CREATE SCHEMA \"{database.schemaName}\"", connection, transaction);
        await create.ExecuteNonQueryAsync();
        var sql = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Persistence", "schema.sql"));
        await using var setup = new NpgsqlCommand(sql, connection, transaction);
        await setup.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return database;
    }

    public RoomTestDbContext CreateContext()
    {
        // All unqualified table names resolve only to this test's isolated schema.
        var options = new DbContextOptionsBuilder<RoomTestDbContext>()
            .UseNpgsql(connectionString)
            .Options;
        return new RoomTestDbContext(options);
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
