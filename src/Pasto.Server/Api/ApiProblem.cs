using System.Diagnostics;

namespace Pasto.Server.Api;

internal static class ApiProblem
{
    public static string CorrelationId(HttpContext context)
        => Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

    public static Task WriteAsync(HttpContext context, int status, string code)
        => Create(context, status, code).ExecuteAsync(context);

    internal static IResult Create(HttpContext context, int status, string code)
        => Results.Problem(statusCode: status, title: code, extensions: new Dictionary<string, object?>
        {
            ["code"] = code,
            ["correlationId"] = CorrelationId(context)
        });
}
