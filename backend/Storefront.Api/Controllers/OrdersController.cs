using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Storefront.Api.DTOs;
using Storefront.Api.Security;
using Storefront.Api.Services;

namespace Storefront.Api.Controllers;

/// <summary>The signed-in customer's own orders. Requires a bearer token from /api/account/login.</summary>
[ApiController]
[Route("api/orders")]
[Produces("application/json")]
[Authorize(Policy = AuthorizationPolicies.Customer)]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
public sealed class OrdersController(IOrderQueryService orders) : ControllerBase
{
    /// <summary>Lists your most recent orders (newest first, up to 100).</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<OrderSummaryDto>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<OrderSummaryDto>>> GetMine(CancellationToken cancellationToken) =>
        Ok(await orders.GetOrdersForUserAsync(RequiredUserId(), cancellationToken));

    /// <summary>Gets one of your orders. Another customer's order returns 404, not 403, so references can't be probed.</summary>
    [HttpGet("{reference}")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrderDto>> GetOne(string reference, CancellationToken cancellationToken)
    {
        if (reference.Length > Data.Configurations.OrderConfiguration.ReferenceMaxLength)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Order not found.");
        }

        var order = await orders.GetOrderForUserAsync(RequiredUserId(), reference, cancellationToken);
        return order is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: "Order not found.")
            : Ok(order);
    }

    // [Authorize] guarantees an authenticated user; a missing ID claim would be a server misconfiguration.
    private string RequiredUserId() =>
        User.GetUserId() ?? throw new InvalidOperationException("Authenticated user has no NameIdentifier claim.");
}
