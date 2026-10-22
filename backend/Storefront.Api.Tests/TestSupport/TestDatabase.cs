using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Storefront.Api.Data;

namespace Storefront.Api.Tests.TestSupport;

/// <summary>
/// A private SQLite database for one test, built by running the real EF Core migrations, so tests
/// exercise the same schema (constraints, indexes) as the app and never touch the developer's database.
/// In-memory by default; file-backed when a test needs several connections (concurrency, restarts).
/// </summary>
internal sealed class TestDatabase : IDisposable
{
    private readonly SqliteConnection? _keepAlive;

    private TestDatabase(string connectionString, SqliteConnection? keepAlive, string? filePath)
    {
        ConnectionString = connectionString;
        _keepAlive = keepAlive;
        FilePath = filePath;
        using var db = CreateContext();
        db.Database.Migrate();
    }

    public string ConnectionString { get; }

    public string? FilePath { get; }

    /// <summary>In-memory database that lives as long as this object (one open connection keeps it alive).</summary>
    public static TestDatabase InMemory()
    {
        var connectionString = $"Data Source=test-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        return new TestDatabase(connectionString, keepAlive, null);
    }

    /// <summary>Database file in the temp folder, deleted on dispose.</summary>
    public static TestDatabase TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"storefront-test-{Guid.NewGuid():N}.db");
        return new TestDatabase($"Data Source={path}", null, path);
    }

    public StorefrontDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<StorefrontDbContext>().UseSqlite(ConnectionString).Options);

    public void Dispose()
    {
        _keepAlive?.Dispose();
        if (FilePath is not null)
        {
            // Pooled connections keep the file open on Windows; release them before deleting.
            SqliteConnection.ClearAllPools();
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                TryDelete(FilePath + suffix);
            }
        }
    }

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Best effort: a leftover temp file is harmless.
        }
    }
}
