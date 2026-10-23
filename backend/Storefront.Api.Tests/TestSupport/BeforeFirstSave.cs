using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Storefront.Api.Tests.TestSupport;

/// <summary>
/// Runs an action just before a context's first write transaction begins: after the checkout service
/// has done its "is there already an order for this key?" check, but before it inserts. Lets a test make
/// another request win the race deterministically, instead of hoping parallel tasks interleave that way.
/// (It runs before BEGIN, not inside the transaction, so the other request isn't blocked by this one's lock.)
/// </summary>
internal sealed class BeforeFirstSave(Func<Task> action) : DbTransactionInterceptor
{
    private bool _done;

    public override async ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        if (!_done)
        {
            _done = true;
            await action();
        }

        return result;
    }
}
