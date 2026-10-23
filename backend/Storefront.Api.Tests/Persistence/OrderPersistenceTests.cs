using Microsoft.EntityFrameworkCore;
using Storefront.Api.Data;
using Storefront.Api.Data.Entities;
using Storefront.Api.DTOs;
using Storefront.Api.Exceptions;
using Storefront.Api.Services;
using Storefront.Api.Tests.Checkout;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Persistence;

[Trait("Category", TestCategories.Database)]
public sealed class OrderPersistenceTests : IDisposable
{
    private readonly InMemoryFakeStoreClient _client = new(TestData.Catalogue());
    private readonly TestDatabase _database = TestDatabase.InMemory();

    public void Dispose() => _database.Dispose();

    private async Task<CheckoutResult> Checkout(CheckoutRequestDto request, CheckoutCaller caller)
    {
        await using var db = _database.CreateContext();
        return await TestData.CheckoutService(_client, db).CheckoutAsync(request, caller, CancellationToken.None);
    }

    private static CheckoutRequestDto TwoLineRequest()
    {
        var request = CheckoutRequestValidatorTests.ValidRequest((2, 2), (19, 1));
        request.ExpectedTotal = 56.75m;
        return request;
    }

    [Fact]
    public async Task Checkout_saves_the_order_and_its_lines_with_server_prices()
    {
        var result = await Checkout(TwoLineRequest(), CheckoutCaller.Guest);

        await using var db = _database.CreateContext();
        var order = await db.Orders.Include(o => o.Lines).SingleAsync();
        Assert.Equal(result.Confirmation.OrderReference, order.Reference);
        Assert.Equal(52.55m, order.Subtotal);
        Assert.Equal(4.20m, order.Tax);
        Assert.Equal(0m, order.Shipping);
        Assert.Equal(56.75m, order.Total);
        Assert.Equal("USD", order.Currency);
        Assert.Null(order.UserId);
        Assert.Equal(TestData.Now.UtcDateTime, order.CreatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, order.CreatedAtUtc.Kind);

        var shirt = order.Lines.Single(l => l.ProductId == 2);
        Assert.Equal("Mens Casual Slim Fit T-Shirt", shirt.Title);
        Assert.Equal(22.30m, shirt.UnitPrice);
        Assert.Equal(2, shirt.Quantity);
        Assert.Equal(44.60m, shirt.LineTotal);
        Assert.Equal(2, order.Lines.Count);
    }

    [Fact]
    public async Task Stored_order_keeps_its_snapshot_when_the_catalogue_changes()
    {
        var first = await Checkout(TwoLineRequest(), CheckoutCaller.Guest);

        // The upstream catalogue later changes price and title, and someone orders at the new price.
        var changedClient = new InMemoryFakeStoreClient([TestData.Product(2, "Renamed T-Shirt", 99.99m, "men's clothing")]);
        var later = CheckoutRequestValidatorTests.ValidRequest((2, 1));
        later.ExpectedTotal = 107.99m; // 99.99 + 8.00 tax, free shipping
        await using (var writeDb = _database.CreateContext())
        {
            await TestData.CheckoutService(changedClient, writeDb).CheckoutAsync(later, CheckoutCaller.Guest, CancellationToken.None);
        }

        await using var db = _database.CreateContext();
        var order = await db.Orders.Include(o => o.Lines).SingleAsync(o => o.Reference == first.Confirmation.OrderReference);
        var dto = OrderMapping.ToOrderDto(order);
        Assert.Equal(56.75m, dto.Totals.Total);
        Assert.Equal("Mens Casual Slim Fit T-Shirt", dto.Lines[0].Title);
        Assert.Equal(22.30m, dto.Lines[0].UnitPrice);
        Assert.Equal(2, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task Signed_in_checkout_links_the_order_to_the_user_from_the_token()
    {
        var userId = await CreateUserAsync("ada@example.com");

        await Checkout(TwoLineRequest(), new CheckoutCaller(userId, null));

        await using var db = _database.CreateContext();
        Assert.Equal(userId, (await db.Orders.SingleAsync()).UserId);
    }

    [Fact]
    public async Task Failed_checkout_writes_nothing()
    {
        var stale = TwoLineRequest();
        stale.ExpectedTotal = 1.00m;

        await Assert.ThrowsAsync<PriceChangedException>(() => Checkout(stale, CheckoutCaller.Guest));
        await Assert.ThrowsAsync<ProductsUnavailableException>(
            () => Checkout(CheckoutRequestValidatorTests.ValidRequest((404, 1)), CheckoutCaller.Guest));

        await using var db = _database.CreateContext();
        Assert.Equal(0, await db.Orders.CountAsync());
        Assert.Equal(0, await db.OrderLines.CountAsync());
    }

    [Fact]
    public async Task Repeating_a_request_with_the_same_idempotency_key_returns_the_original_order()
    {
        var caller = new CheckoutCaller(null, "key-0001-retry");

        var first = await Checkout(TwoLineRequest(), caller);
        var second = await Checkout(TwoLineRequest(), caller);

        Assert.False(first.IsReplay);
        Assert.True(second.IsReplay);
        Assert.Equal(first.Confirmation.OrderReference, second.Confirmation.OrderReference);
        Assert.Equal(first.Confirmation.Totals, second.Confirmation.Totals);
        await using var db = _database.CreateContext();
        Assert.Equal(1, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task Requests_without_a_key_each_create_an_order()
    {
        await Checkout(TwoLineRequest(), CheckoutCaller.Guest);
        await Checkout(TwoLineRequest(), CheckoutCaller.Guest);

        await using var db = _database.CreateContext();
        Assert.Equal(2, await db.Orders.CountAsync());
    }

    [Fact]
    public async Task Reusing_a_key_for_different_items_is_rejected()
    {
        var caller = new CheckoutCaller(null, "key-0002-reuse");
        await Checkout(TwoLineRequest(), caller);

        await Assert.ThrowsAsync<IdempotencyKeyReusedException>(
            () => Checkout(CheckoutRequestValidatorTests.ValidRequest((2, 1)), caller));
    }

    [Fact]
    public async Task Another_caller_cannot_replay_someone_elses_key()
    {
        var userId = await CreateUserAsync("owner@example.com");
        await Checkout(TwoLineRequest(), new CheckoutCaller(userId, "key-0003-owned"));

        // Same key and body, but as a guest: must not return the owner's confirmation.
        await Assert.ThrowsAsync<IdempotencyKeyReusedException>(
            () => Checkout(TwoLineRequest(), new CheckoutCaller(null, "key-0003-owned")));
    }

    [Theory]
    [InlineData("short")]
    [InlineData("has spaces in it")]
    [InlineData("semi;colon-key")]
    public async Task Malformed_idempotency_keys_are_rejected(string key)
    {
        var ex = await Assert.ThrowsAsync<RequestValidationException>(
            () => Checkout(TwoLineRequest(), new CheckoutCaller(null, key)));

        Assert.Contains("Idempotency-Key", ex.Errors.Keys);
    }

    [Fact]
    public async Task Concurrent_duplicate_requests_create_exactly_one_order()
    {
        using var fileDatabase = TestDatabase.TempFile();
        var caller = new CheckoutCaller(null, "key-0004-concurrent");

        async Task<CheckoutResult> Attempt()
        {
            await using var db = fileDatabase.CreateContext();
            return await TestData.CheckoutService(_client, db).CheckoutAsync(TwoLineRequest(), caller, CancellationToken.None);
        }

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(Attempt)));

        Assert.Single(results.Select(r => r.Confirmation.OrderReference).Distinct());
        Assert.Single(results, r => !r.IsReplay);
        await using var check = fileDatabase.CreateContext();
        Assert.Equal(1, await check.Orders.CountAsync());
    }

    [Fact]
    public async Task Losing_the_insert_race_returns_the_winners_order_instead_of_failing_or_duplicating()
    {
        // Both requests pass the "already ordered?" check; the other one commits first, so this
        // request's insert hits the unique index on IdempotencyKey. It must answer with the winner's
        // order. (The parallel test above usually serialises before reaching this path.)
        var caller = new CheckoutCaller(null, "key-0007-race-loser");
        CheckoutResult? winner = null;
        var race = new BeforeFirstSave(async () => winner = await Checkout(TwoLineRequest(), caller));

        await using var db = _database.CreateContext(race);
        var loser = await TestData.CheckoutService(_client, db).CheckoutAsync(TwoLineRequest(), caller, CancellationToken.None);

        Assert.NotNull(winner);
        Assert.False(winner.IsReplay);
        Assert.True(loser.IsReplay);
        Assert.Equal(winner.Confirmation.OrderReference, loser.Confirmation.OrderReference);
        await using var check = _database.CreateContext();
        Assert.Equal(1, await check.Orders.CountAsync());
    }

    [Fact]
    public async Task Losing_the_race_to_a_different_order_with_the_same_key_is_rejected_not_merged()
    {
        var caller = new CheckoutCaller(null, "key-0008-race-other");
        var other = CheckoutRequestValidatorTests.ValidRequest((2, 1));
        other.ExpectedTotal = 30.07m;
        var race = new BeforeFirstSave(() => Checkout(other, caller));

        await using var db = _database.CreateContext(race);
        await Assert.ThrowsAsync<IdempotencyKeyReusedException>(() =>
            TestData.CheckoutService(_client, db).CheckoutAsync(TwoLineRequest(), caller, CancellationToken.None));

        await using var check = _database.CreateContext();
        var saved = await check.Orders.Include(o => o.Lines).SingleAsync();
        Assert.Single(saved.Lines); // the other request's order, untouched
    }

    [Fact]
    public async Task Repeated_reference_collisions_give_up_with_an_error_rather_than_loop_or_save_twice()
    {
        var always = new SequenceReferenceGenerator("DEMO-20261022-SAMESA");
        await using (var db = _database.CreateContext())
        {
            await TestData.CheckoutService(_client, db, references: always).CheckoutAsync(TwoLineRequest(), CheckoutCaller.Guest, CancellationToken.None);
        }

        await using (var db = _database.CreateContext())
        {
            var service = TestData.CheckoutService(_client, db, references: new SequenceReferenceGenerator("DEMO-20261022-SAMESA"));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() =>
                service.CheckoutAsync(TwoLineRequest(), CheckoutCaller.Guest, CancellationToken.None));
            Assert.True(Storefront.Api.Data.DatabaseErrors.IsUniqueViolation(error));
        }

        await using var check = _database.CreateContext();
        Assert.Equal(1, await check.Orders.CountAsync());
    }

    [Fact]
    public async Task A_reference_collision_is_retried_with_a_new_reference()
    {
        var references = new SequenceReferenceGenerator("DEMO-20261022-AAAAAA", "DEMO-20261022-AAAAAA", "DEMO-20261022-BBBBBB");
        await using (var db = _database.CreateContext())
        {
            var service = TestData.CheckoutService(_client, db, references: references);
            await service.CheckoutAsync(TwoLineRequest(), CheckoutCaller.Guest, CancellationToken.None);
        }

        await using (var db = _database.CreateContext())
        {
            var service = TestData.CheckoutService(_client, db, references: references);
            var second = await service.CheckoutAsync(TwoLineRequest(), CheckoutCaller.Guest, CancellationToken.None);
            Assert.Equal("DEMO-20261022-BBBBBB", second.Confirmation.OrderReference);
        }

        await using var check = _database.CreateContext();
        Assert.Equal(2, await check.Orders.CountAsync());
    }

    [Fact]
    public async Task Database_constraints_reject_invalid_lines_and_roll_back_the_whole_order()
    {
        await using var db = _database.CreateContext();
        var order = new Order
        {
            Reference = "DEMO-20261022-BADQTY",
            CreatedAtUtc = DateTime.UtcNow,
            CustomerName = "Ada",
            CustomerEmail = "ada@example.com",
            Currency = "USD",
            Subtotal = 10m,
            Total = 10m,
        };
        order.Lines.Add(new OrderLine { ProductId = 1, Title = "Fine", UnitPrice = 10m, Quantity = 1, LineTotal = 10m });
        order.Lines.Add(new OrderLine { ProductId = 2, Title = "Zero quantity", UnitPrice = 1m, Quantity = 0, LineTotal = 0m });
        db.Orders.Add(order);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        Assert.True(DatabaseErrors.IsConstraintViolation(ex));
        await using var check = _database.CreateContext();
        Assert.Equal(0, await check.Orders.CountAsync());
        Assert.Equal(0, await check.OrderLines.CountAsync());
    }

    [Theory]
    [InlineData("Subtotal")]
    [InlineData("Total")]
    public async Task Negative_amounts_are_rejected_by_check_constraints(string field)
    {
        await using var db = _database.CreateContext();
        var order = new Order
        {
            Reference = $"DEMO-NEG-{field}",
            CreatedAtUtc = DateTime.UtcNow,
            CustomerName = "Ada",
            CustomerEmail = "ada@example.com",
            Currency = "USD",
            Subtotal = field == "Subtotal" ? -1m : 1m,
            Total = field == "Total" ? -1m : 1m,
        };
        db.Orders.Add(order);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(DatabaseErrors.IsConstraintViolation(ex));
    }

    [Fact]
    public async Task Order_queries_only_return_the_callers_own_orders()
    {
        var ada = await CreateUserAsync("ada@example.com");
        var bob = await CreateUserAsync("bob@example.com");
        var adas = await Checkout(TwoLineRequest(), new CheckoutCaller(ada, null));
        await Checkout(TwoLineRequest(), new CheckoutCaller(bob, null));
        await Checkout(TwoLineRequest(), CheckoutCaller.Guest);

        await using var db = _database.CreateContext();
        var queries = new OrderQueryService(db);

        var list = await queries.GetOrdersForUserAsync(ada, CancellationToken.None);
        var summary = Assert.Single(list);
        Assert.Equal(adas.Confirmation.OrderReference, summary.OrderReference);
        Assert.Equal(3, summary.ItemCount);
        Assert.Equal(56.75m, summary.Total);

        Assert.NotNull(await queries.GetOrderForUserAsync(ada, adas.Confirmation.OrderReference, CancellationToken.None));
        Assert.Null(await queries.GetOrderForUserAsync(bob, adas.Confirmation.OrderReference, CancellationToken.None));
    }

    [Fact]
    public void Migrations_match_the_model()
    {
        using var db = _database.CreateContext();

        Assert.False(db.Database.HasPendingModelChanges(),
            "The EF model has changed since the last migration. Run: dotnet ef migrations add <Name>");
    }

    private async Task<string> CreateUserAsync(string email)
    {
        await using var db = _database.CreateContext();
        var user = new StorefrontUser { UserName = email, Email = email, NormalizedEmail = email.ToUpperInvariant(), NormalizedUserName = email.ToUpperInvariant() };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    private sealed class SequenceReferenceGenerator(params string[] references) : IOrderReferenceGenerator
    {
        private int _next;

        public string Next() => references[Math.Min(_next++, references.Length - 1)];
    }
}
