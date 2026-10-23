using Storefront.Api.DTOs;
using Storefront.Api.Exceptions;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Catalogue;

[Trait("Category", TestCategories.Unit)]
public class ProductCatalogServiceTests
{
    private readonly InMemoryFakeStoreClient _client = new(TestData.Catalogue());

    private async Task<int[]> Ids(ProductQueryParameters query)
    {
        var service = TestData.CatalogService(_client);
        var result = await service.GetProductsAsync(query, CancellationToken.None);
        Assert.Equal(result.Items.Count, result.Count);
        return result.Items.Select(p => p.Id).ToArray();
    }

    [Fact]
    public async Task Returns_all_products_in_featured_order_by_default()
    {
        Assert.Equal(new[] { 1, 2, 5, 9, 14, 18, 19 }, await Ids(new ProductQueryParameters()));
    }

    [Fact]
    public async Task Search_is_case_insensitive_and_requires_every_term()
    {
        Assert.Equal(new[] { 18, 19 }, await Ids(new ProductQueryParameters { Search = "SHORT sleeve" }));
        Assert.Equal(new[] { 19 }, await Ids(new ProductQueryParameters { Search = "sleeve tee" }));
        Assert.Empty(await Ids(new ProductQueryParameters { Search = "nothing-matches" }));
    }

    [Fact]
    public async Task Clothing_group_includes_mens_and_womens_clothing()
    {
        Assert.Equal(new[] { 1, 2, 18, 19 }, await Ids(new ProductQueryParameters { Category = "clothing" }));
        Assert.Equal(new[] { 18, 19 }, await Ids(new ProductQueryParameters { Category = "womens-clothing" }));
        Assert.Equal(new[] { 5 }, await Ids(new ProductQueryParameters { Category = "jewelry" }));
        Assert.Equal(new[] { 1, 2, 5, 9, 14, 18, 19 }, await Ids(new ProductQueryParameters { Category = "all" }));
    }

    [Fact]
    public async Task Sorts_by_price_and_rating()
    {
        Assert.Equal(new[] { 19, 18, 2, 9, 1, 5, 14 }, await Ids(new ProductQueryParameters { Sort = "price-asc" }));
        Assert.Equal(new[] { 14, 5, 1, 9, 2, 18, 19 }, await Ids(new ProductQueryParameters { Sort = "price-desc" }));
        Assert.Equal(new[] { 18, 5, 19, 2, 1, 9, 14 }, await Ids(new ProductQueryParameters { Sort = "rating" }));
    }

    [Fact]
    public async Task Search_category_price_range_and_sort_combine()
    {
        var ids = await Ids(new ProductQueryParameters
        {
            Search = "women",
            Category = "clothing",
            MaxPrice = 9.00m,
            Sort = "price-desc",
        });

        Assert.Equal(new[] { 19 }, ids);
    }

    [Fact]
    public async Task Maps_normalised_category_price_in_minor_units_and_badge()
    {
        var service = TestData.CatalogService(_client);

        var product = await service.GetProductAsync(5, CancellationToken.None);

        Assert.NotNull(product);
        Assert.Equal("jewelry", product.Category);
        Assert.Equal("Jewelry", product.CategoryName);
        Assert.Equal(69500L, product.PriceMinor);
        Assert.Equal("top-rated", product.Badge?.Code);
    }

    [Fact]
    public async Task Get_product_returns_null_for_unknown_id()
    {
        var service = TestData.CatalogService(_client);

        Assert.Null(await service.GetProductAsync(999, CancellationToken.None));
    }

    [Fact]
    public async Task Catalogue_is_cached_between_requests()
    {
        var service = TestData.CatalogService(_client);

        await service.GetProductsAsync(new ProductQueryParameters(), CancellationToken.None);
        await service.GetCategoriesAsync(CancellationToken.None);
        await service.GetProductAsync(1, CancellationToken.None);

        Assert.Equal(1, _client.ListCalls);
        Assert.Equal(0, _client.SingleCalls);
    }

    [Fact]
    public async Task Upstream_failures_propagate_and_are_not_cached()
    {
        var service = TestData.CatalogService(_client);
        _client.FailWith = new UpstreamServiceException(UpstreamFailureKind.Unavailable, "down");

        await Assert.ThrowsAsync<UpstreamServiceException>(
            () => service.GetProductsAsync(new ProductQueryParameters(), CancellationToken.None));

        _client.FailWith = null;
        var recovered = await service.GetProductsAsync(new ProductQueryParameters(), CancellationToken.None);

        Assert.Equal(7, recovered.Count);
        Assert.Equal(2, _client.ListCalls);
    }

    [Fact]
    public async Task Categories_list_standalone_categories_then_groups_with_children()
    {
        var service = TestData.CatalogService(_client);

        var categories = await service.GetCategoriesAsync(CancellationToken.None);

        Assert.Equal(new[] { "electronics", "jewelry", "clothing", "mens-clothing", "womens-clothing" },
            categories.Select(c => c.Slug).ToArray());

        var clothing = categories.Single(c => c.Slug == "clothing");
        Assert.True(clothing.IsGroup);
        Assert.Equal(4, clothing.ProductCount);
        Assert.Equal("clothing", categories.Single(c => c.Slug == "mens-clothing").ParentSlug);
    }

    [Theory]
    [InlineData("cheapest", null, null, false)]
    [InlineData("rating", null, null, true)]
    [InlineData(null, "50", "10", false)]
    [InlineData(null, "10", "50", true)]
    public void Query_parameter_validation(string? sort, string? min, string? max, bool valid)
    {
        var query = new ProductQueryParameters
        {
            Sort = sort,
            MinPrice = min is null ? null : decimal.Parse(min),
            MaxPrice = max is null ? null : decimal.Parse(max),
        };

        var results = query.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(query)).ToList();

        Assert.Equal(valid, results.Count == 0);
    }
}
