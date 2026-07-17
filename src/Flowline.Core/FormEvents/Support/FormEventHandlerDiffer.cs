using Flowline.Core.Models;

namespace Flowline.Core.FormEvents.Support;

// FormEventHandler's Equals/GetHashCode are identity-only (FunctionName+LibraryName, R12 dedup key) so a
// Parameters-only change lands in both a desired and current set by identity — a plain set comparison
// (Except/SetEquals) can't tell "updated" from "unchanged". Shared by FormEventPlanner (equality check)
// and FormEventExecutor (dry-run added/updated/removed summary) so both compute this the same way.
static class FormEventHandlerDiffer
{
    public static (int Added, int Updated, int Removed) Diff(IReadOnlySet<FormEventHandler> desired, IReadOnlySet<FormEventHandler> current)
    {
        var (added, updated, removed) = DiffDetailed(desired, current);
        return (added.Count, updated.Count, removed.Count);
    }

    // Item-level variant of Diff, for callers that need to report exactly which handlers changed (e.g. the
    // executor's verbose/dry-run change report), not just counts.
    public static (List<FormEventHandler> Added, List<FormEventHandler> Updated, List<FormEventHandler> Removed) DiffDetailed(
        IReadOnlySet<FormEventHandler> desired, IReadOnlySet<FormEventHandler> current)
    {
        var currentByIdentity = current.ToDictionary(h => h);

        var added = new List<FormEventHandler>();
        var updated = new List<FormEventHandler>();
        foreach (var handler in desired)
        {
            if (!currentByIdentity.TryGetValue(handler, out var match))
                added.Add(handler);
            else if (match.HandlerUniqueId != handler.HandlerUniqueId || match.Parameters != handler.Parameters)
                updated.Add(handler);
        }

        var removed = current.Except(desired).ToList();
        return (added, updated, removed);
    }
}
