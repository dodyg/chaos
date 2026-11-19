using Microsoft.AspNetCore.Http.Features;

namespace Chaos.Server.Web;

internal static class SecurityHeaders
{
    /// <summary>Sign-in pages and API responses are never cached or framed. The app shell loads same-origin assets.</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (http, next) =>
    {
        var path = http.Request.Path;
        var isApp = path.StartsWithSegments(WebSession.AppPath);
        var isBundledAsset = path.StartsWithSegments("/app/assets");
        if (HttpMethods.IsPost(http.Request.Method) && path.StartsWithSegments(WebSession.ApiPath) &&
            http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } size)
        {
            size.MaxRequestBodySize = 8192;
        }

        http.Response.OnStarting(() =>
        {
            var headers = http.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = isApp
                ? "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; worker-src 'self'; manifest-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'"
                : "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'";
            if (!path.StartsWithSegments("/health"))
            {
                headers.CacheControl = isBundledAsset && http.Response.StatusCode == StatusCodes.Status200OK
                    ? "public, max-age=31536000, immutable"
                    : "no-store";
            }

            return Task.CompletedTask;
        });
        await next();
        });
}
