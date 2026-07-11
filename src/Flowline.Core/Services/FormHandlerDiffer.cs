using Flowline.Core.Models;

namespace Flowline.Core.Services;

// FormHandler's Equals/GetHashCode are identity-only (FunctionName+LibraryName, R12 dedup key) so a
// Parameters-only change lands in both a desired and current set by identity — a plain set comparison
// (Except/SetEquals) can't tell "updated" from "unchanged". Shared by FormEventPlanner (equality check)
// and FormEventExecutor (dry-run added/updated/removed summary) so both compute this the same way.
static class FormHandlerDiffer
{
    public static (int Added, int Updated, int Removed) Diff(IReadOnlySet<FormHandler> desired, IReadOnlySet<FormHandler> current)
    {
        var (added, updated, removed) = DiffDetailed(desired, current);
        return (added.Count, updated.Count, removed.Count);
    }

    // Item-level variant of Diff, for callers that need to report exactly which handlers changed (e.g. the
    // executor's verbose/dry-run change report), not just counts.
    public static (List<FormHandler> Added, List<FormHandler> Updated, List<FormHandler> Removed) DiffDetailed(
        IReadOnlySet<FormHandler> desired, IReadOnlySet<FormHandler> current)
    {
        var currentByIdentity = current.ToDictionary(h => h);

        var added = new List<FormHandler>();
        var updated = new List<FormHandler>();
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
