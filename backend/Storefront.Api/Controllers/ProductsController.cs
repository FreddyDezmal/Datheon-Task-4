using Microsoft.AspNetCore.Mvc;
using Storefront.Api.DTOs;
using Storefront.Api.Services;

namespace Storefront.Api.Controllers;

[ApiController]
[Route("api/products")]
public sealed class ProductsController(IProductCatalogService catalogService) : ControllerBase
{
    /// <summary>Lists products with optional search, category, price range and sort.</summary>
    [HttpGet]
    [ProducesResponseType<ProductListResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status504GatewayTimeout)]
    public async Task<ActionResult<ProductListResponse>> GetProducts(
        [FromQuery] ProductQueryParameters query, CancellationToken cancellationToken) =>
        Ok(await catalogService.GetProductsAsync(query, cancellationToken));

    /// <summary>Gets a single product by its ID.</summary>
    [HttpGet("{id:int}")]
    [ProducesResponseType<ProductDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ProductDto>> GetProduct(int id, CancellationToken cancellationToken)
    {
        if (id <= 0)
        {
            ModelState.AddModelError(nameof(id), "Product ID must be a positive integer.");
            return ValidationProblem(ModelState);
        }

        var product = await catalogService.GetProductAsync(id, cancellationToken);
        return product is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: $"Product {id} was not found.")
            : Ok(product);
    }
}
