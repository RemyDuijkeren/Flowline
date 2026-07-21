using System.Text.Json;
using System.Text.RegularExpressions;
using Flowline.Core.Models;
using Flowline.Core.Plugins;

namespace Flowline.Core.Services;

/// <summary>
/// Identifies the WebResources project among the solution file's <c>.csproj</c> entries by elimination
/// plus weighted content signals (R9, KD3) — the hardened replacement for the substring check this used
/// to be.
/// </summary>
/// <remarks>
/// <b>Elimination first.</b> A candidate is any solution-referenced, on-disk <c>.csproj</c> that isn't the
/// Dataverse solution project (already excluded — <see cref="MsBuildSolutionProject.IsCsProject"/> never
/// matches <c>.cdsproj</c>), isn't a plugin project (<paramref name="pluginProjectPaths"/>, resolved
/// upstream so plugin-ness isn't re-derived here), isn't a PCF control
/// (<see cref="PcfProjectResolver.IsPcfProject"/>, R10), and isn't a test project. One exception: a
/// candidate carrying a <b>strong</b> WebResources signal (NoTargets SDK, suppressed compile, a
/// <c>dist/</c> folder, or a Flowline annotation) is kept even if it landed in
/// <paramref name="pluginProjectPaths"/> — that pre-filter set is deliberately over-inclusive (a
/// TargetFramework-less project under a shared <c>Directory.Build.props</c> falls into it), and a real
/// plugin never carries any of those signals, so this can only rescue a genuine WebResources project.
///
/// <b>Weighted signals rank survivors and enforce a floor.</b> Each candidate's own text and project
/// directory (never MSBuild evaluation — R3) score points: a Flowline-annotation hit is very strong (3,
/// positive proof of Flowline-managed web resources, not just an arbitrary JS project); a suppressed
/// compile or a <c>dist/</c> folder is strong (2 each); a build-tooling or convention signal is medium (1
/// each). The resolver takes the unique top score, but the winner must score <b>at least 1</b> — a real
/// WebResources project always carries a signal, so a zero-signal winner (a plain library that merely
/// survived elimination) throws <see cref="ExitCode.ConfigInvalid"/> rather than being returned as the
/// WebResources project and silently driving the deploy drift gate off a <c>dist/</c> that doesn't exist.
/// Two-plus candidates tied at the top score throw rather than guess (KD4) — never an alphabetical pick.
/// </remarks>
internal static class WebResourcesProjectResolver
{
    const int VeryStrongWeight = 3;
    const int StrongWeight = 2;
    const int MediumWeight = 1;

    /// <summary>The <c>flowline:</c> annotation comment marker the web-resource pipeline already parses.</summary>
    /// <remarks>
    /// Matches the <c>//</c>, <c>//!</c>, and <c>/*!</c> comment forms
    /// <see cref="Flowline.Core.FormEvents.Support.FormEventAnnotationParser"/> recognizes, loosely — this
    /// only needs to prove the file is Flowline-managed, not parse the directive grammar.
    /// </remarks>
    static readonly Regex s_flowlineAnnotation = new(@"(?://!?|/\*!)\s*flowline:", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    static readonly string[] s_testSdkMarkers = ["Microsoft.NET.Test.Sdk", "xunit", "nunit", "MSTest"];
    static readonly string[] s_bundlerConfigPatterns = ["rollup.config.*", "webpack.*", "vite.*"];
    static readonly string[] s_webAssetExtensions = [".ts", ".js", ".html", ".css"];
    static readonly string[] s_annotationScanExtensions = [".ts", ".js"];

    // Folders a Flowline WebResources project routinely gains once it's built (node_modules) or compiled
    // (bin/obj) — none of them can hold the project's own source, so descending into them wastes a scan a
    // node_modules tree can make arbitrarily large.
    static readonly string[] s_excludedScanDirs = ["node_modules", "bin", "obj"];

    /// <summary>Absolute path to the WebResources project.</summary>
    /// <param name="projects">Project entries as read from the solution file.</param>
    /// <param name="slnFolder">The Flowline project root — the folder holding the solution file.</param>
    /// <param name="pluginProjectPaths">
    /// Absolute paths of every already-resolved plugin project, compared case-insensitively (matching
    /// every other path comparison in this codebase — <see cref="MsBuildSolutionReader.PathEquals"/>).
    /// </param>
    /// <param name="solutionFileName">The solution file's own name, for error messages.</param>
    /// <exception cref="FlowlineException">
    /// <see cref="ExitCode.ConfigInvalid"/> when no candidate survives elimination (R5 — a WebResources
    /// project is required), when the surviving winner carries no positive signal (a zero-signal false
    /// positive, not a real WebResources project), or when two-plus tie at the top score (R9 — never
    /// resolved by picking).
    /// </exception>
    internal static string Resolve(
        IReadOnlyList<MsBuildSolutionProject> projects,
        string slnFolder,
        IReadOnlySet<string> pluginProjectPaths,
        string solutionFileName)
    {
        var candidates = projects
            .Where(p => p.IsCsProject)
            .Select(p => Path.GetFullPath(Path.Combine(slnFolder, p.Path)))
            .Where(File.Exists)
            // Over-inclusive pre-filter set: a WebResources project with no own <TargetFramework> + a root
            // Directory.Build.props lands in pluginProjectPaths. Keep it if it carries a strong signal — a
            // real plugin never does, so this can't rescue an actual plugin.
            .Where(path => !pluginProjectPaths.Contains(path) || HasStrongWebResourceSignal(path))
            .Where(path => !PcfProjectResolver.IsPcfProject(path))
            .Where(path => !IsTestProject(path))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"'{solutionFileName}' has no WebResources project — Flowline always scaffolds one, even if " +
                "there's nothing to push yet. Add one and wire it in with 'dotnet sln add', or run 'clone' again.");

        var scored = candidates.Select(path => (Path: path, Score: ScoreSignals(path))).ToList();
        var topScore = scored.Max(c => c.Score);
        var top = scored.Where(c => c.Score == topScore).Select(c => c.Path).ToList();

        if (top.Count > 1)
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"'{solutionFileName}' has {top.Count} projects that could be the WebResources project " +
                $"({string.Join(", ", top.Select(Path.GetFileName))}) and Flowline can't tell which. " +
                "Remove the extras, or rename the one that isn't WebResources.");

        // Require a positive signal: a WebResources project always carries one (NoTargets SDK, suppressed
        // compile, dist/, an npm build, or a Flowline annotation). A zero-signal winner is a non-WebResources
        // false positive — returning it would point the deploy drift gate at a dist/ that never exists and
        // silently revert un-synced web resources, so throw instead of guessing.
        if (topScore < MediumWeight)
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"'{solutionFileName}' has no WebResources project — '{Path.GetFileName(top[0])}' survived elimination " +
                "but carries no WebResources signal (no NoTargets SDK, dist/, npm build, or flowline annotation). " +
                "Add the real WebResources project and wire it in with 'dotnet sln add', or run 'clone' again.");

        return top[0];
    }

    // Strong = the signals a genuine WebResources project always carries and a plugin never does. Used to
    // rescue a WebResources project that the over-inclusive plugin pre-filter swept into pluginProjectPaths.
    static bool HasStrongWebResourceSignal(string projectPath)
    {
        var projectDir = Path.GetDirectoryName(projectPath)!;
        return HasSuppressedCompile(ReadTextSafely(projectPath)) ||  // NoTargets SDK or empty CoreCompile
               DirectoryExistsSafely(Path.Combine(projectDir, "dist")) ||
               HasFlowlineAnnotation(projectDir);
    }

    // Suffix/word match, not substring: {SolutionName}.WebResources.csproj must never read as a test project
    // just because the solution is named TestApp, ContosoTest, Latest, or Greatest. The SDK-marker fallback
    // catches a differently-named test project (a project called "Verification.csproj" referencing
    // Microsoft.NET.Test.Sdk).
    static bool IsTestProject(string projectPath)
    {
        var name = Path.GetFileNameWithoutExtension(projectPath);
        return name.EndsWith("Test", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase) ||
               s_testSdkMarkers.Any(marker => ReadTextSafely(projectPath).Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    static int ScoreSignals(string projectPath)
    {
        var projectDir = Path.GetDirectoryName(projectPath)!;
        var projectText = ReadTextSafely(projectPath);

        var score = 0;

        if (HasFlowlineAnnotation(projectDir))
            score += VeryStrongWeight;

        if (HasSuppressedCompile(projectText))
            score += StrongWeight;

        if (DirectoryExistsSafely(Path.Combine(projectDir, "dist")))
            score += StrongWeight;

        if (HasPackageJsonWithBuildScript(projectDir))
            score += MediumWeight;

        if (HasBundlerConfig(projectDir))
            score += MediumWeight;

        if (FolderNameSuggestsWebResources(projectDir))
            score += MediumWeight;

        if (HasWebAssets(projectDir))
            score += MediumWeight;

        return score;
    }

    // Attribute-anchored, not a bare substring: `Sdk="Microsoft.Build.NoTargets` only matches the SDK
    // declaration itself, not an incidental mention elsewhere in the file (a comment, an Exec command).
    static bool HasSuppressedCompile(string projectText) =>
        projectText.Contains("<Target Name=\"CoreCompile\"", StringComparison.OrdinalIgnoreCase) ||
        projectText.Contains("Sdk=\"Microsoft.Build.NoTargets", StringComparison.OrdinalIgnoreCase);

    static bool HasFlowlineAnnotation(string projectDir) =>
        EnumerateSourceFiles(projectDir, s_annotationScanExtensions)
            .Any(file => s_flowlineAnnotation.IsMatch(ReadTextSafely(file)));

    static bool HasWebAssets(string projectDir) =>
        EnumerateSourceFiles(projectDir, s_webAssetExtensions).Any();

    static bool HasPackageJsonWithBuildScript(string projectDir)
    {
        var text = ReadTextSafely(Path.Combine(projectDir, "package.json"));
        if (text.Length == 0)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("scripts", out var scripts) &&
                   scripts.ValueKind == JsonValueKind.Object &&
                   scripts.TryGetProperty("build", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    static bool HasBundlerConfig(string projectDir)
    {
        try
        {
            return s_bundlerConfigPatterns.Any(pattern =>
                Directory.EnumerateFiles(projectDir, pattern, SearchOption.TopDirectoryOnly).Any());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    static bool FolderNameSuggestsWebResources(string projectDir)
    {
        var name = Path.GetFileName(projectDir);
        return name.Contains("WebResources", StringComparison.OrdinalIgnoreCase) &&
               !name.Contains("test", StringComparison.OrdinalIgnoreCase);
    }

    static bool DirectoryExistsSafely(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // An unreadable file (locked by the IDE, deleted mid-scan) reads as "signal not present", not a
    // failure — every caller here is scoring a tiebreak, not deciding whether the candidate exists at all.
    static string ReadTextSafely(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    // Deepest a real WebResources source tree needs; also the cycle guard — a symlink/junction loop can't
    // recurse past this, so a filesystem cycle terminates instead of blowing the stack.
    const int MaxScanDepth = 8;

    // Bounded, hand-rolled recursion rather than Directory.EnumerateFiles(..., AllDirectories): the latter
    // walks into node_modules before any filter sees it, and a Flowline WebResources project's
    // node_modules is exactly the tree this must not pay to traverse.
    static IEnumerable<string> EnumerateSourceFiles(string dir, string[] extensions, int depth = 0)
    {
        foreach (var file in EnumerateFilesSafely(dir))
            if (extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                yield return file;

        if (depth >= MaxScanDepth)
            yield break;

        foreach (var subdir in EnumerateDirectoriesSafely(dir))
        {
            if (s_excludedScanDirs.Contains(Path.GetFileName(subdir), StringComparer.OrdinalIgnoreCase))
                continue;

            foreach (var file in EnumerateSourceFiles(subdir, extensions, depth + 1))
                yield return file;
        }
    }

    static IReadOnlyList<string> EnumerateFilesSafely(string dir)
    {
        try
        {
            return Directory.EnumerateFiles(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    static IReadOnlyList<string> EnumerateDirectoriesSafely(string dir)
    {
        try
        {
            return Directory.EnumerateDirectories(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
