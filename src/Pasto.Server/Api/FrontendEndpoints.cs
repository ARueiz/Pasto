using System.Text.RegularExpressions;

namespace Pasto.Server.Api;

internal static partial class FrontendEndpoints
{
    public static void UsePastoFrontend(this WebApplication app, bool required)
    {
        if (!app.Environment.WebRootFileProvider.GetFileInfo("index.html").Exists)
        {
            if (required)
                throw new InvalidOperationException("The deployed frontend is missing wwwroot/index.html.");
            return;
        }

        var options = new StaticFileOptions
        {
            OnPrepareResponse = context =>
            {
                var path = context.Context.Request.Path;
                context.Context.Response.Headers.CacheControl = path.StartsWithSegments("/assets")
                    && FingerprintedAsset().IsMatch(context.File.Name)
                    ? "public, max-age=31536000, immutable"
                    : "no-cache";
            }
        };
        app.UseStaticFiles(options);
        app.MapFallbackToFile("/", "index.html", options);
        app.MapFallbackToFile("/r/{code:length(6):nonfile}", "index.html", options);
    }

    [GeneratedRegex(@"-[A-Za-z0-9_-]{8}\.[^.]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex FingerprintedAsset();
}
