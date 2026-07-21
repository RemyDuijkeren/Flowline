using Flowline.Core.Console;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

/// <summary>The exactly-one-<c>.cdsproj</c> rule (R7), operating on an already-parsed project list.</summary>
/// <remarks>
/// Split out of <see cref="SolutionFileLayout"/> so the rule is stated once and unit-tested without a
/// solution-file read (KD2). Same verdicts as before: zero or two-plus <c>.cdsproj</c> entries is
/// <see cref="ExitCode.ConfigInvalid"/>, a referenced-but-missing one is <see cref="ExitCode.NotFound"/>.
/// More than one is refused rather than resolved by picking: a Flowline project root holds exactly one
/// Dataverse solution, and quietly choosing one of two would sync and deploy the wrong solution without
/// ever saying so.
/// </remarks>
internal static class DataverseSolutionProjectResolver
{
    /// <summary>Absolute path to the <c>.cdsproj</c> the solution file records.</summary>
    /// <param name="projects">Project entries as read from the solution file.</param>
    /// <param name="slnFolder">The Flowline project root — the folder holding the solution file.</param>
    /// <param name="solutionFileName">The solution file's own name, for error messages.</param>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.ConfigInvalid"/> when the solution file records no <c>.cdsproj</c>, or more than
    /// one; <see cref="ExitCode.NotFound"/> when the one entry it names isn't on disk.
    /// </exception>
    internal static string Resolve(IReadOnlyList<MsBuildSolutionProject> projects, string slnFolder, string solutionFileName)
    {
        var candidates = projects.Where(p => p.IsCdsProject)
                                  .OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
                                  .ToList();

        if (candidates.Count == 0)
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"'{solutionFileName}' lists no .cdsproj, so Flowline can't tell which project packs the solution. " +
                "Add it with 'flowline sln add', or run 'clone' again.");

        if (candidates.Count > 1)
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"'{solutionFileName}' lists {candidates.Count} .cdsproj projects " +
                $"({string.Join(", ", candidates.Select(p => p.Name))}) — a Flowline project holds one solution. " +
                "Remove the extras from the solution file.");

        var dataverseSolutionProjectPath = Path.GetFullPath(Path.Combine(slnFolder, candidates[0].Path));

        if (!File.Exists(dataverseSolutionProjectPath))
            throw new FlowlineException(ExitCode.NotFound,
                $"'{solutionFileName}' references '{candidates[0].Name}' at " +
                $"{ConsolePath.FormatRelativePath(dataverseSolutionProjectPath, markup: false)}, but it isn't there. " +
                "Restore it, or remove it from the solution file and run 'clone' again.");

        return dataverseSolutionProjectPath;
    }
}
