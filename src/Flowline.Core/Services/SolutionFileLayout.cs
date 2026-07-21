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
/// Per-type classification lives in the resolvers this class composes: <see cref="DataverseSolutionProjectResolver"/>
/// for the <c>.cdsproj</c>, <see cref="PluginProjectResolver"/> for plugins, and
/// <see cref="WebResourcesProjectResolver"/> for WebResources — this class only reuses their list-based
/// logic instead of their file-reading entry points, so the rules are stated once and this facade adds
/// nothing but the single read and the caching.
///
/// <b>Per-type resolution is lazy.</b> <see cref="LoadAsync"/> reads and parses the file once (and throws
/// there if it is missing — R6, the one precondition every command shares), but each project type is
/// resolved and verified only when its property is first read, then cached. So a command touches only the
/// validation it needs: <c>generate</c> reads <see cref="PluginProjects"/> and never triggers the
/// WebResources resolution (R5), while <c>deploy</c> reads all three and validates the whole layout. Coupling a
/// codegen command to a WebResources misconfiguration it never uses would be the wrong kind of loud.
/// </remarks>
public sealed class SolutionFileLayout
{
    readonly IReadOnlyList<MsBuildSolutionProject> _projects;
    readonly string _slnFolder;
    readonly string _solutionFileName;
    readonly Lazy<string> _dataverseSolutionProjectPath;
    readonly Lazy<IReadOnlyList<PluginProjectCandidate>> _pluginProjects;
    readonly Lazy<string?> _webResourcesProjectPath;

    /// <summary>Absolute path to the <c>.cdsproj</c> the solution file records. Resolved on first access (R7).</summary>
    public string DataverseSolutionProjectPath => _dataverseSolutionProjectPath.Value;

    /// <summary>Absolute path to the folder holding the <c>.cdsproj</c> and the unpacked solution source.</summary>
    public string DataverseSolutionFolder => Path.GetDirectoryName(DataverseSolutionProjectPath)!;

    /// <summary>Every plugin-project candidate that survives the pre-filter (KTD4) — zero is a valid state.</summary>
    public IReadOnlyList<PluginProjectCandidate> PluginProjects => _pluginProjects.Value;

    /// <summary>Absolute path to the WebResources project, or <c>null</c> when none is confidently identified. Resolved on first access.</summary>
    /// <remarks>
    /// <c>null</c> is a legitimate (if unusual) state — no confident WebResources project (a plugin-only or
    /// migrated repo); consumers skip web-resource work with a loud warning. A genuine tie still throws on
    /// access — see <see cref="WebResourcesProjectResolver"/> for the rule. Its exclusion set is the
    /// resolved plugin projects, so reading this resolves <see cref="PluginProjects"/> too.
    /// </remarks>
    public string? WebResourcesProjectPath => _webResourcesProjectPath.Value;

    SolutionFileLayout(IReadOnlyList<MsBuildSolutionProject> projects, string slnFolder, string solutionFileName)
    {
        _projects = projects;
        _slnFolder = slnFolder;
        _solutionFileName = solutionFileName;

        _dataverseSolutionProjectPath = new Lazy<string>(
            () => DataverseSolutionProjectResolver.Resolve(_projects, _slnFolder, _solutionFileName));

        // Advisory, like every non-push consumer of plugin discovery: a project the solution file
        // references but that isn't on disk is dropped rather than thrown, because resolving the layout is
        // not about to build anything.
        _pluginProjects = new Lazy<IReadOnlyList<PluginProjectCandidate>>(
            () => PluginProjectResolver
                  .EnumerateCandidates(_projects, _slnFolder, reportMissingProject: _ => { })
                  .Where(c => PluginProjectResolver.DescribePreFilterSkip(c.ProjectPath) == null)
                  .ToList());

        _webResourcesProjectPath = new Lazy<string?>(() =>
        {
            var pluginPaths = new HashSet<string>(PluginProjects.Select(p => p.ProjectPath), StringComparer.OrdinalIgnoreCase);
            return WebResourcesProjectResolver.Resolve(_projects, _slnFolder, pluginPaths, _solutionFileName);
        });
    }

    /// <summary>Reads the solution file at <paramref name="slnFolder"/> once; classifies each project on demand.</summary>
    /// <param name="slnFolder">The Flowline project root — the folder holding the solution file.</param>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.NotFound"/> when the folder holds no solution file — the solution file is the
    /// config, so there is nothing to fall back to; stand-alone mode is the way to push without one. The
    /// per-type failures (<see cref="DataverseSolutionProjectResolver"/>, <see cref="WebResourcesProjectResolver"/>)
    /// surface when their properties are read, not here.
    /// </exception>
    /// <remarks>
    /// <c>FindSolutionFile</c> and <c>ReadProjectsAsync</c> each run exactly once here; every later
    /// classification reads the same in-memory list rather than touching disk again (R4).
    /// </remarks>
    public static async Task<SolutionFileLayout> LoadAsync(string slnFolder, CancellationToken cancellationToken = default)
    {
        var reader = new MsBuildSolutionReader();
        var solutionFile = reader.FindSolutionFile(slnFolder)
                           ?? throw new FlowlineException(ExitCode.NotFound,
                               $"No solution file in {ConsolePath.FormatRelativePath(slnFolder, markup: false)} — the solution file is Flowline's " +
                               $"config, so every command but 'clone' needs one. {PluginProjectResolver.StandaloneEscapeHatch}");

        var projects = await reader.ReadProjectsAsync(solutionFile, cancellationToken).ConfigureAwait(false);
        return new SolutionFileLayout(projects, slnFolder, Path.GetFileName(solutionFile));
    }
}
