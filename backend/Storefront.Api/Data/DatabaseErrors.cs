using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Storefront.Api.Data;

/// <summary>Classifies SQLite failures so callers can react without parsing error messages.</summary>
public static class DatabaseErrors
{
    // https://www.sqlite.org/rescode.html
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private const int SqliteCantOpen = 14;               // file missing, unreadable, or directory not writable
    private const int SqliteConstraint = 19;
    private const int SqliteConstraintUnique = 2067;     // SQLITE_CONSTRAINT_UNIQUE
    private const int SqliteConstraintPrimaryKey = 1555; // SQLITE_CONSTRAINT_PRIMARYKEY

    public static SqliteException? FindSqliteException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException sqlite)
            {
                return sqlite;
            }
        }

        return null;
    }

    public static bool IsUniqueViolation(DbUpdateException exception) =>
        FindSqliteException(exception) is { SqliteErrorCode: SqliteConstraint } e
        && e.SqliteExtendedErrorCode is SqliteConstraintUnique or SqliteConstraintPrimaryKey;

    public static bool IsConstraintViolation(Exception exception) =>
        FindSqliteException(exception) is { SqliteErrorCode: SqliteConstraint };

    public static bool IsBusy(Exception exception) =>
        FindSqliteException(exception) is { SqliteErrorCode: SqliteBusy or SqliteLocked };

    /// <summary>
    /// The database file could not be opened at all, so nothing was read or written. A failure that
    /// happens mid-write (I/O error, disk full) is deliberately not included: its outcome is uncertain.
    /// </summary>
    public static bool IsUnavailable(Exception exception) =>
        FindSqliteException(exception) is { SqliteErrorCode: SqliteCantOpen };
}
