using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Storefront.Api.Tests.TestSupport;

/// <summary>
/// Runs an action just before a context's first SaveChanges: after the checkout service has done its
/// "is there already an order for this key?" check, but before it inserts. Lets a test make another
/// request win the race deterministically, instead of hoping parallel tasks interleave that way.
/// </summary>
internal sealed class BeforeFirstSave(Func<Task> action) : SaveChangesInterceptor
{
    private bool _done;

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (!_done)
        {
            _done = true;
            await action();
        }

        return result;
    }
}
