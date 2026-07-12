namespace Flowline.Core.Services;

// Shared by WebResourceService and FormEventExecutor's plan-report writers — both render an always-
// visible summary line plus optional verbose per-item detail, keyed on the same Verbose/DryRun split.
enum PlanReportMode { Verbose, DryRun }

static class PlanReportFormatting
{
    public static string JoinCounts(params (int Count, string Label)[] parts)
    {
        var nonZero = parts.Where(p => p.Count > 0).Select(p => $"{p.Count} {p.Label}").ToList();
        return nonZero.Count > 0 ? string.Join(", ", nonZero) : "no changes";
    }
}
