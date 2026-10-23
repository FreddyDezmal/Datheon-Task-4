using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Storefront.Api.Tests.TestSupport;

namespace Storefront.Api.Tests.Integration;

/// <summary>
/// Sign-in tokens must survive a new app instance (every deploy starts a new container), so the Data
/// Protection key ring that signs them lives in the database. Each factory below is a separate host
/// with its own in-memory key cache, standing in for a new container on the same database.
/// The last step (keys deleted from the database, then rejected) proves the keys come from the
/// database and not from a folder on this machine that every test host would share anyway.
/// </summary>
[Trait("Category", TestCategories.Api)]
public sealed class DataProtectionKeyTests
{
    private static async Task<HttpStatusCode> OrdersWith(StorefrontApiFactory factory, string accessToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return (await client.GetAsync("/api/orders")).StatusCode;
    }

    internal static async Task SurviveNewInstances(Func<StorefrontApiFactory> newInstance, Func<Task<long>> keyCount, Func<Task> deleteKeys)
    {
        StorefrontApiFactory.TokenResponse tokens;
        using (var first = newInstance())
        {
            (_, tokens) = await first.SignInAsync("keys@example.com");
        }

        Assert.True(await keyCount() >= 1, "The key ring should have been written to the database.");

        using (var second = newInstance())
        {
            Assert.Equal(HttpStatusCode.OK, await OrdersWith(second, tokens.AccessToken));
            var refreshed = await second.CreateClient().PostAsJsonAsync("/api/account/refresh", new { refreshToken = tokens.RefreshToken });
            Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        }

        await deleteKeys();

        using var third = newInstance();
        Assert.Equal(HttpStatusCode.Unauthorized, await OrdersWith(third, tokens.AccessToken));
    }

    [Fact]
    public async Task Sign_ins_survive_a_new_instance_because_the_keys_live_in_the_database()
    {
        var path = Path.Combine(Path.GetTempPath(), $"storefront-keys-{Guid.NewGuid():N}.db");
        async Task<long> Scalar(string sql)
        {
            await using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        try
        {
            await SurviveNewInstances(
                () => new StorefrontApiFactory(databasePath: path),
                () => Scalar("SELECT COUNT(*) FROM DataProtectionKeys"),
                () => Scalar("DELETE FROM DataProtectionKeys"));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestDatabase.TryDelete(path);
        }
    }
}
