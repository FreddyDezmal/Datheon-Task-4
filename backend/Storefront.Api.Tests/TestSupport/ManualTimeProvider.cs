namespace Storefront.Api.Tests.TestSupport;

/// <summary>
/// A clock the test moves by hand. Plugged into the API's DI container it also drives
/// ASP.NET Core's bearer-token expiry, so "an hour later" takes no real time.
/// </summary>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now = _now.Add(by);
}
