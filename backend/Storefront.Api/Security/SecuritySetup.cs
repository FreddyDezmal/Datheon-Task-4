using System.Threading.RateLimiting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Storefront.Api.Data;
using Storefront.Api.Data.Entities;

namespace Storefront.Api.Security;

public static class SecuritySetup
{
    public const string DataProtectionApplicationName = "Storefront.Api";

    /// <summary>
    /// Accounts use ASP.NET Core Identity: PBKDF2 password hashing, its user store, and its own bearer
    /// tokens (opaque, signed by the Data Protection key ring). The account endpoints themselves are
    /// mapped explicitly in <see cref="AccountEndpoints"/>, with only what the storefront uses.
    /// Bearer tokens rather than cookies: the frontend runs on another origin, and header tokens are
    /// never sent automatically by the browser, so there is no CSRF exposure.
    /// </summary>
    public static IServiceCollection AddStorefrontAuthentication(this IServiceCollection services, DataProtectionCertificates certificates)
    {
        // The key ring lives in the app database (DataProtectionKeys), not in the container's home
        // folder, which is replaced on every deploy and would sign every shopper out. A fixed
        // application name lets every instance share the keys, whatever folder it runs from.
        var dataProtection = services.AddDataProtection()
            .SetApplicationName(DataProtectionApplicationName)
            .PersistKeysToDbContext<StorefrontDbContext>();

        // With a certificate, every new key is encrypted before it is written to the database
        // (DataProtectionSettings). Keys written before encryption was enabled stay readable and keep
        // working until they expire; previous certificates decrypt keys from before a rotation.
        if (certificates.Current is not null)
        {
            dataProtection.ProtectKeysWithCertificate(certificates.Current);
            if (certificates.Previous.Count > 0)
            {
                dataProtection.UnprotectKeysWithAnyCertificate([.. certificates.Previous]);
            }
        }

        // Bearer tokens are the only way to authenticate (Identity's opaque tokens, protected by the
        // Data Protection keys above). No cookie scheme is registered, so a sign-in cookie can't exist
        // and no request is ever authenticated by something the browser sends on its own (SA-08).
        // Every bearer token is checked against its server-side session before the request counts as
        // authenticated, so a session ended by sign-out, refresh-token reuse or a password change stops
        // working at once (SessionValidation, SA-02).
        services.AddAuthentication(IdentityConstants.BearerScheme)
            .AddBearerToken(IdentityConstants.BearerScheme, options =>
                options.Events.OnMessageReceived = SessionValidation.OnMessageReceivedAsync);

        services.AddOptions<SessionOptions>()
            .BindConfiguration(SessionOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddScoped<SessionService>();

        services.AddOptions<SignInProtectionOptions>()
            .BindConfiguration(SignInProtectionOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddSingleton<SignInThrottle>();

        // Identity's user store, password hashing and sign-in checks, without its UI or API-endpoint
        // bundles. The endpoints the storefront uses are mapped in AccountEndpoints.
        services.AddIdentityCore<StorefrontUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
                // Identity's lockout locked the account for everyone after 5 failures, so anyone who knew
                // an email could keep its owner out (SA-06). SignInThrottle limits guessing per account and client instead.
                options.Lockout.AllowedForNewUsers = false;
                // No email sender is configured, so accounts can't be confirmed by email (see README).
                options.SignIn.RequireConfirmedAccount = false;
            })
            .AddSignInManager()
            .AddEntityFrameworkStores<StorefrontDbContext>();

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthorizationPolicies.Customer, policy => policy
                .AddAuthenticationSchemes(IdentityConstants.BearerScheme)
                .RequireAuthenticatedUser());

        return services;
    }

    /// <summary>Says at startup whether the keys are encrypted, and warns before the certificate expires.</summary>
    public static void LogDataProtectionKeyEncryption(this WebApplication app, DataProtectionCertificates certificates)
    {
        if (certificates.Current is not { } current)
        {
            if (!app.Environment.IsDevelopment())
            {
                app.Logger.LogWarning(
                    "DataProtection:AllowUnencryptedKeys is set: sign-in keys are stored unencrypted in the database.");
            }

            return;
        }

        app.Logger.LogInformation(
            "Sign-in keys are encrypted with certificate {Thumbprint} (expires {NotAfter:yyyy-MM-dd}); {PreviousCount} previous certificate(s) for decryption.",
            current.Thumbprint, current.NotAfter.ToUniversalTime(), certificates.Previous.Count);

        // Expiry doesn't stop the certificate from working (key encryption doesn't check it), but it is
        // the reminder to rotate.
        var remaining = current.NotAfter.ToUniversalTime() - DateTime.UtcNow;
        if (remaining < TimeSpan.FromDays(30))
        {
            app.Logger.LogWarning(
                "The Data Protection certificate {Thumbprint} expires on {NotAfter:yyyy-MM-dd}. Rotate it (Documentation/Deployment.md).",
                current.Thumbprint, current.NotAfter.ToUniversalTime());
        }
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

    // Partitioned by client: an IPv4 address, or an IPv6 /64 network (ClientPartition). Behind a reverse
    // proxy, enable forwarded headers first so this is the real client rather than the proxy (Deployment.md).
    private static RateLimitPartition<string> FixedWindowPerClient(
        HttpContext context, string policy, Func<RateLimitingOptions, int> permitLimit)
    {
        var options = context.RequestServices.GetRequiredService<IOptions<RateLimitingOptions>>().Value;
        var client = ClientPartition.For(context);
        return RateLimitPartition.GetFixedWindowLimiter($"{policy}:{client}", _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit(options),
            Window = TimeSpan.FromSeconds(options.WindowSeconds),
            QueueLimit = 0,
        });
    }
}
