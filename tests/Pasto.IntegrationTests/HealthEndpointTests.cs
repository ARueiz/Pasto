using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pasto.Server.Application.Rooms;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task DatabaseUnavailable_OnlyReadinessFailsAndResponsesContainNoDetails()
    {
        var connection = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = 1, Database = "pasto_test",
            Username = "diagnostic-test", Password = "health-secret"
        };
        await using var factory = new PastoApiFactory(connection.ConnectionString);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('A', 22));
        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");
        using var cleanup = await client.GetAsync("/health/cleanup");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("Unhealthy", await ready.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, cleanup.StatusCode);
        Assert.False((await cleanup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task WorkerFailures_AreVisibleSeparatelyAndDoNotGateHealthyDatabase()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await using var factory = new PastoApiFactory(database.ConnectionString);
        using var client = factory.CreateClient();
        var status = factory.Services.GetRequiredService<RoomCleanupStatus>();
        var lastSuccess = DateTimeOffset.Parse("2026-11-21T02:00:00Z", System.Globalization.CultureInfo.InvariantCulture);
        status.RecordSuccess(lastSuccess);
        status.RecordFailure();
        status.RecordFailure();

        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");
        var cleanup = await client.GetFromJsonAsync<JsonElement>("/health/cleanup");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(2, cleanup.GetProperty("consecutiveFailures").GetInt32());
        Assert.Equal(lastSuccess, cleanup.GetProperty("lastSuccessfulAt").GetDateTimeOffset());

        status.RecordSuccess(lastSuccess.AddMinutes(1));
        cleanup = await client.GetFromJsonAsync<JsonElement>("/health/cleanup");
        Assert.Equal(0, cleanup.GetProperty("consecutiveFailures").GetInt32());
        Assert.Equal(lastSuccess.AddMinutes(1), cleanup.GetProperty("lastSuccessfulAt").GetDateTimeOffset());
    }
}
