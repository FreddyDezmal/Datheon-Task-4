using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Storefront.Api.Configuration;
using Storefront.Api.DTOs;

namespace Storefront.Api.Services;

public interface ICheckoutRequestValidator
{
    /// <summary>Returns field errors keyed by JSON path (empty when the request is valid).</summary>
    IDictionary<string, string[]> Validate(CheckoutRequestDto request);
}

/// <summary>
/// Business validation for checkout. This is the security boundary: the frontend runs the
/// same checks for usability, but the server never relies on them.
/// </summary>
public sealed partial class CheckoutRequestValidator(IOptions<PricingOptions> options) : ICheckoutRequestValidator
{
    public const int NameMinLength = 2;
    public const int NameMaxLength = 100;
    public const int EmailMaxLength = 254;

    private readonly PricingOptions _options = options.Value;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EmailPattern();

    public IDictionary<string, string[]> Validate(CheckoutRequestDto request)
    {
        var errors = new Dictionary<string, List<string>>();
        void Add(string key, string message)
        {
            if (!errors.TryGetValue(key, out var list))
            {
                errors[key] = list = [];
            }

            list.Add(message);
        }

        var name = request.Customer?.Name?.Trim() ?? string.Empty;
        if (name.Length < NameMinLength || name.Length > NameMaxLength)
        {
            Add("customer.name", $"Name must be between {NameMinLength} and {NameMaxLength} characters.");
        }

        var email = request.Customer?.Email?.Trim() ?? string.Empty;
        if (email.Length == 0 || email.Length > EmailMaxLength || !EmailPattern().IsMatch(email))
        {
            Add("customer.email", "Enter a valid email address.");
        }

        var items = request.Items ?? [];
        if (items.Count == 0)
        {
            Add("items", "The cart is empty.");
        }
        else if (items.Count > _options.MaxLineItems)
        {
            Add("items", $"A single order can contain at most {_options.MaxLineItems} different products.");
        }

        var seen = new HashSet<int>();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item is null)
            {
                Add($"items[{i}]", "Item is required.");
                continue;
            }

            if (item.ProductId <= 0)
            {
                Add($"items[{i}].productId", "Product ID must be a positive integer.");
            }
            else if (!seen.Add(item.ProductId))
            {
                Add($"items[{i}].productId", $"Product {item.ProductId} appears more than once.");
            }

            if (item.Quantity < 1 || item.Quantity > _options.MaxQuantityPerLine)
            {
                Add($"items[{i}].quantity", $"Quantity must be between 1 and {_options.MaxQuantityPerLine}.");
            }
        }

        if (request.ExpectedTotal is < 0)
        {
            Add("expectedTotal", "Expected total cannot be negative.");
        }

        return errors.ToDictionary(e => e.Key, e => e.Value.ToArray());
    }
}
