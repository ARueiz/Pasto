using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Pasto.Server.Infrastructure.Persistence;

public sealed class PastoDbContextFactory : IDesignTimeDbContextFactory<PastoDbContext>
{
    public PastoDbContext CreateDbContext(string[] args)
    {
        // EF CLI 不啟動 Web host。產生 Migration 不需連 DB，但套用時必須由操作者明確指定。
        // 不提供預設正式 DB／密碼，也不自動讀取本機 .env。
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__Pasto");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("Set ConnectionStrings__Pasto before running EF tools.");
        }

        return new PastoDbContext(new DbContextOptionsBuilder<PastoDbContext>()
            .UseNpgsql(connectionString)
            .Options);
    }
}
