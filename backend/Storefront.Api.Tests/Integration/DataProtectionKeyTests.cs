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
        await using var database = new KeyDatabase();
        await SurviveNewInstances(
            () => new StorefrontApiFactory(databasePath: database.Path),
            () => database.Scalar("SELECT COUNT(*) FROM DataProtectionKeys"),
            () => database.Scalar("DELETE FROM DataProtectionKeys"));
    }

    [Fact]
    public async Task With_a_certificate_the_keys_are_stored_encrypted_and_sign_ins_survive_a_new_instance()
    {
        await using var database = new KeyDatabase();
        var settings = TestCertificates.Settings(TestCertificates.A);

        StorefrontApiFactory.TokenResponse tokens;
        using (var first = new StorefrontApiFactory(databasePath: database.Path, settings: settings))
        {
            (_, tokens) = await first.SignInAsync("encrypted@example.com");
            Assert.Contains(first.Logs.Entries, e => e.Message.StartsWith("Sign-in keys are encrypted with certificate"));
        }

        AssertEncrypted(await database.KeyXml());

        using var second = new StorefrontApiFactory(databasePath: database.Path, settings: settings);
        Assert.Equal(HttpStatusCode.OK, await OrdersWith(second, tokens.AccessToken));
    }

    [Fact]
    public async Task With_a_different_certificate_old_sign_ins_are_rejected_but_the_api_keeps_working()
    {
        await using var database = new KeyDatabase();
        StorefrontApiFactory.TokenResponse tokens;
        using (var first = new StorefrontApiFactory(databasePath: database.Path, settings: TestCertificates.Settings(TestCertificates.A)))
        {
            (_, tokens) = await first.SignInAsync("lost-certificate@example.com");
        }

        // The key in the database can't be decrypted without certificate A: the outcome of losing the
        // certificate is that everyone signs in again, not an outage.
        using var second = new StorefrontApiFactory(databasePath: database.Path, settings: TestCertificates.Settings(TestCertificates.B));
        Assert.Equal(HttpStatusCode.Unauthorized, await OrdersWith(second, tokens.AccessToken));

        var (client, _) = await second.SignInAsync("lost-certificate@example.com");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/orders")).StatusCode);
    }

    [Fact]
    public async Task Rotating_to_a_new_certificate_keeps_sign_ins_while_the_old_one_is_listed_as_previous()
    {
        await using var database = new KeyDatabase();
        StorefrontApiFactory.TokenResponse tokens;
        using (var first = new StorefrontApiFactory(databasePath: database.Path, settings: TestCertificates.Settings(TestCertificates.A)))
        {
            (_, tokens) = await first.SignInAsync("rotation@example.com");
        }

        using var rotated = new StorefrontApiFactory(
            databasePath: database.Path, settings: TestCertificates.Settings(TestCertificates.B, TestCertificates.A));
        Assert.Equal(HttpStatusCode.OK, await OrdersWith(rotated, tokens.AccessToken));
        var refreshed = await rotated.CreateClient().PostAsJsonAsync("/api/account/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
    }

    [Fact]
    public async Task Keys_written_before_encryption_was_enabled_keep_working_after()
    {
        await using var database = new KeyDatabase();
        StorefrontApiFactory.TokenResponse tokens;
        using (var unencrypted = new StorefrontApiFactory(databasePath: database.Path))
        {
            (_, tokens) = await unencrypted.SignInAsync("before-encryption@example.com");
        }

        Assert.All(await database.KeyXml(), xml => Assert.Contains("unencrypted form", xml));

        // Turning encryption on doesn't re-encrypt existing keys; they stay readable (and in use) until they
        // expire, which is why Deployment.md deletes them when encryption is first enabled.
        using var encrypted = new StorefrontApiFactory(databasePath: database.Path, settings: TestCertificates.Settings(TestCertificates.A));
        Assert.Equal(HttpStatusCode.OK, await OrdersWith(encrypted, tokens.AccessToken));
    }

    internal static void AssertEncrypted(IReadOnlyList<string> keyXml)
    {
        Assert.NotEmpty(keyXml);
        Assert.All(keyXml, xml =>
        {
            Assert.Contains("<encryptedSecret", xml);
            Assert.Contains("EncryptedXmlDecryptor", xml);
            Assert.DoesNotContain("unencrypted form", xml);
        });
    }

    /// <summary>A temporary SQLite file shared by several app instances.</summary>
    private sealed class KeyDatabase : IAsyncDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"storefront-keys-{Guid.NewGuid():N}.db");

        public async Task<long> Scalar(string sql)
        {
            await using var connection = await Open();
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
        }

        public async Task<IReadOnlyList<string>> KeyXml()
        {
            await using var connection = await Open();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Xml FROM DataProtectionKeys";
            var keys = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                keys.Add(reader.GetString(0));
            }

            return keys;
        }

        public ValueTask DisposeAsync()
        {
            SqliteConnection.ClearAllPools();
            TestDatabase.TryDelete(Path);
            return ValueTask.CompletedTask;
        }

        private async Task<SqliteConnection> Open()
        {
            var connection = new SqliteConnection($"Data Source={Path};Pooling=False");
            await connection.OpenAsync();
            return connection;
        }
    }
}
