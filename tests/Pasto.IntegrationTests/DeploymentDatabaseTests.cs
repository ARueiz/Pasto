using Microsoft.EntityFrameworkCore;
using Pasto.Server.Infrastructure.Persistence;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class DeploymentDatabaseTests
{
    [Fact]
    public void RuntimeProductGrants_MatchAllTablesInTheRelationalModel()
    {
        using var context = new PastoDbContext(new DbContextOptionsBuilder<PastoDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=pasto_test")
            .Options);

        var modelTables = context.Model.GetRelationalModel().Tables
            .Select(table => $"{table.Schema ?? "public"}.{table.Name}")
            .Order(StringComparer.Ordinal);

        Assert.Equal(modelTables, DeploymentDatabase.RuntimeProductTables.Order(StringComparer.Ordinal));
    }
}
