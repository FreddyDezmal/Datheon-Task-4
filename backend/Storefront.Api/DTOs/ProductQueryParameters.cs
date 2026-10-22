using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Storefront.Api.Services;

namespace Storefront.Api.DTOs;

/// <summary>Query-string parameters for GET /api/products.</summary>
public sealed class ProductQueryParameters : IValidatableObject
{
    /// <summary>Case-insensitive title search. Every whitespace-separated term must match.</summary>
    [StringLength(100)]
    [FromQuery(Name = "search")]
    public string? Search { get; set; }

    /// <summary>A category slug ("electronics"), a group slug ("clothing"), or "all".</summary>
    [StringLength(50), RegularExpression("^[a-z0-9-]+$", ErrorMessage = "Category must be a lowercase slug.")]
    [FromQuery(Name = "category")]
    public string? Category { get; set; }

    /// <summary>featured (default), price-asc, price-desc or rating.</summary>
    [FromQuery(Name = "sort")]
    public string? Sort { get; set; }

    [Range(typeof(decimal), "0", "1000000")]
    [FromQuery(Name = "minPrice")]
    public decimal? MinPrice { get; set; }

    [Range(typeof(decimal), "0", "1000000")]
    [FromQuery(Name = "maxPrice")]
    public decimal? MaxPrice { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!ProductSortParser.TryParse(Sort, out _))
        {
            yield return new ValidationResult(
                $"Sort must be one of: {string.Join(", ", ProductSortParser.AllowedValues)}.",
                ["sort"]);
        }

        if (MinPrice is { } min && MaxPrice is { } max && min > max)
        {
            yield return new ValidationResult(
                "minPrice cannot be greater than maxPrice.",
                ["minPrice", "maxPrice"]);
        }
    }
}
