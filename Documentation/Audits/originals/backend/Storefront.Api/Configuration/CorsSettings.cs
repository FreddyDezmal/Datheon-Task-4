namespace Storefront.Api.Configuration;

/// <summary>Origins allowed to call the API from a browser (section "Cors").</summary>
public sealed class CorsSettings
{
    public const string SectionName = "Cors";
    public const string PolicyName = "Frontend";

    public string[] AllowedOrigins { get; set; } = ["http://localhost:5173"];
}
