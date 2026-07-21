using Flowline.Core.Console;
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
/// Per-type classification lives in the resolvers this class composes: <see cref="DataverseSolutionProjectResolver"/>
/// for the <c>.cdsproj</c>, <see cref="PluginProjectResolver"/> for plugins, and
/// <see cref="WebResourcesProjectResolver"/> for WebResources — this class only reuses their list-based
/// logic instead of their file-reading entry points, so the rules are stated once and this facade adds
/// nothing but the single read and the caching.
/// </remarks>
public sealed class SolutionFileLayout
{
    /// <summary>Absolute path to the <c>.cdsproj</c> the solution file records.</summary>
    public string DataverseSolutionProjectPath { get; }

    /// <summary>Absolute path to the folder holding the <c>.cdsproj</c> and the unpacked solution source.</summary>
    public string DataverseSolutionFolder { get; }

    /// <summary>Every plugin-project candidate that survives the pre-filter (KTD4) — zero is a valid state.</summary>
    public IReadOnlyList<PluginProjectCandidate> PluginProjects { get; }

    /// <summary>Absolute path to the WebResources project.</summary>
    /// <remarks>
    /// Required (R5): a valid solution file always has one, so <see cref="LoadAsync"/> throws rather than
    /// returning a conventional path that may not exist — see <see cref="WebResourcesProjectResolver"/> for
    /// the detection rule.
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
    /// Also thrown, and <see cref="ExitCode.ConfigInvalid"/>, for the <c>.cdsproj</c> failures
    /// (<see cref="DataverseSolutionProjectResolver"/>) and the WebResources failures
    /// (<see cref="WebResourcesProjectResolver"/>) below.
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

        // Order matters: cdsproj first, so a missing/duplicate solution project throws before WebResources
        // detection ever runs. Plugins next, so their resolved paths can feed WebResources' exclusion set.
        var dataverseSolutionProjectPath = DataverseSolutionProjectResolver.Resolve(projects, slnFolder, solutionFileName);

        // Advisory, like every non-push consumer of plugin discovery: a project the solution file
        // references but that isn't on disk is dropped rather than thrown, because resolving the layout is
        // not about to build anything.
        var pluginProjects = PluginProjectResolver
                              .EnumerateCandidates(projects, slnFolder, reportMissingProject: _ => { })
                              .Where(c => PluginProjectResolver.DescribePreFilterSkip(c.ProjectPath) == null)
                              .ToList();

        var pluginProjectPaths = new HashSet<string>(
            pluginProjects.Select(p => p.ProjectPath), StringComparer.OrdinalIgnoreCase);

        var webResourcesProjectPath = WebResourcesProjectResolver.Resolve(projects, slnFolder, pluginProjectPaths, solutionFileName);

        return new SolutionFileLayout(dataverseSolutionProjectPath, pluginProjects, webResourcesProjectPath);
    }
}
