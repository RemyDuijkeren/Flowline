using System.Reflection;
using System.Text.RegularExpressions;
using Flowline.Core.Console;
using Flowline.Core.Models;

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

    /// <summary>Cheap pre-filter (KTD4): why this candidate can't be a plugin project, or <c>null</c> if it might be.</summary>
    /// <remarks>
    /// Building every solution project just to reflect and discard it is wasteful, so obvious
    /// non-candidates are dropped from the project file's own text before any build runs.
    ///
    /// The pre-filter's job is avoiding a pointless build, not classifying. A drop here is final — the
    /// project never reaches reflection, and since discovery also defines what the orphan sweeps treat as
    /// having local source, a wrong drop deletes a live assembly, its steps, and its Custom APIs. So it
    /// drops only when the project file's own text makes it certain, and defers to reflection otherwise:
    /// a plugin project whose SDK reference arrives from <c>Directory.Build.props</c> or transitively
    /// through a <c>ProjectReference</c> carries none of the marker strings in its own csproj, and
    /// dropping it on that absence is a guess. The target-framework check runs first and stays
    /// unconditional because <c>&lt;TargetFramework&gt;</c> is the project's own declaration — nothing a
    /// props file or a project reference adds can make a <c>net10.0</c> project a plugin project.
    /// </remarks>
    public static string? DescribePreFilterSkip(string projectFilePath)
    {
        var text = File.ReadAllText(projectFilePath);

        var frameworks = ReadTargetFrameworks(text);
        if (frameworks.Count > 0 && !frameworks.Any(f => f.StartsWith("net4", StringComparison.OrdinalIgnoreCase)))
            return $"targets {string.Join(", ", frameworks)}, not .NET Framework";

        // Microsoft.CrmSdk is in the list because that's the package name real projects reference
        // (Microsoft.CrmSdk.CoreAssemblies) — the Microsoft.Xrm.Sdk assembly it delivers never appears in
        // the csproj by name.
        string[] pluginSdkMarkers = ["Microsoft.Xrm.Sdk", "Microsoft.CrmSdk", "Flowline.Attributes"];
        if (pluginSdkMarkers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase)))
            return null;

        // No marker in this file. Both of these mean the SDK could still reach the project from text this
        // check never reads, so the filter can't be confident — hand it to reflection, which can be.
        if (text.Contains("<ProjectReference", StringComparison.OrdinalIgnoreCase))
            return null;

        if (HasDirectoryBuildProps(projectFilePath))
            return null;

        return "no Microsoft.Xrm.Sdk or Flowline.Attributes reference";
    }

    /// <summary>Whether a <c>Directory.Build.props</c> sits at or above the project folder.</summary>
    /// <remarks>
    /// Presence alone is the signal — this deliberately does not read the file. Anything it imports could
    /// add the SDK reference, and evaluating that properly means evaluating MSBuild, which discovery
    /// exists to avoid. Cheap and over-inclusive is the right shape: the cost of a false "might be" is one
    /// extra reflection pass, the cost of a false "definitely isn't" is a deleted plugin registration.
    /// </remarks>
    static bool HasDirectoryBuildProps(string projectFilePath)
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(projectFilePath))!);
             dir != null;
             dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Build.props")))
                return true;
        }

        return false;
    }

    static readonly Regex s_targetFrameworkElement =
        new(@"<TargetFrameworks?>([^<]*)</TargetFrameworks?>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static List<string> ReadTargetFrameworks(string projectText) =>
        s_targetFrameworkElement.Matches(projectText)
                                .SelectMany(m => m.Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                                .ToList();

    /// <summary>What to tell a user who needs this push to go through before they can fix the project.</summary>
    /// <remarks>
    /// Every "Flowline can't tell" failure carries it. Standalone mode runs no discovery at all — the user
    /// names the artifact and Flowline pushes it — so it is a real way out of every one of these, not a
    /// consolation. Refusing the push is only defensible when the refusal comes with the alternative.
    /// </remarks>
    internal const string StandaloneEscapeHatch =
        "To push right now, use standalone mode: flowline push --pluginFile <dll>.";

    /// <summary>
    /// Finds the assembly in a candidate's Release output that carries plugin types (R4/KD3, R2/KD2).
    /// </summary>
    /// <param name="candidate">The candidate to resolve, already built.</param>
    /// <param name="reportSkip">Receives one note per output assembly that turned out not to be the plugin assembly.</param>
    /// <returns>The confirmed assembly path, or <c>null</c> when nothing in the output is plugin-bearing (R3).</returns>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.ValidationFailed"/> when not one assembly in the output could be reflected, so
    /// "not a plugin project" was never established — only assumed.
    /// </exception>
    /// <remarks>
    /// Two very different things end up as "no plugin assembly here", and only one of them is safe.
    ///
    /// The assembly loaded and carries no <c>IPlugin</c> or <c>CodeActivity</c> type: that is R3, and it is
    /// the common case — WebResources, a shared DTO library, a test project. Definitively not a plugin
    /// project, <c>null</c>, and the caller skips it silently.
    ///
    /// Nothing loaded at all: that is not a verdict, it is a failure to reach one. The classic cause is
    /// <c>Microsoft.Xrm.Sdk.dll</c> not being copy-local, which makes a real plugin project's base types
    /// unresolvable and reports it as carrying none. Since the discovered set also defines what the orphan
    /// sweeps treat as having local source, guessing "not a plugin project" here deletes that project's
    /// live assembly, steps, and Custom APIs. So it throws: a refused push the user can report is strictly
    /// better than a half-baked one that silently deletes.
    ///
    /// "Any assembly loaded" is the bar, not "the project's own assembly loaded" — a dependency DLL in the
    /// same folder is routinely unreadable on its own, and demanding a per-assembly verdict would turn
    /// ordinary output folders into failures.
    /// </remarks>
    public static string? ResolvePluginAssembly(PluginProjectCandidate candidate, Action<string> reportSkip)
    {
        var reflectedSomething = false;

        foreach (var dllPath in FindOutputAssemblies(candidate))
        {
            if (ConfirmsPluginTypes(dllPath, out var failure))
                return dllPath;

            reflectedSomething |= failure == null;

            reportSkip(failure == null
                ? $"{ConsolePath.FormatRelativePath(dllPath)} — no IPlugin or CodeActivity type"
                : $"{ConsolePath.FormatRelativePath(dllPath)} — couldn't reflect it ({failure})");
        }

        // FindOutputAssemblies never returns empty — it throws instead — so this means every assembly there
        // failed to load.
        if (!reflectedSomething)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"Couldn't read a single assembly in '{candidate.ProjectName}' build output, so Flowline can't tell " +
                "whether it's a plugin project — and won't guess. Usually Microsoft.Xrm.Sdk.dll isn't copy-local next " +
                $"to the output. Run with --verbose for the reflection errors. {StandaloneEscapeHatch}");

        return null;
    }

    /// <summary>Every assembly in a candidate's Release output, best guess at the plugin assembly first.</summary>
    /// <param name="candidate">The candidate whose output to enumerate.</param>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.NotFound"/> when the project has no Release output at all.
    /// </exception>
    /// <remarks>
    /// Empty output is always an error, <c>--no-build</c> included. Reflection is the only thing that can
    /// say whether a candidate is a plugin project, and with nothing to reflect it never got to say —
    /// treating that as "not a plugin project" is a guess, and a guess here costs the project's live
    /// assembly, steps, and Custom APIs on the next orphan sweep. The user has two cheap ways out (build
    /// it, or drop <c>--no-build</c>) and a third that skips discovery entirely, so failing loudly is not
    /// a dead end.
    ///
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
                $"No Release build output for '{candidate.ProjectName}' — nothing to reflect, so Flowline can't tell " +
                "whether it's a plugin project. Build it first, or drop --no-build. " +
                $"Looked in {ConsolePath.FormatRelativePath(candidate.BuildOutputRoot)}. {StandaloneEscapeHatch}");

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
