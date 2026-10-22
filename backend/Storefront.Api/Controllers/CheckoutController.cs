using Microsoft.AspNetCore.Mvc;
using Storefront.Api.DTOs;
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
    /// Simulated checkout. Re-validates products and quantities and re-prices the order from
    /// the server-side catalogue. No payment is taken and no order is stored.
    /// </summary>
    [HttpPost]
    [Consumes("application/json")]
    [RequestSizeLimit(MaxRequestBodyBytes)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType<CheckoutConfirmationDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<CheckoutConfirmationDto>> Checkout(
        [FromBody] CheckoutRequestDto request, CancellationToken cancellationToken) =>
        Ok(await checkoutService.CheckoutAsync(request, cancellationToken));
}
