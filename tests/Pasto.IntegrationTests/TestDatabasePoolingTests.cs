using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Pasto.Server.Infrastructure.Persistence;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class TestDatabasePoolingTests
{
    [Fact]
    public async Task SequentialContexts_ReuseBackend_AndResetSessionSettings()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        PoolSession original;
        await using (var context = database.CreateContext())
        {
            await context.Database.OpenConnectionAsync();
            original = await ReadSessionAsync(context);
            await context.Database.ExecuteSqlRawAsync("SET application_name = 'pasto_pool_probe'");
            var lockTimeout = original.LockTimeout == "17ms" ? "18ms" : "17ms";
            await context.Database.ExecuteSqlRawAsync(lockTimeout == "17ms"
                ? "SET lock_timeout = '17ms'"
                : "SET lock_timeout = '18ms'");
            Assert.Equal(lockTimeout, (await ReadSessionAsync(context)).LockTimeout);
        }
        await using var reused = database.CreateContext();
        var session = await ReadSessionAsync(reused);
        Assert.Equal(original.Pid, session.Pid);
        Assert.Equal(original.ApplicationName, session.ApplicationName);
        Assert.Equal(original.LockTimeout, session.LockTimeout);
        Assert.Equal(database.SchemaName, session.SearchPath);
    }

    [Fact]
    public async Task ConcurrentContexts_LeaseDifferentBackends_AndSchemasStayIsolated()
    {
        await using var first = await PostgresTestDatabase.CreateAsync();
        await using var second = await PostgresTestDatabase.CreateAsync();
        await using var a = first.CreateContext();
        await using var b = first.CreateContext();
        await using var c = second.CreateContext();
        await Task.WhenAll(a.Database.OpenConnectionAsync(), b.Database.OpenConnectionAsync(), c.Database.OpenConnectionAsync());
        var sessions = await Task.WhenAll(ReadSessionAsync(a), ReadSessionAsync(b), ReadSessionAsync(c));
        Assert.Equal(3, sessions.Select(x => x.Pid).Distinct().Count());
        Assert.Equal(first.SchemaName, sessions[0].SearchPath);
        Assert.Equal(first.SchemaName, sessions[1].SearchPath);
        Assert.Equal(second.SchemaName, sessions[2].SearchPath);
    }

    [Fact]
    public async Task Disposal_ClosesIdleBackendsAndDropsOnlyOwnedSchema()
    {
        await using var observer = await PostgresTestDatabase.CreateAsync();
        var database = await PostgresTestDatabase.CreateAsync();
        var schema = database.SchemaName;
        int pid;
        try
        {
            await using var context = database.CreateContext();
            pid = (await ReadSessionAsync(context)).Pid;
        }
        finally
        {
            await database.DisposeAsync();
        }
        await using var inspection = observer.CreateContext();
        Assert.False(await inspection.Database.SqlQuery<bool>(
            $"SELECT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = {schema}) AS \"Value\"").SingleAsync());
        Assert.True(await inspection.Database.SqlQuery<bool>(
            $"SELECT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = {observer.SchemaName}) AS \"Value\"").SingleAsync());
        // 後端退出由 server 非同步觀察；有界輪詢實際狀態，不用固定 sleep 判定已清理。
        var elapsed = Stopwatch.StartNew();
        while (await inspection.Database.SqlQuery<bool>(
            $"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE pid = {pid}) AS \"Value\"").SingleAsync())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(3), "Disposed pool kept an idle PostgreSQL backend alive.");
            await Task.Delay(10);
        }
    }

    private static Task<PoolSession> ReadSessionAsync(PastoDbContext context)
        => context.Database.SqlQuery<PoolSession>($"""
            SELECT pg_backend_pid() AS "Pid", current_setting('application_name') AS "ApplicationName",
                   current_setting('lock_timeout') AS "LockTimeout", current_setting('search_path') AS "SearchPath"
            """).SingleAsync();

    public sealed class PoolSession
    {
        public int Pid { get; set; }
        public required string ApplicationName { get; set; }
        public required string LockTimeout { get; set; }
        public required string SearchPath { get; set; }
    }
}
