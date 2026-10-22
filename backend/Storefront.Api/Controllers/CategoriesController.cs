using Microsoft.AspNetCore.Mvc;
using Storefront.Api.DTOs;
using Storefront.Api.Services;

namespace Storefront.Api.Controllers;

[ApiController]
[Route("api/categories")]
[Produces("application/json")]
public sealed class CategoriesController(IProductCatalogService catalogService) : ControllerBase
{
    /// <summary>
    /// Lists normalised categories with product counts. Grouping entries (e.g. "clothing")
    /// have <c>isGroup = true</c>; their children reference them through <c>parentSlug</c>.
    /// </summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CategoryDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status502BadGateway)]
    public async Task<ActionResult<IReadOnlyList<CategoryDto>>> GetCategories(CancellationToken cancellationToken) =>
        Ok(await catalogService.GetCategoriesAsync(cancellationToken));
}
