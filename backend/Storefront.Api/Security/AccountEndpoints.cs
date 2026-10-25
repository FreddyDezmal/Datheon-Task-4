using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Storefront.Api.Data.Entities;

namespace Storefront.Api.Security;

/// <summary>
/// The account endpoints under /api/account: only what the storefront uses.
///
/// These replace Identity's MapIdentityApi, which also exposed account management (2FA, password and
/// email changes) and email-based flows the app can't support: there is no email sender, and the UI
/// has no 2FA. A stolen access token could switch 2FA on and lock the owner out for good
/// (Security-Audit.md, SA-01). Request and response shapes are unchanged for the endpoints kept.
///
///   POST /register      { email, password }  -> 200, or 400 with Identity error codes
///   POST /login         { email, password }  -> 200 { tokenType, accessToken, expiresIn, refreshToken }, or 401 "Failed"
///   POST /refresh       { refreshToken }     -> 200 (new tokens; the old refresh token is used up), or 401
///   POST /logout        { refreshToken }     -> 204 (the session ends on the server)
///   GET  /manage/info   (bearer)             -> 200 { email, isEmailConfirmed }
///
/// Sign-in is bearer-token only. There is no cookie mode (SA-08): `?useCookies=true` is ignored.
/// Every sign-in is a server-side session (SessionService, SA-02), and repeated failures are throttled
/// per account (SignInThrottle, SA-03 and SA-06) instead of locking the account for everyone.
/// </summary>
public static class AccountEndpoints
{
    public sealed record RegisterRequest(string? Email, string? Password);

    public sealed record LoginRequest(string? Email, string? Password);

    public sealed record RefreshRequest(string? RefreshToken);

    public sealed record LogoutRequest(string? RefreshToken);

    public sealed record InfoResponse(string Email, bool IsEmailConfirmed);

    /// <summary>
    /// An account request is a few hundred bytes; anything far larger is refused with 413 before it is
    /// read (Security-Audit.md, SA-10). Applied by <see cref="UseAccountRequestLimits"/>.
    /// </summary>
    public const long MaxRequestBodyBytes = 16 * 1024;

    /// <summary>Caps request bodies under /api/account (minimal APIs have no per-endpoint size attribute).</summary>
    public static IApplicationBuilder UseAccountRequestLimits(this IApplicationBuilder app) =>
        app.Use((context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/api/account")
                && context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            {
                limit.MaxRequestBodySize = MaxRequestBodyBytes;
            }

            return next(context);
        });

    private static readonly EmailAddressAttribute EmailValidator = new();

    public static RouteGroupBuilder MapStorefrontAccount(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/account")
            .WithTags("Account")
            .RequireRateLimiting(RateLimitPolicies.Account);

        group.MapPost("/register", RegisterAsync);
        group.MapPost("/login", LoginAsync);
        group.MapPost("/refresh", RefreshAsync);
        group.MapPost("/logout", LogoutAsync);
        group.MapGet("/manage/info", InfoAsync).RequireAuthorization(AuthorizationPolicies.Customer);
        return group;
    }

    private static async Task<Results<Ok, ValidationProblem>> RegisterAsync(
        RegisterRequest request, UserManager<StorefrontUser> userManager)
    {
        var email = request.Email?.Trim();
        if (string.IsNullOrEmpty(email) || !EmailValidator.IsValid(email))
        {
            return Validation(IdentityResult.Failed(userManager.ErrorDescriber.InvalidEmail(email)));
        }

        var user = new StorefrontUser();
        await userManager.SetUserNameAsync(user, email);
        await userManager.SetEmailAsync(user, email);
        var result = await userManager.CreateAsync(user, request.Password ?? string.Empty);
        return result.Succeeded ? TypedResults.Ok() : Validation(result);
    }

    private static async Task<Results<SignInHttpResult, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        HttpContext http,
        UserManager<StorefrontUser> userManager,
        SignInManager<StorefrontUser> signInManager,
        SessionService sessions,
        SignInThrottle throttle,
        ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(AccountEndpoints).FullName!);
        var email = request.Email?.Trim() ?? string.Empty;
        var password = request.Password ?? string.Empty;
        var account = SignInThrottle.AccountKey(email);
        var client = ClientPartition.For(http);

        // Checked before the password, and the same for emails with no account.
        if (throttle.RetryAfter(account, client) is { } wait)
        {
            http.Response.Headers.RetryAfter = ((int)Math.Ceiling(wait.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            logger.LogWarning("Sign-in refused for account {AccountKey} from {Client}: too many failed attempts.", account, client);
            return TypedResults.Problem(
                statusCode: StatusCodes.Status429TooManyRequests,
                title: "Too many failed sign-in attempts.",
                detail: "Wait a few minutes before trying again.");
        }

        var user = email.Length == 0 ? null : await userManager.FindByEmailAsync(email);

        // An unknown email still costs one password hash, so response time doesn't reveal which
        // emails have accounts (SA-05). Two-factor sign-in isn't supported (no UI), so the 2FA flag
        // isn't consulted, and Identity's lockout isn't used: the throttle above replaces it.
        var valid = user is null
            ? VerifyAgainstDummyHash(userManager, password)
            : await userManager.CheckPasswordAsync(user, password);
        if (!valid || user is null)
        {
            throttle.RecordFailure(account, client);
            logger.LogInformation("Sign-in failed for account {AccountKey} from {Client}.", account, client);
            return Failed();
        }

        throttle.RecordSuccess(account, client);
        var session = await sessions.StartAsync(user, http.RequestAborted);
        var principal = SessionClaims.WithSession(await signInManager.CreateUserPrincipalAsync(user), session.Id, session.CurrentTokenId);

        // Written by the bearer handler as { tokenType, accessToken, expiresIn, refreshToken }.
        return TypedResults.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
    }

    private static async Task<Results<SignInHttpResult, ChallengeHttpResult>> RefreshAsync(
        RefreshRequest request,
        HttpContext http,
        IOptionsMonitor<BearerTokenOptions> bearerOptions,
        SignInManager<StorefrontUser> signInManager,
        SessionService sessions,
        TimeProvider timeProvider)
    {
        var protector = bearerOptions.Get(IdentityConstants.BearerScheme).RefreshTokenProtector;
        var ticket = string.IsNullOrEmpty(request.RefreshToken) ? null : protector.Unprotect(request.RefreshToken);
        if (ticket?.Properties.ExpiresUtc is not { } expiresUtc || timeProvider.GetUtcNow() >= expiresUtc)
        {
            return TypedResults.Challenge();
        }

        // Tokens from before server-side sessions carry no session: they're refused, so those shoppers
        // sign in again once.
        var sessionId = ticket.Principal.FindFirstValue(SessionClaims.SessionId);
        var tokenId = ticket.Principal.FindFirstValue(SessionClaims.RefreshTokenId);
        if (sessionId is null || tokenId is null)
        {
            return TypedResults.Challenge();
        }

        // Each refresh token can be exchanged once (SessionService.RotateAsync). The session is refused
        // if it has ended, if the account's password changed, or if this token was already used.
        var (next, user, _) = await sessions.RotateAsync(sessionId, tokenId, http.RequestAborted);
        if (next is null || user is null)
        {
            return TypedResults.Challenge();
        }

        var principal = SessionClaims.WithSession(await signInManager.CreateUserPrincipalAsync(user), sessionId, next);
        return TypedResults.SignIn(principal, authenticationScheme: IdentityConstants.BearerScheme);
    }

    /// <summary>
    /// Ends the session the refresh token (or the bearer token) belongs to. Always 204: whether
    /// anything was revoked isn't revealed, and signing out twice is harmless.
    /// </summary>
    private static async Task<NoContent> LogoutAsync(
        LogoutRequest? request,
        HttpContext http,
        IOptionsMonitor<BearerTokenOptions> bearerOptions,
        SessionService sessions)
    {
        var protector = bearerOptions.Get(IdentityConstants.BearerScheme).RefreshTokenProtector;
        var ticket = string.IsNullOrEmpty(request?.RefreshToken) ? null : protector.Unprotect(request.RefreshToken);
        var sessionId = ticket?.Principal.FindFirstValue(SessionClaims.SessionId)
                        ?? http.User.FindFirstValue(SessionClaims.SessionId);
        if (sessionId is not null)
        {
            await sessions.RevokeAsync(sessionId, "signed-out", http.RequestAborted);
        }

        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<InfoResponse>, NotFound>> InfoAsync(
        ClaimsPrincipal principal, UserManager<StorefrontUser> userManager)
    {
        if (await userManager.GetUserAsync(principal) is not { } user)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(new InfoResponse(
            await userManager.GetEmailAsync(user) ?? string.Empty,
            await userManager.IsEmailConfirmedAsync(user)));
    }

    /// <summary>The same answer for every failure (unknown email or wrong password), so they look alike.</summary>
    private static ProblemHttpResult Failed() =>
        TypedResults.Problem(detail: "Failed", statusCode: StatusCodes.Status401Unauthorized);

    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHasher<StorefrontUser>().HashPassword(new StorefrontUser(), SessionClaims.NewId()));

    private static bool VerifyAgainstDummyHash(UserManager<StorefrontUser> userManager, string password)
    {
        _ = userManager.PasswordHasher.VerifyHashedPassword(new StorefrontUser(), DummyHash.Value, password);
        return false;
    }

    private static ValidationProblem Validation(IdentityResult result) =>
        TypedResults.ValidationProblem(result.Errors
            .GroupBy(e => e.Code)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Description).ToArray()));
}
