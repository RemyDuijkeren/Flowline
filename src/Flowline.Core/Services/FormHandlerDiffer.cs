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
        var currentByIdentity = current.ToDictionary(h => h);

        int added = 0, updated = 0;
        foreach (var handler in desired)
        {
            if (!currentByIdentity.TryGetValue(handler, out var match))
                added++;
            else if (match.HandlerUniqueId != handler.HandlerUniqueId || match.Parameters != handler.Parameters)
                updated++;
        }

        var removed = current.Except(desired).Count();
        return (added, updated, removed);
    }
}
