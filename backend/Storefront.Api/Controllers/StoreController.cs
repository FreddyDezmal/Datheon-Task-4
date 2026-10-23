using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Storefront.Api.Configuration;
using Storefront.Api.DTOs;
using Storefront.Api.Services;

namespace Storefront.Api.Controllers;

[ApiController]
[Route("api/store")]
public sealed class StoreController(IOptions<PricingOptions> pricingOptions) : ControllerBase
{
    /// <summary>Currency and pricing rules, so the cart preview uses the same rules as checkout.</summary>
    [HttpGet("config")]
    [ProducesResponseType<StoreConfigDto>(StatusCodes.Status200OK)]
    public ActionResult<StoreConfigDto> GetConfig()
    {
        var o = pricingOptions.Value;
        return Ok(new StoreConfigDto(
            o.Currency,
            o.Locale,
            o.TaxRate,
            (int)(o.TaxRate * 10_000m),
            o.FlatShippingFee,
            PricingCalculator.ToMinorUnits(o.FlatShippingFee),
            o.FreeShippingThreshold,
            PricingCalculator.ToMinorUnits(o.FreeShippingThreshold),
            o.MaxQuantityPerLine,
            o.MaxLineItems));
    }
}
