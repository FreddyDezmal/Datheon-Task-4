namespace Storefront.Api.Tests.TestSupport;

/// <summary>
/// Test categories, used with <c>dotnet test --filter Category=…</c>:
/// <list type="bullet">
/// <item><b>Unit</b>: one class or function, in memory, no database, no HTTP host. Milliseconds each.</item>
/// <item><b>Database</b>: services against a real SQLite database (in-memory or a temporary file) built
/// from the real migrations, so constraints, transactions and unique indexes are genuinely exercised.</item>
/// <item><b>Api</b>: the whole ASP.NET Core pipeline in memory (routing, authentication, model binding,
/// error handling, rate limiting) over its own temporary SQLite file. FakeStoreAPI is replaced by an
/// in-memory catalogue, so no test needs the network.</item>
/// </list>
/// </summary>
public static class TestCategories
{
    public const string Unit = "Unit";
    public const string Database = "Database";
    public const string Api = "Api";
}
