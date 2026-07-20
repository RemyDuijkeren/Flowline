using System.Reflection;
using System.Text.RegularExpressions;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;

namespace Flowline.Core.Plugins;

/// <summary>A <c>.csproj</c> the solution file references, before anything is known about what it builds.</summary>
/// <param name="ProjectPath">Absolute path to the project file.</param>
/// <param name="ProjectName">The name the solution file records for it — what the user sees in messages.</param>
/// <param name="BuildOutputRoot">Absolute path to the project's <c>bin/Release</c> folder.</param>
public sealed record PluginProjectCandidate(string ProjectPath, string ProjectName, string BuildOutputRoot);

/// <summary>
/// Discovers plugin projects from solution-file membership and locates what each one actually built.
/// </summary>
/// <remarks>
/// Replaces three hardcoded assumptions the push path used to make: that the plugin project lives in a
/// folder named <c>Plugins</c>, that its assembly is named <c>Plugins</c>, and that the build drops it at
/// <c>bin/Release/net462/publish/</c>. None of the three hold for a project that sets its own
/// <c>&lt;AssemblyName&gt;</c> and is built with a plain <c>dotnet build</c> (no packaging, no
/// <c>publish</c> subfolder) — a shape real legacy plugin projects have.
///
/// The replacement is deliberately not "parse MSBuild". Discovery reads the solution file for candidates
/// (KD1), searches each candidate's own <c>bin/Release</c> for what landed there, and lets reflection say
/// which of those is the plugin assembly (KD2). That resolves any assembly name and any output shape
/// without evaluating props files.
/// </remarks>
public static class PluginProjectResolver
{
    /// <summary>Every <c>.csproj</c> the solution file references, as a plugin-project candidate (R1/KD1).</summary>
    /// <param name="projects">Project entries as read from the solution file.</param>
    /// <param name="solutionFolder">Folder holding the solution file — project paths are relative to it.</param>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.NotFound"/> when the solution file references a project that isn't on disk.
    /// Silently dropping it would read exactly like "your plugin project isn't registered".
    /// </exception>
    /// <remarks>
    /// Membership, not a folder glob: the solution file is already the authoritative "what's in this
    /// project" list, so a stray or experimental csproj is skipped by construction. Ordering is by path so
    /// the candidate list doesn't depend on how the solution file happens to be written.
    /// </remarks>
    /// <param name="reportMissingProject">
    /// How to handle a project the solution file references but that isn't on disk — the state a deleted
    /// project folder leaves behind when the solution entry survives it.
    /// <para>
    /// <c>null</c> (the default) throws. That is right for <c>push</c>, which is about to build and
    /// register these projects: a solution file that lies about its contents should stop the run.
    /// </para>
    /// <para>
    /// Supply a callback to skip the project and report it instead. That is right for advisory and
    /// packaging paths — <c>deploy</c> packs from the snapshot and never builds a plugin project, so
    /// failing a production deploy over a stale entry for an unrelated project is disproportionate, and
    /// a drift warning that throws is worse than one that reports on what it can see.
    /// </para>
    /// </param>
    public static IReadOnlyList<PluginProjectCandidate> EnumerateCandidates(
        IReadOnlyList<MsBuildSolutionProject> projects,
        string solutionFolder,
        Action<string>? reportMissingProject = null)
    {
        var candidates = new List<PluginProjectCandidate>();

        foreach (var project in projects.Where(p => p.IsCsProject))
        {
            var projectPath = Path.GetFullPath(Path.Combine(solutionFolder, project.Path));

            if (!File.Exists(projectPath))
            {
                var message = $"The solution file references '{project.Name}' at {ConsolePath.FormatRelativePath(projectPath)}, " +
                              "but it isn't there. Restore the project, or remove it from the solution file.";

                if (reportMissingProject == null)
                    throw new FlowlineException(ExitCode.NotFound, message);

                reportMissingProject(message);
                continue;
            }

            candidates.Add(new PluginProjectCandidate(
                projectPath,
                project.Name,
                Path.Combine(Path.GetDirectoryName(projectPath)!, "bin", "Release")));
        }

        return candidates.OrderBy(c => c.ProjectPath, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The plugin projects of a Flowline project folder, for consumers that only need the list.</summary>
    /// <param name="slnFolder">The Flowline project root — the folder holding the solution file.</param>
    /// <returns>
    /// Solution-referenced projects that survive <see cref="DescribePreFilterSkip"/>, or the single
    /// conventional <c>Plugins/</c> candidate when the folder holds no solution file.
    /// </returns>
    /// <remarks>
    /// One entry point for every consumer that needs "which projects are the plugin projects" without
    /// running a build — the drift check, the deploy input-path scope, and namespace derivation. Push does
    /// not use it: push reports each pre-filter drop under <c>--verbose</c> and must fail loudly when the
    /// solution file is missing, so it drives <see cref="EnumerateCandidates"/> itself.
    ///
    /// The no-solution-file fallback is what keeps a partially-set-up repo working. Those consumers all run
    /// on paths that must survive a folder Flowline has not finished scaffolding, and a drift check that
    /// throws is worse than one that checks the conventional folder. The fallback candidate is returned
    /// unbuilt and unverified — callers already guard on the paths existing, which is exactly the behaviour
    /// they had before discovery landed.
    ///
    /// A solution file referencing a project that isn't on disk — a deleted project folder whose solution
    /// entry outlived it — is reported and skipped here rather than thrown, because none of these consumers
    /// is about to build that project. Push, which is, keeps the throw. Blocking a production deploy over a
    /// stale entry for a project the deploy never touches is the wrong trade; push failing on it is the
    /// right one.
    /// </remarks>
    public static async Task<IReadOnlyList<PluginProjectCandidate>> DiscoverAsync(
        string slnFolder,
        Action<string>? reportMissingProject = null,
        CancellationToken cancellationToken = default)
    {
        var reader = new MsBuildSolutionReader();
        var solutionFile = reader.FindSolutionFile(slnFolder);

        if (solutionFile == null)
            return [ConventionalCandidate(slnFolder)];

        var projects = await reader.ReadProjectsAsync(solutionFile, cancellationToken).ConfigureAwait(false);

        return EnumerateCandidates(projects, slnFolder, reportMissingProject ?? (_ => { }))
               .Where(c => DescribePreFilterSkip(c.ProjectPath) == null)
               .ToList();
    }

    /// <summary>The pre-discovery layout: a project named <c>Plugins</c> in a folder named <c>Plugins</c>.</summary>
    /// <remarks>
    /// The only place in the codebase allowed to compose a plugin path from a fixed project name, and only
    /// as the no-solution-file fallback. <c>PluginPathConventionTests</c> enforces that.
    /// </remarks>
    internal static PluginProjectCandidate ConventionalCandidate(string slnFolder) =>
        new(Path.Combine(slnFolder, "Plugins", "Plugins.csproj"),
            "Plugins",
            Path.Combine(slnFolder, "Plugins", "bin", "Release"));

    /// <summary>Cheap pre-filter (KTD4): why this candidate can't be a plugin project, or <c>null</c> if it might be.</summary>
    /// <remarks>
    /// Building every solution project just to reflect and discard it is wasteful, so obvious
    /// non-candidates are dropped from the project file's own text before any build runs. Deliberately
    /// textual and deliberately generous — it reads the csproj only, so a reference or target framework
    /// inherited from a props file isn't visible here, and both checks are written to let an unknown shape
    /// through rather than drop it. Every drop is surfaced under <c>--verbose</c> by the caller, because a
    /// silent one is indistinguishable from "Flowline didn't register my plugin".
    /// </remarks>
    public static string? DescribePreFilterSkip(string projectFilePath)
    {
        var text = File.ReadAllText(projectFilePath);

        // Microsoft.CrmSdk is in the list because that's the package name real projects reference
        // (Microsoft.CrmSdk.CoreAssemblies) — the Microsoft.Xrm.Sdk assembly it delivers never appears in
        // the csproj by name.
        string[] pluginSdkMarkers = ["Microsoft.Xrm.Sdk", "Microsoft.CrmSdk", "Flowline.Attributes"];
        if (!pluginSdkMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return "no Microsoft.Xrm.Sdk or Flowline.Attributes reference";

        var frameworks = ReadTargetFrameworks(text);
        if (frameworks.Count > 0 && !frameworks.Any(f => f.StartsWith("net4", StringComparison.OrdinalIgnoreCase)))
            return $"targets {string.Join(", ", frameworks)}, not .NET Framework";

        return null;
    }

    static readonly Regex s_targetFrameworkElement =
        new(@"<TargetFrameworks?>([^<]*)</TargetFrameworks?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static List<string> ReadTargetFrameworks(string projectText) =>
        s_targetFrameworkElement.Matches(projectText)
                                .SelectMany(m => m.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                .ToList();

    /// <summary>
    /// Finds the assembly in a candidate's Release output that carries plugin types (R4/KD3, R2/KD2).
    /// </summary>
    /// <param name="candidate">The candidate to resolve, already built.</param>
    /// <param name="reportSkip">Receives one note per output assembly that turned out not to be the plugin assembly.</param>
    /// <returns>The confirmed assembly path, or <c>null</c> when nothing in the output is plugin-bearing (R3).</returns>
    /// <remarks>
    /// Returning <c>null</c> rather than throwing is R3: a solution project that builds no plugin types is
    /// not an error, it's just not a plugin project. Not finding any build output at all <em>is</em> an
    /// error — see <see cref="FindOutputAssemblies"/>.
    /// </remarks>
    public static string? ResolvePluginAssembly(PluginProjectCandidate candidate, Action<string> reportSkip)
    {
        foreach (var dllPath in FindOutputAssemblies(candidate))
        {
            if (ConfirmsPluginTypes(dllPath, out var failure))
                return dllPath;

            reportSkip(failure == null
                ? $"{ConsolePath.FormatRelativePath(dllPath)} — no IPlugin or CodeActivity type"
                : $"{ConsolePath.FormatRelativePath(dllPath)} — couldn't reflect it ({failure})");
        }

        return null;
    }

    /// <summary>Every assembly in a candidate's Release output, best guess at the plugin assembly first.</summary>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.NotFound"/> when the project has no Release output — reflection needs something
    /// to read, and "unbuilt" must say so rather than resolve to nothing and look like "not a plugin project".
    /// </exception>
    /// <remarks>
    /// Ordering carries the whole optimisation: an assembly named after the project file goes first, so the
    /// common case confirms on the first reflection instead of walking the dependency closure. A project
    /// with its own <c>&lt;AssemblyName&gt;</c> simply has no such match and falls back to reflecting in
    /// path order, which still resolves it — just not on the first try.
    /// </remarks>
    public static IReadOnlyList<string> FindOutputAssemblies(PluginProjectCandidate candidate)
    {
        var dllPaths = Directory.Exists(candidate.BuildOutputRoot)
            ? Directory.GetFiles(candidate.BuildOutputRoot, "*.dll", SearchOption.AllDirectories)
            : [];

        if (dllPaths.Length == 0)
            throw new FlowlineException(ExitCode.NotFound,
                $"No Release build output for '{candidate.ProjectName}' — build it first, or drop --no-build. " +
                $"Looked in {ConsolePath.FormatRelativePath(candidate.BuildOutputRoot)}.");

        var expectedName = Path.GetFileNameWithoutExtension(candidate.ProjectPath);

        return dllPaths
               // dotnet publish copies the dependency closure into net462/publish/, so the same assembly
               // shows up twice. One entry per filename, preferring the publish/ copy — that's the exact
               // file the old fixed path pointed at, which keeps the default shape resolving as it did.
               .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
               .Select(g => g.OrderByDescending(IsUnderPublishFolder)
                             .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                             .First())
               .OrderByDescending(p => string.Equals(Path.GetFileNameWithoutExtension(p), expectedName, StringComparison.OrdinalIgnoreCase))
               .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
               .ToList();
    }

    static bool IsUnderPublishFolder(string dllPath) =>
        Path.GetDirectoryName(dllPath) is { } dir &&
        string.Equals(Path.GetFileName(dir), "publish", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reflects one assembly and reports whether it carries plugin types (KD2/KTD2).</summary>
    /// <param name="failure">Why the assembly couldn't be reflected at all, or <c>null</c> when it was read fine.</param>
    /// <remarks>
    /// A dependency DLL sitting in the same output folder may well be unreadable here — a native image, or
    /// a managed assembly whose own references aren't beside it. That is not a push failure: it just means
    /// this file isn't the plugin assembly. The reason is handed back rather than swallowed so the caller
    /// can print it under <c>--verbose</c> (KTD4).
    /// </remarks>
    public static bool ConfirmsPluginTypes(string dllPath, out string? failure)
    {
        try
        {
            // Dedupe by filename, not by path: the resolver paths span both net462/ and net462/publish/,
            // which hold the same assemblies, and PathAssemblyResolver rejects a duplicate simple name.
            var resolver = new PathAssemblyResolver(
                PluginAssemblyReader.BuildResolverPaths(dllPath)
                                    .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                                    .Select(g => g.First()));
            using var mlc = new MetadataLoadContext(resolver);

            failure = null;
            return PluginTypeMetadataScanner.ContainsPluginTypes(mlc.LoadFromAssemblyPath(dllPath));
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException
                                       or TypeLoadException or ReflectionTypeLoadException)
        {
            failure = ex.Message;
            return false;
        }
    }
}
