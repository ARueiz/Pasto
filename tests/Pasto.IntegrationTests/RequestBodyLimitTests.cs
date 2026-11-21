using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Pasto.Server.Application.Rooms;
using Pasto.Server.Domain;
using Xunit;

namespace Pasto.IntegrationTests;

public sealed class RequestBodyLimitTests
{
    [Theory]
    [InlineData(1023, false, 400)]
    [InlineData(1024, false, 400)]
    [InlineData(1025, false, 413)]
    [InlineData(512, true, 400)]
    // Kestrel 10.0.13 也計算 chunk framing；此處保留較保守的原生上限，不調高它。
    [InlineData(1023, true, 413)]
    [InlineData(1024, true, 413)]
    [InlineData(1025, true, 413)]
    public async Task RealKestrel_EnforcesBodyLimitBeforeJoinStore(int bytes, bool chunked, int expectedStatus)
    {
        var store = new UnexpectedJoinStore();
        await using var factory = new PastoApiFactory("Host=127.0.0.1;Database=pasto_test", configureServices: services =>
        {
            services.RemoveAll<IRoomJoinStore>();
            services.AddScoped<IRoomJoinStore>(_ => store);
        });
        // TestServer 不套用 Kestrel 上限；此處只監聽本機動態埠並真正透過 TCP 發送。
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        Assert.Equal(32768, factory.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value.Limits.MaxRequestBodySize);
        const string json = "{\"code\":\"bad\"}";
        var payload = Encoding.UTF8.GetBytes(json + new string(' ', bytes - Encoding.UTF8.GetByteCount(json)));
        Assert.Equal(bytes, payload.Length);
        using HttpContent content = chunked ? new StreamingContent(payload) : new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/rooms/join")
        {
            Content = content,
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        if (chunked) request.Headers.TransferEncodingChunked = true;
        using var response = await client.SendAsync(request);
        Assert.Equal(expectedStatus, (int)response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expectedStatus == 413 ? "RequestBodyTooLarge" : "InvalidRequest", body.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("correlationId").GetString()));
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealKestrel_RejectsOversizedCreateBodyBeforeStorage(bool chunked)
    {
        // No working DB is needed. A creation that reaches storage would return 503 instead.
        await using var factory = new PastoApiFactory("Host=127.0.0.1;Port=1;Database=pasto_test");
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        using var client = factory.CreateClient();
        var payload = new byte[1025];
        using HttpContent content = chunked ? new StreamingContent(payload) : new ByteArrayContent(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/rooms")
        {
            Content = content,
            Version = HttpVersion.Version11,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact
        };
        if (chunked) request.Headers.TransferEncodingChunked = true;
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("RequestBodyTooLarge", body.RootElement.GetProperty("code").GetString());
    }

    private sealed class UnexpectedJoinStore : IRoomJoinStore
    {
        public int Calls { get; private set; }
        public Task<(Room Room, Guid ParticipantId)> JoinAsync(string code, byte[] credentialHash, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Body must be rejected before join storage.");
        }
    }

    private sealed class StreamingContent(byte[] payload) : HttpContent
    {
        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => await stream.WriteAsync(payload);
    }
}
