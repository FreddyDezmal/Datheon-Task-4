using Storefront.Api.Models;
using Storefront.Api.Services;

namespace Storefront.Api.Tests.Catalogue;

public class ProductBadgeRulesTests
{
    [Theory]
    [InlineData(100, 4.5, 100, "top-rated")]
    [InlineData(100, 4.5, 99, null)]
    [InlineData(100, 4.4, 500, "popular")]
    [InlineData(10, 4.8, 500, "top-rated")]  // Top Rated wins over Popular and Best Value
    [InlineData(25, 3.5, 10, "best-value")]
    [InlineData(25.01, 3.5, 10, null)]
    [InlineData(10, 3.4, 10, null)]
    public void Applies_rules_in_priority_order(double price, double rate, int count, string? expected)
    {
        var badge = ProductBadgeRules.Evaluate((decimal)price, new ProductRating((decimal)rate, count));

        Assert.Equal(expected, badge?.Code);
    }

    [Fact]
    public void No_rating_means_no_badge()
    {
        Assert.Null(ProductBadgeRules.Evaluate(5m, null));
    }
}
