using Storefront.Api.Exceptions;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Checkout;

public class CheckoutServiceTests
{
    private readonly InMemoryFakeStoreClient _client = new(TestData.Catalogue());

    [Fact]
    public async Task Prices_the_order_from_the_catalogue()
    {
        var service = TestData.CheckoutService(_client);
        var request = CheckoutRequestValidatorTests.ValidRequest((2, 2), (19, 1));

        var confirmation = await service.CheckoutAsync(request, CancellationToken.None);

        // 22.30 × 2 + 7.95 = 52.55 → free shipping; tax 4.204 → 4.20.
        Assert.Equal(52.55m, confirmation.Totals.Subtotal);
        Assert.Equal(4.20m, confirmation.Totals.Tax);
        Assert.Equal(0m, confirmation.Totals.Shipping);
        Assert.Equal(56.75m, confirmation.Totals.Total);
        Assert.Equal(44.60m, confirmation.Lines[0].LineTotal);
        Assert.Equal(2230L, confirmation.Lines[0].UnitPriceMinor);
        Assert.Equal("Mens Casual Slim Fit T-Shirt", confirmation.Lines[0].Title);
    }

    [Fact]
    public async Task Confirmation_is_marked_simulated_with_a_demo_reference()
    {
        var service = TestData.CheckoutService(_client);

        var confirmation = await service.CheckoutAsync(
            CheckoutRequestValidatorTests.ValidRequest(), CancellationToken.None);

        Assert.True(confirmation.IsSimulated);
        Assert.StartsWith("DEMO-20261022-", confirmation.OrderReference);
        Assert.Equal(20, confirmation.OrderReference.Length);
        Assert.Equal("Ada Lovelace", confirmation.CustomerName);
        Assert.Contains("simulated", confirmation.Notice, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Matching_expected_total_is_accepted()
    {
        var service = TestData.CheckoutService(_client);
        var request = CheckoutRequestValidatorTests.ValidRequest((2, 1));
        request.ExpectedTotal = 30.07m; // 22.30 + 1.78 tax + 5.99 shipping

        var confirmation = await service.CheckoutAsync(request, CancellationToken.None);

        Assert.Equal(30.07m, confirmation.Totals.Total);
    }

    [Fact]
    public async Task Stale_expected_total_returns_server_quote_instead_of_charging_client_price()
    {
        var service = TestData.CheckoutService(_client);
        var request = CheckoutRequestValidatorTests.ValidRequest((2, 1));
        request.ExpectedTotal = 1.00m; // A tampered or out-of-date client total

        var ex = await Assert.ThrowsAsync<PriceChangedException>(
            () => service.CheckoutAsync(request, CancellationToken.None));

        Assert.Equal(1.00m, ex.ExpectedTotal);
        Assert.Equal(30.07m, ex.Quote.Total);
        Assert.Equal(22.30m, ex.Quote.Lines.Single().UnitPrice);
    }

    [Fact]
    public async Task Unknown_products_are_reported_with_their_ids()
    {
        var service = TestData.CheckoutService(_client);

        var ex = await Assert.ThrowsAsync<ProductsUnavailableException>(() => service.CheckoutAsync(
            CheckoutRequestValidatorTests.ValidRequest((2, 1), (404, 1), (405, 2)), CancellationToken.None));

        Assert.Equal(new[] { 404, 405 }, ex.ProductIds);
    }

    [Fact]
    public async Task Invalid_quantities_fail_validation_before_any_upstream_call()
    {
        var service = TestData.CheckoutService(_client);

        var ex = await Assert.ThrowsAsync<RequestValidationException>(() => service.CheckoutAsync(
            CheckoutRequestValidatorTests.ValidRequest((2, 0)), CancellationToken.None));

        Assert.Contains("items[0].quantity", ex.Errors.Keys);
        Assert.Equal(0, _client.ListCalls);
    }

    [Fact]
    public async Task Upstream_outage_surfaces_as_upstream_exception()
    {
        _client.FailWith = new UpstreamServiceException(UpstreamFailureKind.Timeout, "slow");
        var service = TestData.CheckoutService(_client);

        var ex = await Assert.ThrowsAsync<UpstreamServiceException>(() => service.CheckoutAsync(
            CheckoutRequestValidatorTests.ValidRequest(), CancellationToken.None));

        Assert.Equal(UpstreamFailureKind.Timeout, ex.Kind);
    }
}
