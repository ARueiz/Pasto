using System.Diagnostics;

namespace Pasto.Server.Api;

internal static class ApiProblem
{
    public static string CorrelationId(HttpContext context)
        => Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

    public static Task WriteAsync(HttpContext context, int status, string code)
        => Create(context, status, code).ExecuteAsync(context);

    internal static IResult Create(HttpContext context, int status, string code, int? retryAfterSeconds = null)
    {
        var extensions = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["correlationId"] = CorrelationId(context)
        };
        if (retryAfterSeconds is int seconds) extensions["retryAfterSeconds"] = seconds;
        return Results.Problem(statusCode: status, title: code, extensions: extensions);
    }
}
