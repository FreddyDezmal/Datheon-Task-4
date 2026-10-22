using System.Globalization;
using System.Text;
using Storefront.Api.Models;

namespace Storefront.Api.Services;

/// <summary>
/// Maps FakeStoreAPI's free-form category strings (e.g. "jewelery", "men's clothing")
/// onto stable slugs, display names and groups. Unknown categories are slugified rather
/// than rejected, so a new upstream category still shows up under "All Products".
/// </summary>
public static class CategoryNormalizer
{
    public const string AllSlug = "all";
    public const string ClothingGroupSlug = "clothing";
    public const string ClothingGroupName = "Clothing";

    private static readonly CategoryInfo Electronics = new("electronics", "Electronics", null);
    private static readonly CategoryInfo Jewelry = new("jewelry", "Jewelry", null);
    private static readonly CategoryInfo MensClothing = new("mens-clothing", "Men's Clothing", ClothingGroupSlug);
    private static readonly CategoryInfo WomensClothing = new("womens-clothing", "Women's Clothing", ClothingGroupSlug);

    private static readonly Dictionary<string, CategoryInfo> KnownCategories = new(StringComparer.Ordinal)
    {
        ["electronics"] = Electronics,
        ["electronic"] = Electronics,
        ["jewelery"] = Jewelry, // FakeStoreAPI's spelling
        ["jewelry"] = Jewelry,
        ["jewellery"] = Jewelry,
        ["men's clothing"] = MensClothing,
        ["mens clothing"] = MensClothing,
        ["men clothing"] = MensClothing,
        ["women's clothing"] = WomensClothing,
        ["womens clothing"] = WomensClothing,
        ["women clothing"] = WomensClothing,
    };

    public static CategoryInfo Normalize(string? rawCategory)
    {
        var key = CanonicalKey(rawCategory);
        if (key.Length == 0)
        {
            return new CategoryInfo("uncategorized", "Uncategorized", null);
        }

        if (KnownCategories.TryGetValue(key, out var known))
        {
            return known;
        }

        var slug = Slugify(key);
        var name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(key);
        var group = key.Contains("clothing", StringComparison.Ordinal) ? ClothingGroupSlug : null;
        return new CategoryInfo(slug.Length == 0 ? "uncategorized" : slug, name, group);
    }

    /// <summary>True when the category matches a filter slug: "all"/empty, the category slug, or its group slug.</summary>
    public static bool Matches(CategoryInfo category, string? filterSlug)
    {
        if (string.IsNullOrWhiteSpace(filterSlug) || filterSlug == AllSlug)
        {
            return true;
        }

        return string.Equals(category.Slug, filterSlug, StringComparison.Ordinal)
            || string.Equals(category.Group, filterSlug, StringComparison.Ordinal);
    }

    private static string CanonicalKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var normalized = raw.Trim().ToLowerInvariant()
            .Replace('’', '\'')  // right single quotation mark
            .Replace('`', '\'');
        return string.Join(' ', normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingDash = false;
        foreach (var ch in value)
        {
            if (ch == '\'')
            {
                continue;
            }

            if (char.IsAsciiLetterOrDigit(ch))
            {
                if (pendingDash && builder.Length > 0)
                {
                    builder.Append('-');
                }

                builder.Append(ch);
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }

        return builder.ToString();
    }
}
