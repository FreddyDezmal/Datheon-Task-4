using Storefront.Api.DTOs;
using Storefront.Api.Services;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Checkout;

[Trait("Category", TestCategories.Unit)]
public class CheckoutRequestValidatorTests
{
    private readonly CheckoutRequestValidator _validator = new(TestData.PricingOptions());

    /// <summary>
    /// A structurally valid request. ExpectedTotal defaults to 30.07, the correct total for the
    /// default cart (1 × product 2 at 22.30); tests with other carts set it explicitly.
    /// </summary>
    internal static CheckoutRequestDto ValidRequest(params (int Id, int Qty)[] items) => new()
    {
        Customer = new CustomerDto { Name = "Ada Lovelace", Email = "ada@example.com" },
        Items = (items.Length == 0 ? [(2, 1)] : items)
            .Select(i => new CheckoutItemDto { ProductId = i.Id, Quantity = i.Qty })
            .ToList(),
        ExpectedTotal = 30.07m,
    };

    [Fact]
    public void Valid_request_has_no_errors()
    {
        Assert.Empty(_validator.Validate(ValidRequest((1, 1), (2, 99))));
    }

    [Fact]
    public void Expected_total_is_required()
    {
        var request = ValidRequest();
        request.ExpectedTotal = null;

        Assert.Contains("expectedTotal", _validator.Validate(request).Keys);
    }

    [Fact]
    public void Negative_expected_total_is_rejected()
    {
        var request = ValidRequest();
        request.ExpectedTotal = -0.01m;

        Assert.Contains("expectedTotal", _validator.Validate(request).Keys);
    }

    [Fact]
    public void Over_the_line_limit_only_the_count_error_is_reported()
    {
        // 5,000 identical lines would otherwise produce 4,999 "appears more than once" errors.
        var request = ValidRequest(Enumerable.Repeat((1, 1), 5000).ToArray());

        var errors = _validator.Validate(request);

        Assert.Equal(new[] { "items" }, errors.Keys.ToArray());
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
