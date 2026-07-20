using System.Text.RegularExpressions;
using FluentAssertions;

namespace Flowline.Tests;

/// <summary>
/// Guards the U5 identity-key change: plugin projects are found through solution-file membership, so no
/// consumer may go back to composing a plugin path from a fixed project or assembly name.
/// </summary>
/// <remarks>
/// This exists because the repo has been burned twice by trusting a plan's stated file list
/// (<c>docs/solutions/design-patterns/extending-identity-key-plan-files-list-incomplete.md</c> and
/// <c>promoting-field-to-identity-key-changes-edit-semantics.md</c>). A missed consumer of an identity key
/// is never a compile error — the old code keeps type-checking and keeps running against the narrower key,
/// silently. Grepping the tree is the only thing that catches it, so the grep is a test rather than a step
/// someone is trusted to remember.
///
/// The allowlist is the real content here. Every entry is a site that legitimately still names the
/// conventional project, with the reason it does. Adding an entry should be a deliberate, reviewed act —
/// if this test fails, the default answer is to route the new code through
/// <c>PluginProjectResolver</c>, not to widen the list.
/// </remarks>
public class PluginPathConventionTests
{
    /// <summary>Files permitted to name the conventional plugin project, and why.</summary>
    static readonly Dictionary<string, string> s_allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["src/Flowline.Core/Plugins/PluginProjectResolver.cs"] =
            "the discovery layer itself — owns the no-solution-file fallback candidate",
        ["src/Flowline/Commands/CloneCommand.cs"] =
            "scaffolding: creates the Plugins project, so it picks the name rather than discovering it",
        ["src/Flowline/Commands/GenerateCommand.cs"] =
            "Plugins/Models is the default early-bound output path, not plugin-project discovery",
        ["src/Flowline/Commands/FlowlineCommand.cs"] =
            "declares the PluginsName constant the two entries above consume",
        ["src/Flowline/Program.cs"] =
            "an example invocation string in --help text",
    };

    // Matches a plugin project/assembly name used as a literal, and the shared constant that stands in for
    // one. Deliberately broad: a false positive costs one allowlist entry with a reason, while a false
    // negative is the silent bug this whole test exists to prevent.
    static readonly Regex s_hardcodedPluginName =
        new(@"""Plugins(\.csproj|\.dll)?""|\bPluginsName\b", RegexOptions.Compiled);

    [Fact]
    public void SourceTree_OutsideDiscoveryLayer_ComposesNoPluginPathFromAHardcodedProjectName()
    {
        var srcRoot = Path.Combine(FindRepoRoot(), "src");

        var offenders = Directory.EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
                                 .Where(f => !IsBuildOutput(f))
                                 .SelectMany(FindOffendingLines)
                                 .ToList();

        offenders.Should().BeEmpty(
            "plugin projects come from solution-file membership (U5) — route these through " +
            "PluginProjectResolver, or add the file to the allowlist with a reason:\n" +
            string.Join("\n", offenders));
    }

    [Fact]
    public void Allowlist_NamesOnlyFilesThatExist()
    {
        // A stale entry silently stops guarding whatever replaced that file.
        var repoRoot = FindRepoRoot();

        s_allowed.Keys
                 .Where(p => !File.Exists(Path.Combine(repoRoot, p.Replace('/', Path.DirectorySeparatorChar))))
                 .Should().BeEmpty("every allowlisted path must still be a real file");
    }

    /// <summary>
    /// Standalone mode must never reach solution-file discovery — that's what makes `--pluginFile` the way
    /// out when discovery can't classify a project and refuses the push.
    /// </summary>
    [Fact]
    public void PushCommand_StandalonePreparation_TouchesNoDiscoveryApi()
    {
        var source = File.ReadAllLines(Path.Combine(FindRepoRoot(), "src", "Flowline", "Commands", "PushCommand.cs"));

        var start = Array.FindIndex(source, l => l.Contains("PrepareStandalonePluginForPush(", StringComparison.Ordinal) &&
                                                 l.Contains("private", StringComparison.Ordinal));
        start.Should().BeGreaterThan(-1, "the standalone preparation method must still exist");

        // Body runs to the next member declaration at the same nesting level.
        var end = Array.FindIndex(source, start + 1, l => l.StartsWith("    private ", StringComparison.Ordinal) ||
                                                          l.StartsWith("    internal ", StringComparison.Ordinal));
        if (end < 0) end = source.Length;

        source[start..end]
            .Where(l => l.Contains("PluginProjectResolver", StringComparison.Ordinal) ||
                        l.Contains("MsBuildSolutionReader", StringComparison.Ordinal))
            .Should().BeEmpty("--pluginFile names the artifact outright, so it must bypass discovery entirely");
    }

    static IEnumerable<string> FindOffendingLines(string filePath)
    {
        var relative = Path.GetRelativePath(FindRepoRoot(), filePath).Replace(Path.DirectorySeparatorChar, '/');
        if (s_allowed.ContainsKey(relative)) yield break;

        var lines = File.ReadAllLines(filePath);
        for (var i = 0; i < lines.Length; i++)
        {
            // Comments explain the convention constantly (including in the file under test); only code counts.
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("//") || trimmed.StartsWith("*") || trimmed.StartsWith("///")) continue;

            if (s_hardcodedPluginName.IsMatch(lines[i]))
                yield return $"  {relative}:{i + 1}: {trimmed}";
        }
    }

    static bool IsBuildOutput(string path) =>
        path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") ||
        path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}");

    static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Flowline.slnx")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Couldn't find Flowline.slnx above the test assembly.");
    }
}
