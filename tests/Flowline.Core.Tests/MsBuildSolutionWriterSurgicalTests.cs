using Flowline.Core.Services;
using FluentAssertions;

namespace Flowline.Core.Tests;

/// <summary>
/// Covers the surgical text insert used for <b>pre-existing</b> <c>.sln</c> files.
/// </summary>
/// <remarks>
/// These are separate from <see cref="MsBuildSolutionWriterTests"/> because they assert a different
/// contract: not "the entry is there" but "nothing else moved". The library's <c>.sln</c> writer
/// re-derives every project's display name from its filename
/// (https://github.com/microsoft/vs-solutionpersistence/issues/122), so a user's solution must never be
/// round-tripped through it. Byte-level assertions are the only way to prove that stayed true.
/// </remarks>
public class MsBuildSolutionWriterSurgicalTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    readonly MsBuildSolutionWriter _writer = new();
    readonly MsBuildSolutionReader _reader = new();

    public MsBuildSolutionWriterSurgicalTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>The legacy C# project type GUID — what .sln writers, including the library, emit.</summary>
    const string LegacyCSharpTypeGuid = "{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}";

    const string ExistingProjectGuid = "{11111111-1111-1111-1111-111111111111}";
    const string SolutionGuid = "{22222222-2222-2222-2222-222222222222}";

    string Path_(string relative) => Path.Combine(_root, relative);

    /// <summary>Writes a fixture with explicit line endings, so the CRLF/LF tests are not at the mercy of git.</summary>
    string Write(string fileName, string newline, params string[] lines)
    {
        var path = Path_(fileName);
        File.WriteAllText(path, string.Join(newline, lines) + newline);
        return path;
    }

    /// <summary>
    /// A hand-authored solution whose only project carries a display name that does NOT match its
    /// filename — the shape that the library writer corrupts.
    /// </summary>
    static string[] HandAuthoredSolution() =>
    [
        "Microsoft Visual Studio Solution File, Format Version 12.00",
        "# Visual Studio Version 17",
        "VisualStudioVersion = 17.9.34728.123",
        "MinimumVisualStudioVersion = 10.0.40219.1",
        $"Project(\"{LegacyCSharpTypeGuid}\") = \"MyFriendlyName\", \"Plugins\\DWE_Base.Plugins.csproj\", \"{ExistingProjectGuid}\"",
        "EndProject",
        "Global",
        "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution",
        "\t\tDebug|Any CPU = Debug|Any CPU",
        "\t\tRelease|Any CPU = Release|Any CPU",
        "\tEndGlobalSection",
        "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution",
        $"\t\t{ExistingProjectGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
        $"\t\t{ExistingProjectGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU",
        $"\t\t{ExistingProjectGuid}.Release|Any CPU.ActiveCfg = Release|Any CPU",
        $"\t\t{ExistingProjectGuid}.Release|Any CPU.Build.0 = Release|Any CPU",
        "\tEndGlobalSection",
        "\tGlobalSection(SolutionProperties) = preSolution",
        "\t\tHideSolutionNode = FALSE",
        "\tEndGlobalSection",
        "\tGlobalSection(ExtensibilityGlobals) = postSolution",
        $"\t\tSolutionGuid = {SolutionGuid}",
        "\tEndGlobalSection",
        "EndGlobal",
    ];

    static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>Lines present before the insert but gone after it — the characterization contract.</summary>
    static string[] RemovedLines(string before, string after)
    {
        var afterLines = Lines(after).ToList();
        var removed = new List<string>();
        foreach (var line in Lines(before))
        {
            var index = afterLines.IndexOf(line);
            if (index < 0) removed.Add(line);
            else afterLines.RemoveAt(index);
        }
        return [.. removed];
    }

    [Fact]
    public async Task AddProjectAsync_ExistingSln_RemovesNoLineFromTheOriginalFile()
    {
        // The characterization test. Everything the user hand-wrote — header, comments, GUIDs, section
        // order, config rows — must survive verbatim; the only legal diff is added lines.
        var solution = Write("DWE_Base.sln", "\r\n", HandAuthoredSolution());
        var before = await File.ReadAllTextAsync(solution);

        await _writer.AddProjectAsync(solution, "Solution/DWE_Base.cdsproj");

        var after = await File.ReadAllTextAsync(solution);
        RemovedLines(before, after).Should().BeEmpty();
    }

    [Fact]
    public async Task AddProjectAsync_ExistingSln_KeepsDisplayNameThatDiffersFromFileName()
    {
        // The regression this whole path exists to prevent: the library rewrites "MyFriendlyName" to
        // "DWE_Base.Plugins", silently breaking `msbuild -t:MyFriendlyName` for the user.
        var solution = Write("DWE_Base.sln", "\r\n", HandAuthoredSolution());

        await _writer.AddProjectAsync(solution, "Solution/DWE_Base.cdsproj");

        var text = await File.ReadAllTextAsync(solution);
        text.Should().Contain("= \"MyFriendlyName\", \"Plugins\\DWE_Base.Plugins.csproj\"");
        text.Should().NotContain("\"DWE_Base.Plugins\",");
    }

    [Fact]
    public async Task AddProjectAsync_ExistingSln_PreservesHeaderExtensibilityGlobalsAndProjectGuids()
    {
        var solution = Write("DWE_Base.sln", "\r\n", HandAuthoredSolution());

        await _writer.AddProjectAsync(solution, "Solution/DWE_Base.cdsproj");

        var text = await File.ReadAllTextAsync(solution);
        text.Should().Contain("Microsoft Visual Studio Solution File, Format Version 12.00");
        text.Should().Contain("# Visual Studio Version 17");
        text.Should().Contain("VisualStudioVersion = 17.9.34728.123");
        text.Should().Contain("MinimumVisualStudioVersion = 10.0.40219.1");
        text.Should().Contain($"SolutionGuid = {SolutionGuid}");
        text.Should().Contain("HideSolutionNode = FALSE");
        // The existing project keeps its identity — a regenerated GUID would orphan every build
        // configuration and project reference pointing at it.
        text.Should().Contain(ExistingProjectGuid);
    }

    [Fact]
    public async Task AddProjectAsync_ExistingSln_InsertsProjectBlockImmediatelyBeforeGlobal()
    {
        var solution = Write("DWE_Base.sln", "\r\n", HandAuthoredSolution());

        await _writer.AddProjectAsync(solution, "Solution/DWE_Base.cdsproj");

        var lines = Lines(await File.ReadAllTextAsync(solution));
        var globalIndex = Array.FindIndex(lines, l => l == "Global");
        lines[globalIndex - 1].Should().Be("EndProject");
        lines[globalIndex - 2].Should().Contain("DWE_Base.cdsproj");
        // The .sln format stores paths with backslashes regardless of host OS.
        lines[globalIndex - 2].Should().Contain("\"Solution\\DWE_Base.cdsproj\"");
        lines[globalIndex - 2].Should().StartWith($"Project(\"{LegacyCSharpTypeGuid}\") = \"DWE_Base\"");
    }

    [Fact]
    public async Task AddProjectAsync_ExistingSlnWithNonDefaultPlatform_MapsEachDeclaredPairToTheProjectsAnyCpuConfiguration()
    {
        // Debug|x64 is the proof that the left-hand pairs come from the file rather than a hardcoded
        // Debug/Release x Any CPU assumption — and that the right-hand side does NOT echo them back.
        var solution = Write("DWE_Base.sln", "\r\n",
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            "Global",
            "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution",
            "\t\tDebug|x64 = Debug|x64",
            "\t\tRelease|x64 = Release|x64",
            "\t\tCustomCfg|ARM64 = CustomCfg|ARM64",
            "\tEndGlobalSection",
            "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution",
            "\tEndGlobalSection",
            "EndGlobal");

        await _writer.AddProjectAsync(solution, "Solution/DWE_Base.cdsproj");

        var text = await File.ReadAllTextAsync(solution);

        // Left side: the declared pair, verbatim. Right side: the configuration the *project* builds,
        // which for a .cdsproj is always Any CPU. Verified against `dotnet sln add` on SDK 10, which
        // writes `{guid}.Debug|x64.ActiveCfg = Debug|Any CPU` for exactly this solution.
        (string Declared, string Project)[] expected =
        [
            ("Debug|x64",       "Debug|Any CPU"),
            ("Release|x64",     "Release|Any CPU"),
            // dotnet sln add falls back to Debug|Any CPU here, because the project has no CustomCfg. We
            // keep the declared name instead — the project has no matching configuration either way, and
            // silently substituting Debug is the more surprising of the two.
            ("CustomCfg|ARM64", "CustomCfg|Any CPU"),
        ];

        foreach (var (declared, project) in expected)
        {
            text.Should().Contain($".{declared}.ActiveCfg = {project}");
            text.Should().Contain($".{declared}.Build.0 = {project}");
        }

        // No invented pairs on the left. dotnet sln add cross-produces every build type against every
        // platform — 3 declared pairs became 12 rows, inventing Any CPU and x86 solution configurations
        // the file never had. Rewriting the user's declared set is what this path exists to avoid.
        text.Should().NotContain("x86");
        text.Should().NotContain("Any CPU = ");
        text.Should().NotContain("Any CPU.ActiveCfg");
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public async Task AddProjectAsync_ExistingSln_KeepsTheFilesOwnLineEndings(string newline)
    {
        // Rewriting line endings turns a one-line change into a whole-file diff in git.
        var solution = Write("DWE_Base.sln", newline, HandAuthoredSolution());

        await _writer.AddProjectAsync(solution, "Solution/DWE_Base.cdsproj");

        var text = await File.ReadAllTextAsync(solution);
        var lineFeeds = text.Count(c => c == '\n');
        var crlfPairs = text.Split("\r\n").Length - 1;
        // CRLF: every LF is part of a CRLF pair. LF: the file holds no CR at all.
        if (newline == "\r\n") crlfPairs.Should().Be(lineFeeds);
        else text.Should().NotContain("\r");
    }

    [Fact]
    public async Task AddProjectAsync_ExistingSlnWithNoProjects_InsertsTheFirstEntry()
    {
        // No `Project(` block to anchor against — `Global` is the only anchor that always exists.
        var solution = Write("Empty.sln", "\r\n",
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            "Global",
            "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution",
            "\t\tDebug|Any CPU = Debug|Any CPU",
            "\tEndGlobalSection",
            "EndGlobal");

        var added = (await _writer.AddProjectAsync(solution, "Solution/DWE_Base.cdsproj")).Added;

        added.Should().BeTrue();
        (await _reader.ReadProjectsAsync(solution)).Should().ContainSingle();
        var text = await File.ReadAllTextAsync(solution);
        // There was no ProjectConfigurationPlatforms section, so one had to be created for the rows.
        text.Should().Contain("GlobalSection(ProjectConfigurationPlatforms) = postSolution");
        text.Should().Contain(".Debug|Any CPU.Build.0 = Debug|Any CPU");
    }

    [Fact]
    public async Task AddProjectAsync_ExistingSlnAlreadyReferencingTheProject_LeavesTheFileByteIdentical()
    {
        var solution = Write("DWE_Base.sln", "\r\n", HandAuthoredSolution());
        var before = await File.ReadAllBytesAsync(solution);

        // Separator differs from what the file stores, which is exactly the case a raw string compare
        // would miss and then duplicate.
        var added = (await _writer.AddProjectAsync(solution, "Plugins/DWE_Base.Plugins.csproj")).Added;

        added.Should().BeFalse();
        (await File.ReadAllBytesAsync(solution)).Should().Equal(before);
    }

    [Fact]
    public async Task AddProjectAsync_ExistingSlnWithUnusualSectionOrder_PreservesThatOrder()
    {
        // ProjectConfigurationPlatforms before SolutionConfigurationPlatforms is unusual but valid, and
        // a round-trip through the library would normalise it away.
        var solution = Write("Odd.sln", "\r\n",
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            $"Project(\"{LegacyCSharpTypeGuid}\") = \"MyFriendlyName\", \"Plugins\\DWE_Base.Plugins.csproj\", \"{ExistingProjectGuid}\"",
            "EndProject",
            "Global",
            "\tGlobalSection(ExtensibilityGlobals) = postSolution",
            $"\t\tSolutionGuid = {SolutionGuid}",
            "\tEndGlobalSection",
            "\tGlobalSection(ProjectConfigurationPlatforms) = postSolution",
            $"\t\t{ExistingProjectGuid}.Debug|Any CPU.ActiveCfg = Debug|Any CPU",
            $"\t\t{ExistingProjectGuid}.Debug|Any CPU.Build.0 = Debug|Any CPU",
            "\tEndGlobalSection",
            "\tGlobalSection(SolutionConfigurationPlatforms) = preSolution",
            "\t\tDebug|Any CPU = Debug|Any CPU",
            "\tEndGlobalSection",
            "EndGlobal");

        await _writer.AddProjectAsync(solution, "Solution/DWE_Base.cdsproj");

        var lines = Lines(await File.ReadAllTextAsync(solution)).ToList();
        lines.FindIndex(l => l.Contains("ExtensibilityGlobals"))
             .Should().BeLessThan(lines.FindIndex(l => l.Contains("(ProjectConfigurationPlatforms)")));
        lines.FindIndex(l => l.Contains("(ProjectConfigurationPlatforms)"))
             .Should().BeLessThan(lines.FindIndex(l => l.Contains("(SolutionConfigurationPlatforms)")));
    }

    [Fact]
    public async Task AddProjectAsync_ExistingSln_ProducesAnEntryTheReaderCanParse()
    {
        // Textually plausible is not enough — the round-trip proves the inserted block is real.
        var solution = Write("DWE_Base.sln", "\r\n", HandAuthoredSolution());

        await _writer.AddProjectAsync(solution, "Solution/DWE_Base.cdsproj");

        var projects = await _reader.ReadProjectsAsync(solution);
        projects.Should().HaveCount(2);
        projects.Should().ContainSingle(p => p.IsCdsProject);
        // Matched on path, not Name: the library derives ActualDisplayName from the filename even when
        // reading (issue #122 again), so the parsed name is not evidence of what the file holds. The
        // file's own text is asserted in KeepsDisplayNameThatDiffersFromFileName.
        projects.Select(p => p.Path).Should().BeEquivalentTo(
            Path.Combine("Plugins", "DWE_Base.Plugins.csproj"),
            Path.Combine("Solution", "DWE_Base.cdsproj"));
    }

    [Fact]
    public async Task AddProjectAsync_ExistingSlnWithNoGlobalSection_AppendsTheEntryAtTheEnd()
    {
        // No Global means no solution configurations to map to, so the entry goes at the end and gets no
        // config rows. Degraded but honest: the file stays parseable and gains the project.
        var solution = Write("Bare.sln", "\r\n",
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            $"Project(\"{LegacyCSharpTypeGuid}\") = \"MyFriendlyName\", \"Plugins\\DWE_Base.Plugins.csproj\", \"{ExistingProjectGuid}\"",
            "EndProject");

        var added = (await _writer.AddProjectAsync(solution, "Solution/DWE_Base.cdsproj")).Added;

        added.Should().BeTrue();
        var text = await File.ReadAllTextAsync(solution);
        text.Should().Contain("Solution\\DWE_Base.cdsproj");
        text.Should().NotContain("ActiveCfg");
        (await _reader.ReadProjectsAsync(solution)).Should().HaveCount(2);
    }

    [Fact]
    public async Task AddProjectAsync_NonUtf8Sln_RefusesInsteadOfReplacingCharacters()
    {
        // A permissive decoder turns unknown bytes into U+FFFD, and this path writes the decoded text
        // straight back — so accepting the file would silently destroy the user's characters.
        var path = Path_("Latin1.sln");
        var latin1 = System.Text.Encoding.Latin1;
        var lines = string.Join("\r\n",
            "Microsoft Visual Studio Solution File, Format Version 12.00",
            "Project(\"" + LegacyCSharpTypeGuid + "\") = \"Café\", \"Plugins\\Café.csproj\", \"" + ExistingProjectGuid + "\"",
            "EndProject",
            "Global",
            "EndGlobal") + "\r\n";
        await File.WriteAllBytesAsync(path, latin1.GetBytes(lines));
        var before = await File.ReadAllBytesAsync(path);

        var act = () => new MsBuildSolutionWriter().AddProjectAsync(path, "Solution/New.cdsproj");

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
        (await File.ReadAllBytesAsync(path)).Should().Equal(before, "a refused file must be left alone");
    }

    [Fact]
    public async Task AddProjectAsync_ExistingSln_LeavesNoTempFileBehind()
    {
        var path = Write("Clean.sln", "\r\n", HandAuthoredSolution());

        await new MsBuildSolutionWriter().AddProjectAsync(path, "Solution/New.cdsproj");

        Directory.EnumerateFiles(_root, "*.flowline-tmp").Should().BeEmpty();
    }

}
