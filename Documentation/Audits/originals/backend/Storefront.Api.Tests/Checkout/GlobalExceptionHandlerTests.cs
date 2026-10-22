using Microsoft.AspNetCore.Mvc;
using Storefront.Api.Exceptions;
using Storefront.Api.Middleware;
using Storefront.Api.Models;

namespace Storefront.Api.Tests.Checkout;

public class GlobalExceptionHandlerTests
{
    [Fact]
    public void Validation_errors_map_to_400_with_field_errors()
    {
        var problem = GlobalExceptionHandler.Map(new RequestValidationException(
            new Dictionary<string, string[]> { ["items[0].quantity"] = ["bad"] }));

        var validation = Assert.IsType<ValidationProblemDetails>(problem);
        Assert.Equal(400, validation.Status);
        Assert.Equal("VALIDATION_FAILED", validation.Extensions["code"]);
        Assert.Contains("items[0].quantity", validation.Errors.Keys);
    }

    [Fact]
    public void Price_change_maps_to_409_with_quote()
    {
        var quote = new PriceBreakdown([new PricedLine(1, "x", 10m, 1, 10m)], 10m, 0.80m, 5.99m, 16.79m, "USD");

        var problem = GlobalExceptionHandler.Map(new PriceChangedException(1m, quote));

        Assert.Equal(409, problem.Status);
        Assert.Equal("PRICE_CHANGED", problem.Extensions["code"]);
        Assert.NotNull(problem.Extensions["quote"]);
    }

    [Theory]
    [InlineData(UpstreamFailureKind.Timeout, 504, "UPSTREAM_TIMEOUT")]
    [InlineData(UpstreamFailureKind.Unavailable, 502, "UPSTREAM_UNAVAILABLE")]
    [InlineData(UpstreamFailureKind.MalformedResponse, 502, "UPSTREAM_MALFORMED")]
    public void Upstream_failures_map_to_gateway_errors(UpstreamFailureKind kind, int status, string code)
    {
        var problem = GlobalExceptionHandler.Map(new UpstreamServiceException(kind, "x"));

        Assert.Equal(status, problem.Status);
        Assert.Equal(code, problem.Extensions["code"]);
    }

    [Fact]
    public void Unavailable_products_map_to_422_with_ids()
    {
        var problem = GlobalExceptionHandler.Map(new ProductsUnavailableException([7]));

        Assert.Equal(422, problem.Status);
        Assert.Equal("PRODUCTS_UNAVAILABLE", problem.Extensions["code"]);
    }

    [Fact]
    public void Unexpected_exceptions_do_not_leak_details()
    {
        var problem = GlobalExceptionHandler.Map(new InvalidOperationException("secret connection string"));

        Assert.Equal(500, problem.Status);
        Assert.Null(problem.Detail);
        Assert.DoesNotContain("secret", problem.Title);
    }
}
