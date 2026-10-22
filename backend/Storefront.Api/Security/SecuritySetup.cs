using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Storefront.Api.Data;
using Storefront.Api.Data.Entities;

namespace Storefront.Api.Security;

public static class SecuritySetup
{
    /// <summary>
    /// Accounts use ASP.NET Core Identity with its built-in API endpoints (MapIdentityApi):
    /// PBKDF2 password hashing, lockout after repeated failures, and Identity's own bearer tokens
    /// (opaque, signed by the Data Protection key ring). No custom token code.
    /// Bearer tokens rather than cookies: the frontend runs on another origin, and header tokens are
    /// never sent automatically by the browser, so there is no CSRF exposure.
    /// </summary>
    public static IServiceCollection AddStorefrontAuthentication(this IServiceCollection services)
    {
        services.AddIdentityApiEndpoints<StorefrontUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                // No email sender is configured, so accounts can't be confirmed by email (see README).
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddEntityFrameworkStores<StorefrontDbContext>();

        // Identity also registers a cookie scheme. Make sure an API caller is never redirected to a
        // login page: unauthenticated -> 401, forbidden -> 403.
        services.ConfigureApplicationCookie(options =>
        {
            options.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            };
            options.Events.OnRedirectToAccessDenied = context =>
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            };
        });

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.Customer, policy => policy
                .AddAuthenticationSchemes(IdentityConstants.BearerScheme)
                .RequireAuthenticatedUser());

        return services;
    }

    public static IServiceCollection AddStorefrontRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString();
                }

                return ValueTask.CompletedTask;
            };

            options.AddPolicy(RateLimitPolicies.Account, context => FixedWindowPerClient(
                context, RateLimitPolicies.Account, o => o.AccountPermitLimit));
            options.AddPolicy(RateLimitPolicies.Checkout, context => FixedWindowPerClient(
                context, RateLimitPolicies.Checkout, o => o.CheckoutPermitLimit));
        });

        return services;
    }

    // Partitioned by client IP. Behind a reverse proxy, enable forwarded headers first so this is the
    // real client address rather than the proxy's (see README, "Production checklist").
    private static RateLimitPartition<string> FixedWindowPerClient(
        HttpContext context, string policy, Func<RateLimitingOptions, int> permitLimit)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
        var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter($"{policy}:{client}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit(options),
            Window = TimeSpan.FromSeconds(options.WindowSeconds),
            QueueLimit = 0,
        });
    }
}
