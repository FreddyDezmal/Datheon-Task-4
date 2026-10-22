using System.ComponentModel.DataAnnotations;

namespace Storefront.Api.Security;

public static class AuthorizationPolicies
{
    /// <summary>Any signed-in customer, authenticated with a bearer token from /api/account/login.</summary>
    public const string Customer = "Customer";
}

public static class RateLimitPolicies
{
    /// <summary>Account endpoints (register, login, refresh, password reset): slows password guessing.</summary>
    public const string Account = "account";

    /// <summary>Checkout: limits order spam from one client.</summary>
    public const string Checkout = "checkout";
}

/// <summary>Per-client-IP request limits (section "RateLimiting"). Each window is fixed and resets every WindowSeconds.</summary>
public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    [Range(1, 10_000)]
    public int AccountPermitLimit { get; set; } = 10;

    [Range(1, 10_000)]
    public int CheckoutPermitLimit { get; set; } = 30;

    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}
