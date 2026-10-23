using Storefront.Api.Tests.TestSupport;
using Storefront.Api.Services;

namespace Storefront.Api.Tests.Catalogue;

[Trait("Category", TestCategories.Unit)]
public class CategoryNormalizerTests
{
    [Theory]
    [InlineData("electronics", "electronics", "Electronics", null)]
    [InlineData("jewelery", "jewelry", "Jewelry", null)]
    [InlineData("Jewellery", "jewelry", "Jewelry", null)]
    [InlineData("men's clothing", "mens-clothing", "Men's Clothing", "clothing")]
    [InlineData("  Men’s   Clothing ", "mens-clothing", "Men's Clothing", "clothing")]
    [InlineData("women's clothing", "womens-clothing", "Women's Clothing", "clothing")]
    [InlineData("kids clothing", "kids-clothing", "Kids Clothing", "clothing")]
    [InlineData("home & garden", "home-garden", "Home & Garden", null)]
    public void Normalizes_upstream_category_strings(string raw, string slug, string name, string? group)
    {
        var category = CategoryNormalizer.Normalize(raw);

        Assert.Equal(slug, category.Slug);
        Assert.Equal(name, category.Name);
        Assert.Equal(group, category.Group);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_category_becomes_uncategorized(string? raw)
    {
        Assert.Equal("uncategorized", CategoryNormalizer.Normalize(raw).Slug);
    }

    [Theory]
    [InlineData("men's clothing", null, true)]
    [InlineData("men's clothing", "all", true)]
    [InlineData("men's clothing", "clothing", true)]
    [InlineData("men's clothing", "mens-clothing", true)]
    [InlineData("men's clothing", "womens-clothing", false)]
    [InlineData("electronics", "clothing", false)]
    [InlineData("jewelery", "jewelry", true)]
    public void Matches_category_group_and_all_filters(string raw, string? filter, bool expected)
    {
        Assert.Equal(expected, CategoryNormalizer.Matches(CategoryNormalizer.Normalize(raw), filter));
    }
}
