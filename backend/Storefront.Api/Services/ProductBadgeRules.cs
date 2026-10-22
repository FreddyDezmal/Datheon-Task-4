using Storefront.Api.Models;

namespace Storefront.Api.Services;

/// <summary>
/// Demo merchandising rules. Evaluated in order; the first match wins.
/// <list type="number">
///   <item>Top Rated  — rating ≥ 4.5 from at least 100 reviews.</item>
///   <item>Popular    — at least 400 reviews.</item>
///   <item>Best Value — price ≤ 25.00 with a rating of at least 3.5.</item>
/// </list>
/// </summary>
public static class ProductBadgeRules
{
    public const decimal TopRatedMinRate = 4.5m;
    public const int TopRatedMinReviews = 100;
    public const int PopularMinReviews = 400;
    public const decimal BestValueMaxPrice = 25.00m;
    public const decimal BestValueMinRate = 3.5m;

    public static ProductBadge? Evaluate(decimal price, ProductRating? rating)
    {
        if (rating is null)
        {
            return null;
        }

        if (rating.Rate >= TopRatedMinRate && rating.Count >= TopRatedMinReviews)
        {
            return new ProductBadge("top-rated", "Top Rated");
        }

        if (rating.Count >= PopularMinReviews)
        {
            return new ProductBadge("popular", "Popular");
        }

        if (price <= BestValueMaxPrice && rating.Rate >= BestValueMinRate)
        {
            return new ProductBadge("best-value", "Best Value");
        }

        return null;
    }
}
