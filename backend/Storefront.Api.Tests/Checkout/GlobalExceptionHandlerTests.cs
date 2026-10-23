using Storefront.Api.Tests.TestSupport;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Storefront.Api.Exceptions;
using Storefront.Api.Middleware;
using Storefront.Api.Models;

namespace Storefront.Api.Tests.Checkout;

[Trait("Category", TestCategories.Unit)]
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

    [Fact]
    public void Over_limit_request_body_maps_to_413_not_500()
    {
        var problem = GlobalExceptionHandler.Map(
            new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));

        Assert.Equal(413, problem.Status);
        Assert.Equal("REQUEST_TOO_LARGE", problem.Extensions["code"]);
    }

    [Fact]
    public void Other_bad_requests_keep_their_client_error_status()
    {
        var problem = GlobalExceptionHandler.Map(new BadHttpRequestException("Unexpected end of request content."));

        Assert.Equal(400, problem.Status);
        Assert.Equal("BAD_REQUEST", problem.Extensions["code"]);
    }

    [Fact]
    public void Reused_idempotency_key_maps_to_422()
    {
        var problem = GlobalExceptionHandler.Map(new IdempotencyKeyReusedException());

        Assert.Equal(422, problem.Status);
        Assert.Equal("IDEMPOTENCY_KEY_REUSED", problem.Extensions["code"]);
    }

    [Fact]
    public void Database_constraint_violations_map_to_409_without_sql_details()
    {
        var sqlite = new SqliteException("SQLite Error 19: 'UNIQUE constraint failed: Orders.Reference'.", 19, 2067);
        var problem = GlobalExceptionHandler.Map(new DbUpdateException("An error occurred while saving.", sqlite));

        Assert.Equal(409, problem.Status);
        Assert.Equal("CONSTRAINT_CONFLICT", problem.Extensions["code"]);
        Assert.DoesNotContain("Orders", problem.Title);
        Assert.Null(problem.Detail);
    }

    [Fact]
    public void A_busy_database_maps_to_503_so_clients_can_retry()
    {
        var problem = GlobalExceptionHandler.Map(
            new DbUpdateException("save failed", new SqliteException("SQLite Error 5: 'database is locked'.", 5)));

        Assert.Equal(503, problem.Status);
        Assert.Equal("DATABASE_BUSY", problem.Extensions["code"]);
    }

    [Fact]
    public void Other_database_failures_map_to_500_without_internals()
    {
        var problem = GlobalExceptionHandler.Map(
            new SqliteException("SQLite Error 1: 'no such table: Orders'.", 1));

        Assert.Equal(500, problem.Status);
        Assert.Equal("DATABASE_ERROR", problem.Extensions["code"]);
        Assert.DoesNotContain("table", problem.Title);
        Assert.Null(problem.Detail);
    }

    [Fact]
    public void Concurrency_conflicts_map_to_409()
    {
        var problem = GlobalExceptionHandler.Map(new DbUpdateConcurrencyException("row changed"));

        Assert.Equal(409, problem.Status);
        Assert.Equal("CONCURRENCY_CONFLICT", problem.Extensions["code"]);
    }
}
