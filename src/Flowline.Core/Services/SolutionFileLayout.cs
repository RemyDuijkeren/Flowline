using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Plugins;

namespace Flowline.Core.Services;

/// <summary>
/// Reads the solution file once and exposes every project Flowline resolves from it — the Dataverse
/// solution project, the plugin projects, and the WebResources project.
/// </summary>
/// <remarks>
/// The solution file (<c>.sln</c>/<c>.slnx</c>) is Flowline's folder configuration, the counterpart to
/// <c>.flowline</c>. Three separate call sites used to open it — the package resolver, plugin discovery,
/// the WebResources resolver — each re-reading the same file and applying its own rules. This class reads
/// it exactly once per <see cref="LoadAsync"/> and classifies every project from that one in-memory list,
/// so a command that needs several project types gets them from one instance instead of three parses.
///
/// Per-type classification still lives in the resolvers this class composes
/// (<see cref="PluginProjectResolver"/> for plugins; the <c>.cdsproj</c> and WebResources rules below, on
/// their way to becoming their own resolvers) — this class only reuses their list-based logic instead of
/// their file-reading entry points, so the rules are stated once and this facade adds nothing but the
/// single read and the caching.
/// </remarks>
public sealed class SolutionFileLayout
{
    /// <summary>The SDK that identifies a Flowline WebResources project.</summary>
    /// <remarks>
    /// Kept identical to <see cref="ProjectLayoutResolver"/>'s rule for this unit — hardening WebResources
    /// detection (elimination + weighted signals) is a later unit's job, not this facade's.
    /// </remarks>
    const string WebResourcesSdk = "Microsoft.Build.NoTargets";

    /// <summary>Absolute path to the <c>.cdsproj</c> the solution file records.</summary>
    public string DataverseSolutionProjectPath { get; }

    /// <summary>Absolute path to the folder holding the <c>.cdsproj</c> and the unpacked solution source.</summary>
    public string DataverseSolutionFolder { get; }

    /// <summary>Every plugin-project candidate that survives the pre-filter (KTD4) — zero is a valid state.</summary>
    public IReadOnlyList<PluginProjectCandidate> PluginProjects { get; }

    /// <summary>Absolute path to the WebResources project, or the conventional path when nothing resolves.</summary>
    /// <remarks>
    /// Unchanged behaviour for this unit: a later unit makes this required (R5) and tightens detection
    /// (R9). Here it stays a best-effort path that is not guaranteed to exist, exactly like
    /// <see cref="ProjectLayoutResolver.ResolveWebResourcesProjectAsync"/> today.
    /// </remarks>
    public string WebResourcesProjectPath { get; }

    SolutionFileLayout(
        string dataverseSolutionProjectPath,
        IReadOnlyList<PluginProjectCandidate> pluginProjects,
        string webResourcesProjectPath)
    {
        DataverseSolutionProjectPath = dataverseSolutionProjectPath;
        DataverseSolutionFolder = Path.GetDirectoryName(dataverseSolutionProjectPath)!;
        PluginProjects = pluginProjects;
        WebResourcesProjectPath = webResourcesProjectPath;
    }

    /// <summary>Reads the solution file at <paramref name="slnFolder"/> and classifies every project it lists.</summary>
    /// <param name="slnFolder">The Flowline project root — the folder holding the solution file.</param>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.NotFound"/> when the folder holds no solution file — the solution file is the
    /// config, so there is nothing to fall back to; stand-alone mode is the way to push without one.
    /// Also thrown, and <see cref="ExitCode.ConfigInvalid"/>, for the <c>.cdsproj</c> failures documented on
    /// the private resolver below.
    /// </exception>
    /// <remarks>
    /// <c>FindSolutionFile</c> and <c>ReadProjectsAsync</c> each run exactly once here, and every
    /// classification step below reads from the same <paramref name="projects"/> list rather than touching
    /// disk again — that single read is the point of this class existing (R4).
    /// </remarks>
    public static async Task<SolutionFileLayout> LoadAsync(string slnFolder, CancellationToken cancellationToken = default)
    {
        var reader = new MsBuildSolutionReader();
        var solutionFile = reader.FindSolutionFile(slnFolder)
                           ?? throw new FlowlineException(ExitCode.NotFound,
                               $"No solution file in {ConsolePath.FormatRelativePath(slnFolder)} — the solution file is Flowline's " +
                               $"config, so every command but 'clone' needs one. {PluginProjectResolver.StandaloneEscapeHatch}");

        var projects = await reader.ReadProjectsAsync(solutionFile, cancellationToken).ConfigureAwait(false);
        var solutionFileName = Path.GetFileName(solutionFile);

        var dataverseSolutionProjectPath = ResolveDataverseSolutionProject(projects, slnFolder, solutionFileName);

        // Advisory, like every non-push consumer of plugin discovery: a project the solution file
        // references but that isn't on disk is dropped rather than thrown, because resolving the layout is
        // not about to build anything.
        var pluginProjects = PluginProjectResolver
                              .EnumerateCandidates(projects, slnFolder, reportMissingProject: _ => { })
                              .Where(c => PluginProjectResolver.DescribePreFilterSkip(c.ProjectPath) == null)
                              .ToList();

        var webResourcesProjectPath = ResolveWebResourcesProject(projects, slnFolder);

        return new SolutionFileLayout(dataverseSolutionProjectPath, pluginProjects, webResourcesProjectPath);
    }

    /// <summary>The exactly-one-<c>.cdsproj</c> rule (R7), operating on an already-parsed project list.</summary>
    /// <remarks>
    /// Same verdicts as <see cref="ProjectLayoutResolver.ResolvePackageProjectAsync"/>: zero or two-plus
    /// <c>.cdsproj</c> entries is <see cref="ExitCode.ConfigInvalid"/>, a referenced-but-missing one is
    /// <see cref="ExitCode.NotFound"/>. Copied rather than called because that method re-reads the solution
    /// file, which is exactly what this class exists to stop doing.
    /// </remarks>
    static string ResolveDataverseSolutionProject(
        IReadOnlyList<MsBuildSolutionProject> projects, string slnFolder, string solutionFileName)
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
                $"{ConsolePath.FormatRelativePath(dataverseSolutionProjectPath)}, but it isn't there. " +
                "Restore it, or remove it from the solution file and run 'clone' again.");

        return dataverseSolutionProjectPath;
    }

    /// <summary>The current (un-hardened) WebResources rule, operating on an already-parsed project list.</summary>
    /// <remarks>
    /// Deliberately identical to <see cref="ProjectLayoutResolver.ResolveWebResourcesProjectAsync"/>'s
    /// substring-on-SDK check and conventional-path fallback — this unit only stops it from re-reading the
    /// solution file. Making it required and replacing the substring with elimination and weighted signals
    /// is a later unit (R5/R9).
    /// </remarks>
    static string ResolveWebResourcesProject(IReadOnlyList<MsBuildSolutionProject> projects, string slnFolder) =>
        projects.Where(p => p.IsCsProject)
                .Select(p => Path.GetFullPath(Path.Combine(slnFolder, p.Path)))
                .Where(File.Exists)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(DeclaresWebResourcesSdk)
        ?? ConventionalWebResourcesProject(slnFolder);

    /// <summary>The pre-Flowline layout: a project named <c>WebResources</c> in a folder named <c>WebResources</c>.</summary>
    static string ConventionalWebResourcesProject(string slnFolder) =>
        Path.Combine(slnFolder, "WebResources", "WebResources.csproj");

    // An unreadable candidate (locked by the IDE, deleted between the File.Exists check and here) reads as
    // "not the WebResources project", not a failure — this whole resolution step is best-effort for now.
    static bool DeclaresWebResourcesSdk(string projectPath)
    {
        try
        {
            return File.ReadAllText(projectPath).Contains(WebResourcesSdk, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
