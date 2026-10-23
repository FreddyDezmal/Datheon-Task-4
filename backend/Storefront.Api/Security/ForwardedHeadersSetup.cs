using System.Text.RegularExpressions;
using Microsoft.AspNetCore.HttpOverrides;

namespace Storefront.Api.Security;

/// <summary>
/// Settings (section "ForwardedHeaders") for running behind a reverse proxy such as Render's.
///
/// Behind a proxy every request arrives from the proxy's address, so per-client rate limits become one
/// shared limit and the API can't tell that the original request used HTTPS. When enabled, the client
/// address is taken from a header the proxy writes, and the scheme from X-Forwarded-Proto.
///
/// Off by default, because a forwarded header is only trustworthy when (a) the app can be reached
/// only through the proxy, and (b) the value is read from a position the proxy writes. A client can
/// put anything in X-Forwarded-For; proxies append to it, so only the right-most entries are the
/// proxies' own. <see cref="TrustedProxyHops"/> says how many of those right-most entries to walk back
/// through. Which header and how many hops a platform needs must be measured on that platform
/// (Documentation/Deployment.md, "Client addresses behind the proxy").
/// </summary>
public sealed partial class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    public bool Enabled { get; set; }

    /// <summary>The header holding the client address, e.g. X-Forwarded-For or True-Client-IP.</summary>
    public string ClientAddressHeader { get; set; } = ForwardedHeadersDefaults.XForwardedForHeaderName;

    /// <summary>How many right-most entries of that header come from proxies you trust (1–5).</summary>
    public int TrustedProxyHops { get; set; } = 1;

    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HeaderName();

    internal static ForwardedHeadersSettings Read(IConfiguration configuration)
    {
        var settings = configuration.GetSection(SectionName).Get<ForwardedHeadersSettings>() ?? new ForwardedHeadersSettings();
        if (!settings.Enabled)
        {
            return settings;
        }

        if (settings.TrustedProxyHops is < 1 or > 5)
        {
            throw new InvalidOperationException("ForwardedHeaders:TrustedProxyHops must be between 1 and 5.");
        }

        if (!HeaderName().IsMatch(settings.ClientAddressHeader ?? string.Empty))
        {
            throw new InvalidOperationException("ForwardedHeaders:ClientAddressHeader must be a plain header name such as X-Forwarded-For.");
        }

        return settings;
    }

    internal ForwardedHeadersOptions ToOptions()
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardedForHeaderName = ClientAddressHeader,
            ForwardLimit = TrustedProxyHops,
            // Not set: X-Forwarded-Host. Host filtering (AllowedHosts) keeps checking the real Host header.
        };

        // The proxy's own addresses aren't published, so any immediate peer is accepted. That is only
        // safe because enabling this setting asserts the app is reachable through the proxy alone.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        return options;
    }
}

public static class ForwardedHeadersSetup
{
    /// <summary>Applies forwarded headers first in the pipeline, so everything after (rate limiting, HSTS, HTTPS redirection, logs) sees the client.</summary>
    public static WebApplication UseStorefrontForwardedHeaders(this WebApplication app, ForwardedHeadersSettings settings)
    {
        if (settings.Enabled)
        {
            app.UseForwardedHeaders(settings.ToOptions());
            app.Logger.LogInformation(
                "Forwarded headers enabled: client address from {Header} ({Hops} trusted proxy hop(s)), scheme from X-Forwarded-Proto.",
                settings.ClientAddressHeader, settings.TrustedProxyHops);
        }

        return app;
    }
}
