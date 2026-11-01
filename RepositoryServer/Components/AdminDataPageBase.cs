using Microsoft.AspNetCore.Components;

namespace OpenShock.RepositoryServer.Components;

/// <summary>
/// An admin page that lists rows and changes them: the catalog pages, the publishers, the webhooks.
/// </summary>
/// <remarks>
/// Only the scoping and reload brackets live here. Every page kept its own markup, its own fields
/// and its own service calls, because those are what distinguish the pages from each other - the
/// two things they genuinely shared were "open a scope, read everything, clear the spinner" and
/// "open a scope, change one thing, read everything again", both of which were written out by hand
/// nine times.
/// </remarks>
public abstract class AdminDataPageBase : AdminPageBase
{
    [Inject] private ILogger<AdminDataPageBase> Logger { get; set; } = null!;

    /// <summary>
    /// Whether a read is in flight. Bind the grid's <c>IsLoading</c> to this rather than tracking a
    /// flag per page.
    /// </summary>
    protected bool Loading { get; private set; } = true;

    /// <summary>
    /// Whether the last read failed. Pass it to <c>TableFrame</c>, so that a failed read is not drawn
    /// as a table with no rows in it.
    /// </summary>
    protected bool LoadFailed { get; private set; }

    /// <summary>
    /// Reads everything the page shows, into the page's own fields. The scope is opened and disposed
    /// around this call, so take every service this page needs from it: they then share a DbContext,
    /// which is the reason <see cref="AdminPageBase.CreateScope"/> exists.
    /// </summary>
    protected abstract Task LoadDataAsync(AsyncServiceScope scope);

    protected override Task OnInitializedAsync() => ReloadAsync();

    /// <summary>Re-reads the page. Safe to call from anywhere that changed something.</summary>
    protected async Task ReloadAsync()
    {
        Loading = true;
        LoadFailed = false;

        try
        {
            await using var scope = CreateScope();
            await LoadDataAsync(scope);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // An exception escaping a lifecycle method tears down the whole circuit, which turns a
            // database blip into a dead page. Stay up and offer the retry instead.
            Logger.LogError(ex, "Failed to load an admin page");
            LoadFailed = true;
        }
        finally
        {
            // In a finally because the per-page versions of this were not: a read that threw left
            // the flag set, and the grid then span forever on a page that had already given up.
            Loading = false;
        }
    }

    /// <summary>
    /// Runs one change in a scope of its own and re-reads the page afterwards.
    /// </summary>
    /// <remarks>
    /// Validation that can refuse without touching a service belongs before the call, not inside:
    /// a refusal must not cost a scope or a reload. The scope is disposed before the reload opens
    /// its own, so a change and the read that follows it never share a DbContext.
    /// </remarks>
    protected async Task MutateAsync(Func<AsyncServiceScope, Task> change)
    {
        await using (var scope = CreateScope())
        {
            await change(scope);
        }

        // Re-read rather than patch the row in place: a change can move rows the page is not
        // looking at - a discontinued board, a deleted publisher's provenance - and re-reading is
        // what each page already did by hand.
        await ReloadAsync();
    }
}
