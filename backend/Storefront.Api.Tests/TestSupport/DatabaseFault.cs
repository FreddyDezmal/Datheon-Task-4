using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Storefront.Api.Tests.TestSupport;

/// <summary>Where in an order's write a <see cref="DatabaseFault"/> strikes.</summary>
public enum FaultStage
{
    /// <summary>While inserting, inside the transaction: COMMIT is never sent.</summary>
    Insert,
    /// <summary>When COMMIT is about to be sent: nothing becomes durable.</summary>
    BeforeCommit,
    /// <summary>After COMMIT succeeded: the order IS saved, but the caller is told the commit failed.</summary>
    AfterCommit,
}

/// <summary>
/// Throws a real SqliteException with the given result code, once, at the chosen stage of the next
/// order write. Lets tests check how each failure is reported against what actually reached the database.
/// </summary>
internal sealed class DatabaseFault(FaultStage stage, Func<Exception> makeException) : DbTransactionInterceptor, IDbCommandInterceptor
{
    /// <summary>A real SqliteException with this result code.</summary>
    public DatabaseFault(FaultStage stage, int sqliteErrorCode)
        : this(stage, () => new SqliteException($"Injected SQLite error {sqliteErrorCode}.", sqliteErrorCode))
    {
    }

    /// <summary>Off until the test arms it, so startup (migrations commit transactions too) is unaffected.</summary>
    public bool Armed { get; set; }

    private Exception Fire()
    {
        Armed = false;
        return makeException();
    }

    public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (Armed && stage == FaultStage.Insert && command.CommandText.Contains("INSERT INTO \"Orders\"", StringComparison.Ordinal))
        {
            throw Fire();
        }

        return ValueTask.FromResult(result);
    }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        if (Armed && stage == FaultStage.BeforeCommit)
        {
            throw Fire();
        }

        return ValueTask.FromResult(result);
    }

    public override Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (Armed && stage == FaultStage.AfterCommit)
        {
            throw Fire();
        }

        return Task.CompletedTask;
    }
}
