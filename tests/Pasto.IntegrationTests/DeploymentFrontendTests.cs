using System.Net;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class DeploymentFrontendTests
{
    [Fact]
    public async Task FrontendFilesAndApplicationEndpointsUseTheirOwnCachePolicy()
    {
        var webRoot = Directory.CreateTempSubdirectory("pasto-wwwroot-");
        try
        {
            webRoot.CreateSubdirectory("assets");
            await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "index.html"), "<html>Pasto</html>");
            await File.WriteAllTextAsync(Path.Combine(webRoot.FullName, "assets", "index-ab12CD_3.js"), "export {};");
            await using var factory = new PastoApiFactory("Host=127.0.0.1;Port=1;Database=pasto_test;Username=test",
                new Dictionary<string, string?> { ["webroot"] = webRoot.FullName });
            using var client = factory.CreateClient();

            foreach (var path in new[] { "/", "/index.html", "/r/ABC123", "/r/ABC123/" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("<html>Pasto</html>", await response.Content.ReadAsStringAsync());
                Assert.True(response.Headers.CacheControl?.NoCache);
                Assert.False(response.Headers.CacheControl?.NoStore);
            }

            using var asset = await client.GetAsync("/assets/index-ab12CD_3.js");
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.True(asset.Headers.CacheControl?.Public);
            Assert.Equal(TimeSpan.FromDays(365), asset.Headers.CacheControl?.MaxAge);
            Assert.Contains(asset.Headers.CacheControl!.Extensions, extension => extension.Name == "immutable");
            Assert.False(asset.Headers.CacheControl.NoStore);

            foreach (var path in new[] { "/api/missing", "/hubs/missing", "/health/missing", "/assets/missing.js", "/r/abc.js" })
            {
                using var response = await client.GetAsync(path);
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
                Assert.True(response.Headers.CacheControl?.NoStore);
            }
        }
        finally
        {
            webRoot.Delete(recursive: true);
        }
    }
}
