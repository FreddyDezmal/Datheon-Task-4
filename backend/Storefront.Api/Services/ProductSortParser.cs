namespace Storefront.Api.Services;

public enum ProductSort
{
    Featured,
    PriceAscending,
    PriceDescending,
    Rating,
}

public static class ProductSortParser
{
    private static readonly Dictionary<string, ProductSort> Values = new(StringComparer.OrdinalIgnoreCase)
    {
        ["featured"] = ProductSort.Featured,
        ["price-asc"] = ProductSort.PriceAscending,
        ["price-desc"] = ProductSort.PriceDescending,
        ["rating"] = ProductSort.Rating,
    };

    public static IEnumerable<string> AllowedValues => Values.Keys;

    /// <summary>Null or empty means <see cref="ProductSort.Featured"/>.</summary>
    public static bool TryParse(string? value, out ProductSort sort)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            sort = ProductSort.Featured;
            return true;
        }

        return Values.TryGetValue(value.Trim(), out sort);
    }
}
