namespace Flowline.Core.Models;

public enum RunMode { Normal, NoDelete, DryRun }

public static class RunModeExtensions
{
    // Explicit NoDelete-or-DryRun rather than "!= Normal" so a future 4th RunMode value doesn't
    // silently become report-only by default.
    public static bool IsReportOnly(this RunMode mode) => mode is RunMode.NoDelete or RunMode.DryRun;
}
