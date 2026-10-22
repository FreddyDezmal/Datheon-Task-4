using Microsoft.EntityFrameworkCore;
using Storefront.Api.Data;
using Storefront.Api.DTOs;

namespace Storefront.Api.Services;

public interface IOrderQueryService
{
    Task<IReadOnlyList<OrderSummaryDto>> GetOrdersForUserAsync(string userId, CancellationToken cancellationToken);

    /// <summary>Returns the order only if it belongs to <paramref name="userId"/>; otherwise null.</summary>
    Task<OrderDto?> GetOrderForUserAsync(string userId, string reference, CancellationToken cancellationToken);
}

/// <summary>
/// Read side for a customer's own orders. Every query is filtered by the caller's user ID, so one
/// customer can never read another's order, even with a valid reference.
/// </summary>
public sealed class OrderQueryService(StorefrontDbContext db) : IOrderQueryService
{
    public const int MaxOrdersReturned = 100;

    public async Task<IReadOnlyList<OrderSummaryDto>> GetOrdersForUserAsync(
        string userId, CancellationToken cancellationToken)
    {
        var rows = await db.Orders.AsNoTracking()
            .Where(o => o.UserId == userId)
            .OrderByDescending(o => o.CreatedAtUtc)
            .ThenByDescending(o => o.Id)
            .Take(MaxOrdersReturned)
            .Select(o => new
            {
                o.Reference,
                o.CreatedAtUtc,
                ItemCount = o.Lines.Sum(l => l.Quantity),
                o.Total,
                o.Currency,
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new OrderSummaryDto(
                r.Reference,
                new DateTimeOffset(DateTime.SpecifyKind(r.CreatedAtUtc, DateTimeKind.Utc)),
                r.ItemCount,
                r.Total,
                r.Currency))
            .ToList();
    }

    public async Task<OrderDto?> GetOrderForUserAsync(string userId, string reference, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking()
            .Include(o => o.Lines)
            .SingleOrDefaultAsync(o => o.Reference == reference && o.UserId == userId, cancellationToken);

        return order is null ? null : OrderMapping.ToOrderDto(order);
    }
}
