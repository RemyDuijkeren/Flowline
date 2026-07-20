using Flowline.Core.Console;
using Flowline.Core.Models;

namespace Flowline.Core.Services;

/// <summary>
/// Resolves the solution package project (<c>.cdsproj</c>) and the WebResources project from
/// solution-file membership.
/// </summary>
/// <remarks>
/// The counterpart to <see cref="Flowline.Core.Plugins.PluginProjectResolver"/>, which does the same for
/// plugin projects. Together they are the whole of "which project is which" — no command composes a
/// project path from a folder or project name any more.
///
/// The two halves deliberately differ in how hard they fail, because what they cost differs. There is no
/// Flowline project without a package project — nothing to pack, nothing to deploy — so a missing one is
/// an error with a fix in it. The WebResources project is optional, and the paths that ask for it
/// (push, drift) must keep working in a repo Flowline has not finished scaffolding, so it degrades to the
/// conventional folder exactly the way plugin discovery does.
/// </remarks>
public static class ProjectLayoutResolver
{
    /// <summary>The SDK that identifies a Flowline WebResources project.</summary>
    /// <remarks>
    /// Identity, not filename: the project is <c>Microsoft.Build.NoTargets</c> because it compiles nothing
    /// and only drives the npm build. Matching on the SDK is what lets the file be named anything and live
    /// anywhere, which is the whole point of reading the solution file. No plugin project uses this SDK —
    /// a plugin project has to compile.
    /// </remarks>
    const string WebResourcesSdk = "Microsoft.Build.NoTargets";

    /// <summary>Absolute path to the <c>.cdsproj</c> the solution file records.</summary>
    /// <param name="slnFolder">The Flowline project root — the folder holding the solution file.</param>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.NotFound"/> when there is no solution file, or when the entry names a file that
    /// isn't on disk; <see cref="ExitCode.ConfigInvalid"/> when the solution file records no
    /// <c>.cdsproj</c>, or more than one.
    /// </exception>
    /// <remarks>
    /// Every failure here is a real dead end for the caller — sync and deploy have nothing to pack without
    /// it — so each one names the file and the way out rather than handing back a null path that fails
    /// later somewhere less obvious.
    ///
    /// More than one <c>.cdsproj</c> is refused rather than resolved by picking: a Flowline project root
    /// holds exactly one Dataverse solution, and quietly choosing one of two would sync and deploy the
    /// wrong solution without ever saying so.
    /// </remarks>
    public static async Task<string> ResolvePackageProjectAsync(string slnFolder, CancellationToken cancellationToken = default)
    {
        var reader = new MsBuildSolutionReader();
        var solutionFile = reader.FindSolutionFile(slnFolder)
                           ?? throw new FlowlineException(ExitCode.NotFound,
                               $"No solution file in {ConsolePath.FormatRelativePath(slnFolder)} — run 'clone' first.");

        var projects = await reader.ReadProjectsAsync(solutionFile, cancellationToken).ConfigureAwait(false);
        var packageProjects = projects.Where(p => p.IsCdsProject)
                                      .OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
                                      .ToList();

        var solutionFileName = Path.GetFileName(solutionFile);

        if (packageProjects.Count == 0)
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"'{solutionFileName}' lists no .cdsproj, so Flowline can't tell which project packs the solution. " +
                "Add it with 'flowline sln add', or run 'clone' again.");

        if (packageProjects.Count > 1)
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"'{solutionFileName}' lists {packageProjects.Count} .cdsproj projects " +
                $"({string.Join(", ", packageProjects.Select(p => p.Name))}) — a Flowline project holds one solution. " +
                "Remove the extras from the solution file.");

        var packageProjectPath = Path.GetFullPath(Path.Combine(slnFolder, packageProjects[0].Path));

        if (!File.Exists(packageProjectPath))
            throw new FlowlineException(ExitCode.NotFound,
                $"'{solutionFileName}' references '{packageProjects[0].Name}' at " +
                $"{ConsolePath.FormatRelativePath(packageProjectPath)}, but it isn't there. " +
                "Restore it, or remove it from the solution file and run 'clone' again.");

        return packageProjectPath;
    }

    /// <summary>Absolute path to the WebResources project, or the conventional one when nothing resolves.</summary>
    /// <param name="slnFolder">The Flowline project root — the folder holding the solution file.</param>
    /// <returns>
    /// The solution-referenced <see cref="WebResourcesSdk"/> project, or
    /// <see cref="ConventionalWebResourcesProject"/> when the folder holds no solution file or the solution
    /// file records no such project. The path is not guaranteed to exist.
    /// </returns>
    /// <remarks>
    /// Never throws. Its callers — push, the drift check, the deploy input-path scope — all already guard
    /// on the folder existing and all have to survive a repo that has no WebResources project at all, so
    /// there is nothing for a throw here to improve.
    /// </remarks>
    public static async Task<string> ResolveWebResourcesProjectAsync(string slnFolder, CancellationToken cancellationToken = default)
    {
        var reader = new MsBuildSolutionReader();
        var solutionFile = reader.FindSolutionFile(slnFolder);

        if (solutionFile == null)
            return ConventionalWebResourcesProject(slnFolder);

        var projects = await reader.ReadProjectsAsync(solutionFile, cancellationToken).ConfigureAwait(false);

        // A referenced project that isn't on disk is skipped, not reported: the WebResources project is
        // optional here, so "can't read it" and "isn't one" lead to the same place.
        return projects.Where(p => p.IsCsProject)
                       .Select(p => Path.GetFullPath(Path.Combine(slnFolder, p.Path)))
                       .Where(File.Exists)
                       .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                       .FirstOrDefault(DeclaresWebResourcesSdk)
               ?? ConventionalWebResourcesProject(slnFolder);
    }

    /// <summary>The pre-Flowline layout: a project named <c>WebResources</c> in a folder named <c>WebResources</c>.</summary>
    /// <remarks>
    /// The only place allowed to compose a WebResources path from a fixed name, and only as the fallback —
    /// mirrors <c>PluginProjectResolver.ConventionalCandidate</c> for the same reason: a partially
    /// scaffolded repo still works.
    /// </remarks>
    internal static string ConventionalWebResourcesProject(string slnFolder) =>
        Path.Combine(slnFolder, "WebResources", "WebResources.csproj");

    static bool DeclaresWebResourcesSdk(string projectPath) =>
        File.ReadAllText(projectPath).Contains(WebResourcesSdk, StringComparison.OrdinalIgnoreCase);
}
