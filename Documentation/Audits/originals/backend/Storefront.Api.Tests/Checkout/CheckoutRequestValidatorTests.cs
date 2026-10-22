using Storefront.Api.DTOs;
using Storefront.Api.Services;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Checkout;

public class CheckoutRequestValidatorTests
{
    private readonly CheckoutRequestValidator _validator = new(TestData.PricingOptions());

    internal static CheckoutRequestDto ValidRequest(params (int Id, int Qty)[] items) => new()
    {
        Customer = new CustomerDto { Name = "Ada Lovelace", Email = "ada@example.com" },
        Items = (items.Length == 0 ? [(2, 1)] : items)
            .Select(i => new CheckoutItemDto { ProductId = i.Id, Quantity = i.Qty })
            .ToList(),
    };

    [Fact]
    public void Valid_request_has_no_errors()
    {
        Assert.Empty(_validator.Validate(ValidRequest((1, 1), (2, 99))));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(100)]
    public void Quantity_must_be_between_one_and_the_configured_maximum(int quantity)
    {
        var errors = _validator.Validate(ValidRequest((1, quantity)));

        Assert.Contains("items[0].quantity", errors.Keys);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Product_id_must_be_positive(int productId)
    {
        var errors = _validator.Validate(ValidRequest((productId, 1)));

        Assert.Contains("items[0].productId", errors.Keys);
    }

    [Fact]
    public void Duplicate_product_lines_are_rejected()
    {
        var errors = _validator.Validate(ValidRequest((1, 1), (1, 2)));

        Assert.Contains("items[1].productId", errors.Keys);
    }

    [Fact]
    public void Empty_cart_is_rejected()
    {
        var request = ValidRequest();
        request.Items = [];

        Assert.Contains("items", _validator.Validate(request).Keys);
    }

    [Fact]
    public void Too_many_lines_are_rejected()
    {
        var request = ValidRequest(Enumerable.Range(1, 51).Select(i => (i, 1)).ToArray());

        Assert.Contains("items", _validator.Validate(request).Keys);
    }

    [Theory]
    [InlineData("", "ada@example.com", "customer.name")]
    [InlineData(" A ", "ada@example.com", "customer.name")]
    [InlineData("Ada", "", "customer.email")]
    [InlineData("Ada", "not-an-email", "customer.email")]
    [InlineData("Ada", "ada@localhost", "customer.email")]
    [InlineData("Ada", "a da@example.com", "customer.email")]
    public void Customer_details_are_validated(string name, string email, string expectedKey)
    {
        var request = ValidRequest();
        request.Customer = new CustomerDto { Name = name, Email = email };

        Assert.Contains(expectedKey, _validator.Validate(request).Keys);
    }

    [Fact]
    public void Missing_customer_reports_both_fields()
    {
        var request = ValidRequest();
        request.Customer = null;

        var errors = _validator.Validate(request);

        Assert.Contains("customer.name", errors.Keys);
        Assert.Contains("customer.email", errors.Keys);
    }
}
