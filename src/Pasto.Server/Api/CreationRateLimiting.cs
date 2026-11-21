using System.Globalization;
using System.Net.Sockets;

namespace Pasto.Server.Api;

internal static class CreationRateLimiting
{
    internal static Task RejectAsync(HttpContext context, int retryAfterSeconds, string code)
        => Rejection(context, retryAfterSeconds, code).ExecuteAsync(context);

    internal static IResult Rejection(HttpContext context, int retryAfterSeconds, string code)
    {
        context.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return ApiProblem.Create(context, 429, code);
    }

    internal static string IpKey(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
            return "unknown";
        if (address.IsIPv4MappedToIPv6)
            return address.MapToIPv4().ToString();
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            Span<byte> bytes = stackalloc byte[16];
            address.TryWriteBytes(bytes, out _);
            return Convert.ToHexString(bytes[..8]);
        }
        return address.ToString();
    }

}
