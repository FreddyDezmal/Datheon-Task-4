using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Storefront.Api.Services;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Integration;

/// <summary>
/// Hosts the real API in memory (Development environment, so migrations run on startup) with its
/// own temporary SQLite file and an in-memory product catalogue instead of FakeStoreAPI.
/// </summary>
public sealed class StorefrontApiFactory : WebApplicationFactory<Program>
{
    private readonly bool _deleteDatabaseOnDispose;

    public StorefrontApiFactory(string? databasePath = null, int accountPermitLimit = 1000)
    {
        DatabasePath = databasePath ?? Path.Combine(Path.GetTempPath(), $"storefront-api-test-{Guid.NewGuid():N}.db");
        _deleteDatabaseOnDispose = databasePath is null;
        AccountPermitLimit = accountPermitLimit;
    }

    public string DatabasePath { get; }

    public int AccountPermitLimit { get; }

    internal InMemoryFakeStoreClient Catalogue { get; } = new(TestData.Catalogue());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("ConnectionStrings:Storefront", $"Data Source={DatabasePath}");
        builder.UseSetting("RateLimiting:AccountPermitLimit", AccountPermitLimit.ToString());
        builder.UseSetting("RateLimiting:CheckoutPermitLimit", "1000");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IFakeStoreClient>();
            services.AddSingleton<IFakeStoreClient>(Catalogue);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _deleteDatabaseOnDispose)
        {
            SqliteConnection.ClearAllPools();
            TestDatabase.TryDelete(DatabasePath);
        }
    }

    /// <summary>Registers an account and returns an HttpClient that sends its bearer token.</summary>
    public async Task<HttpClient> CreateSignedInClientAsync(string email, string password = "Str0ng!Passw0rd")
    {
        var client = CreateClient();
        var register = await client.PostAsJsonAsync("/api/account/register", new { email, password });
        if (!register.IsSuccessStatusCode && register.StatusCode != System.Net.HttpStatusCode.BadRequest)
        {
            register.EnsureSuccessStatusCode();
        }

        var login = await client.PostAsJsonAsync("/api/account/login", new { email, password });
        login.EnsureSuccessStatusCode();
        var token = await login.Content.ReadFromJsonAsync<TokenResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token!.AccessToken);
        return client;
    }

    public sealed record TokenResponse(string TokenType, string AccessToken, int ExpiresIn, string RefreshToken);
}
