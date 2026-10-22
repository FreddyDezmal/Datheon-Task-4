namespace Storefront.Api.Models;

/// <summary>Shape of a product as returned by FakeStoreAPI. Never exposed directly to clients.</summary>
public sealed class FakeStoreProduct
{
    public int Id { get; set; }
    public string? Title { get; set; }
    public decimal Price { get; set; }
    public string? Description { get; set; }
    public string? Category { get; set; }
    public string? Image { get; set; }
    public FakeStoreRating? Rating { get; set; }
}

public sealed class FakeStoreRating
{
    public decimal Rate { get; set; }
    public int Count { get; set; }
}
