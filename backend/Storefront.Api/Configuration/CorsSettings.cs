namespace Storefront.Api.Configuration;

/// <summary>Origins allowed to call the API from a browser (section "Cors").</summary>
public sealed class CorsSettings
{
    public const string SectionName = "Cors";
    public const string PolicyName = "Frontend";

    /// <summary>
    /// Deliberately empty by default. The configuration binder appends configured array values
    /// to an initialised array, so a default here could never be removed by configuration
    /// (a production allowlist would still admit it). Defaults live in appsettings.json instead.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];
}
