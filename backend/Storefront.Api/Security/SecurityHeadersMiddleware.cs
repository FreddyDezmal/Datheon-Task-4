namespace Storefront.Api.Security;

/// <summary>
/// Adds defensive headers to every response. The API serves JSON only, so its content policy
/// forbids loading anything; Swagger UI (Development only) needs scripts and is left without a CSP.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        var isSwagger = context.Request.Path.StartsWithSegments("/swagger");
        var isPrivate = IsPrivate(context.Request.Path);
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Cross-Origin-Resource-Policy"] = "same-site";
            if (!isSwagger)
            {
                headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            }

            // Tokens, order history and order confirmations (names, emails): never stored by a browser
            // or an intermediary cache (Security-Audit.md, SA-09).
            if (isPrivate)
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        });

        return next(context);
    }

    /// <summary>Responses that can carry tokens or personal data.</summary>
    internal static bool IsPrivate(PathString path) =>
        path.StartsWithSegments("/api/account") || path.StartsWithSegments("/api/orders") || path.StartsWithSegments("/api/checkout");
}
