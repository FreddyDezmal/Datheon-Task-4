using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Storefront.Api.DTOs;
using Storefront.Api.Security;
using Storefront.Api.Services;

namespace Storefront.Api.Controllers;

[ApiController]
[Route("api/checkout")]
[Produces("application/json")]
public sealed class CheckoutController(ICheckoutService checkoutService) : ControllerBase
{
    /// <summary>
    /// A maximal valid order (50 lines plus customer details) is about 3 KiB; 64 KiB leaves ample
    /// headroom while stopping multi-megabyte bodies before they are deserialised.
    /// </summary>
    public const long MaxRequestBodyBytes = 64 * 1024;

    /// <summary>
    /// Places an order (no payment is taken). Re-validates products and quantities and re-prices the
    /// order from the server-side catalogue, then saves it. Guests may check out; if a bearer token
    /// is sent, the order is linked to that account. A bearer token that is invalid or expired is
    /// rejected with 401 rather than silently placing a guest order, so a client whose session has
    /// lapsed can refresh it and retry instead of losing the link to the account.
    /// Send an <c>Idempotency-Key</c> header to make retries safe: repeating the same request with the
    /// same key returns the original order (with <c>Idempotent-Replayed: true</c>) instead of a new one.
    /// </summary>
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Checkout)]
    [Consumes("application/json")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [ProducesResponseType<CheckoutConfirmationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<CheckoutConfirmationDto>> Checkout(
        [FromBody] CheckoutRequestDto request,
        [FromHeader(Name = IdempotencyKeys.HeaderName)] string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (Request.Headers.Authorization.Count > 0 && User.Identity?.IsAuthenticated != true)
        {
            return Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Your session has expired.",
                detail: "Sign in again, or remove the Authorization header to check out as a guest.");
        }

        var caller = new CheckoutCaller(User.GetUserId(), string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey);
        var result = await checkoutService.CheckoutAsync(request, caller, cancellationToken);

        if (result.IsReplay)
        {
            Response.Headers[IdempotencyKeys.ReplayedHeaderName] = "true";
        }

        return Ok(result.Confirmation);
    }
}
