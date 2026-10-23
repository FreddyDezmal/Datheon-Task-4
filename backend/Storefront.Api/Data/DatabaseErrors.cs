using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Storefront.Api.Data;

/// <summary>
/// Classifies database failures (SQLite and PostgreSQL) so callers can react without parsing messages.
///
/// "Busy" and "unavailable" are reported to clients as 503s. Checkout only lets them through for
/// failures before COMMIT is sent, where nothing can have been saved (see CheckoutService); a failure
/// during COMMIT is wrapped as an unknown outcome first, whatever its code.
/// </summary>
public static class DatabaseErrors
{
    // https://www.sqlite.org/rescode.html
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteCantOpen = 14;               // file missing, unreadable, or directory not writable
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;     // SQLITE_CONSTRAINT_UNIQUE
    private const int SqliteConstraintPrimaryKey = 1555; // SQLITE_CONSTRAINT_PRIMARYKEY

    // https://www.postgresql.org/docs/current/errcodes-appendix.html
    private const string PgUniqueViolation = "23505";
    private const string PgIntegrityClass = "23";        // integrity constraint violations
    private const string PgConnectionClass = "08";       // connection exceptions
    private static readonly HashSet<string> PgBusy =
    [
        "40001", // serialization_failure
        "40P01", // deadlock_detected
        "55P03", // lock_not_available
    ];
    private static readonly HashSet<string> PgUnavailable =
    [
        "57P01", // admin_shutdown
        "57P02", // crash_shutdown
        "57P03", // cannot_connect_now (starting up, in recovery)
        "53300", // too_many_connections
        "3D000", // invalid_catalog_name: the database doesn't exist
        "28000", // invalid_authorization_specification
        "28P01", // invalid_password
    ];

    private static T? Find<T>(Exception exception) where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    public static SqliteException? FindSqliteException(Exception exception) => Find<SqliteException>(exception);

    public static PostgresException? FindPostgresException(Exception exception) => Find<PostgresException>(exception);

    /// <summary>A failure of either database engine (as opposed to a bug or an upstream error).</summary>
    public static bool IsDatabaseException(Exception exception) =>
        exception is DbUpdateException || Find<DbException>(exception) is not null;

    public static bool IsUniqueViolation(DbUpdateException exception) =>
        (FindSqliteException(exception) is { SqliteErrorCode: SqliteConstraint } e
            && e.SqliteExtendedErrorCode is SqliteConstraintUnique or SqliteConstraintPrimaryKey)
        || FindPostgresException(exception)?.SqlState == PgUniqueViolation;

    public static bool IsConstraintViolation(Exception exception) =>
        FindSqliteException(exception) is { SqliteErrorCode: SqliteConstraint }
        || FindPostgresException(exception)?.SqlState.StartsWith(PgIntegrityClass, StringComparison.Ordinal) == true;

    public static bool IsBusy(Exception exception) =>
        FindSqliteException(exception) is { SqliteErrorCode: SqliteBusy or SqliteLocked }
        || (FindPostgresException(exception) is { } pg && PgBusy.Contains(pg.SqlState));

    /// <summary>
    /// The database could not be reached or opened: a SQLite file that can't be opened, or a PostgreSQL
    /// server that refused, dropped or never answered the connection, is shutting down, has no room for
    /// another connection, or rejected the credentials. A failure that happens mid-write with the
    /// connection still up (disk full, I/O error) is deliberately not included.
    /// </summary>
    public static bool IsUnavailable(Exception exception)
    {
        if (FindSqliteException(exception) is { SqliteErrorCode: SqliteCantOpen })
        {
            return true;
        }

        if (FindPostgresException(exception) is { } pg)
        {
            return PgUnavailable.Contains(pg.SqlState) || pg.SqlState.StartsWith(PgConnectionClass, StringComparison.Ordinal);
        }

        // An NpgsqlException that isn't a PostgresException never got an answer from the server:
        // connection refused or reset, DNS, TLS, or a timeout.
        return Find<NpgsqlException>(exception) is not null;
    }
}
