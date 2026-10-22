namespace Storefront.Api.Data.Entities;

/// <summary>One product on an order, with the title and price as they were when the order was placed.</summary>
public sealed class OrderLine
{
    public long Id { get; set; }

    public long OrderId { get; set; }

    public Order? Order { get; set; }

    /// <summary>FakeStoreAPI product ID. Not a foreign key: the catalogue lives upstream, not in this database.</summary>
    public int ProductId { get; set; }

    public required string Title { get; set; }

    public decimal UnitPrice { get; set; }

    public int Quantity { get; set; }

    public decimal LineTotal { get; set; }
}
