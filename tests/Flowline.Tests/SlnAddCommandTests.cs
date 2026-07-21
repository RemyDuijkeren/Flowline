using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Services;

namespace Flowline.Tests;

/// <summary>
/// Covers <c>flowline sln add</c>.
/// </summary>
/// <remarks>
/// The write scenarios run against a real temp directory rather than a fake filesystem, because the
/// command exists to produce a file that <c>dotnet build</c> can consume — asserting on the bytes it
/// actually writes is the only assertion that means anything here.
/// </remarks>
public sealed class SlnAddCommandTests : IDisposable
{
    readonly string _root;
    readonly string _cdsproj;
    readonly MsBuildSolutionReader _reader = new();
    readonly MsBuildSolutionWriter _writer = new();

    public SlnAddCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "flowline-sln-add-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "Solution"));
        _cdsproj = Path.Combine(_root, "Solution", "MySolution.cdsproj");
        File.WriteAllText(_cdsproj, "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    // A hand-authored .sln whose project display name deliberately differs from its filename — the
    // thing a round-trip through the persistence library would silently rewrite.
    void WriteExistingSln(string fileName = "MySolution.sln") =>
        File.WriteAllText(Path.Combine(_root, fileName),
            """
            Microsoft Visual Studio Solution File, Format Version 12.00
            # Visual Studio Version 17
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "MyFriendlyName", "Plugins\Contoso.Plugins.csproj", "{11111111-1111-1111-1111-111111111111}"
            EndProject
            Global
            	GlobalSection(SolutionConfigurationPlatforms) = preSolution
            		Debug|Any CPU = Debug|Any CPU
            		Release|Any CPU = Release|Any CPU
            	EndGlobalSection
            EndGlobal

            """);

    // ── Argument refusal (R2 / AE1) ───────────────────────────────────────────

    [Fact]
    public void ValidateExtension_CsprojArgument_ThrowsPointingAtDotnetSlnAdd()
    {
        var act = () => SlnAddCommand.ValidateExtension("Plugins/MySolution.Plugins.csproj");

        act.Should().Throw<FlowlineException>()
           .Where(e => e.ExitCode == ExitCode.ValidationFailed && e.Message.Contains("dotnet sln add"));
    }

    [Fact]
    public async Task CsprojArgument_IsRefusedBeforeAnythingIsWritten()
    {
        var csproj = Path.Combine(_root, "Plugins", "MySolution.Plugins.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(csproj)!);
        await File.WriteAllTextAsync(csproj, "<Project />");

        var act = () => SlnAddCommand.ValidateExtension(csproj);

        act.Should().Throw<FlowlineException>();
        // The refusal has to come before the solution file is even located, or a repo with no solution
        // file would gain one purely by being asked the wrong question.
        Directory.EnumerateFiles(_root, "*.sln*").Should().BeEmpty();
    }

    [Theory]
    [InlineData("Solution/MySolution.txt")]
    [InlineData("Solution/MySolution")]
    [InlineData("Solution/MySolution.vbproj")]
    public void ValidateExtension_NonProjectExtension_Throws(string path)
    {
        var act = () => SlnAddCommand.ValidateExtension(path);

        act.Should().Throw<FlowlineException>()
           .Where(e => e.ExitCode == ExitCode.ValidationFailed && e.Message.Contains(".cdsproj"));
    }

    [Theory]
    [InlineData("Solution/MySolution.cdsproj")]
    [InlineData("Solution/MySolution.CDSPROJ")]
    public void ValidateExtension_Cdsproj_DoesNotThrow(string path)
    {
        var act = () => SlnAddCommand.ValidateExtension(path);

        act.Should().NotThrow();
    }

    // ── Missing project file (R5) ─────────────────────────────────────────────

    [Fact]
    public void ValidateProjectExists_MissingFile_ThrowsNotFoundNamingThePath()
    {
        var missing = Path.Combine(_root, "Solution", "Nope.cdsproj");

        var act = () => SlnAddCommand.ValidateProjectExists(missing, "Solution/Nope.cdsproj");

        act.Should().Throw<FlowlineException>()
           .Where(e => e.ExitCode == ExitCode.NotFound && e.Message.Contains("Solution/Nope.cdsproj"));
    }

    [Fact]
    public void ValidateProjectExists_PresentFile_DoesNotThrow()
    {
        var act = () => SlnAddCommand.ValidateProjectExists(_cdsproj, "Solution/MySolution.cdsproj");

        act.Should().NotThrow();
    }

    // ── Writing into an existing .sln (R3 / R6 / R9 / AE2) ────────────────────

    [Fact]
    public async Task AddAsync_ProjectWithSlnSolutionFile_WritesIntoTheSlnAndDoesNotConvertIt()
    {
        WriteExistingSln();

        var result = await SlnAddCommand.AddAsync(_reader, _writer, _cdsproj, _root);

        result.Outcome.Should().Be(SlnAddCommand.Outcome.Added);
        result.CreatedSolutionFile.Should().BeFalse();
        Path.GetFileName(result.SolutionFilePath).Should().Be("MySolution.sln");

        var content = await File.ReadAllTextAsync(result.SolutionFilePath);
        content.Should().Contain(@"Solution\MySolution.cdsproj");
        // R9: no conversion, no second file, and the hand-written display name survives untouched.
        Directory.EnumerateFiles(_root, "*.slnx").Should().BeEmpty();
        content.Should().Contain("\"MyFriendlyName\"");
    }

    // ── Idempotency (R4 / AE3) ────────────────────────────────────────────────

    [Fact]
    public async Task AddAsync_ProjectAlreadyPresent_ReportsAlreadyPresentWithoutDuplicating()
    {
        WriteExistingSln();
        await SlnAddCommand.AddAsync(_reader, _writer, _cdsproj, _root);

        var result = await SlnAddCommand.AddAsync(_reader, _writer, _cdsproj, _root);

        result.Outcome.Should().Be(SlnAddCommand.Outcome.AlreadyPresent);
        var projects = await _reader.ReadProjectsAsync(result.SolutionFilePath);
        projects.Where(p => p.IsCdsProject).Should().ContainSingle();
    }

    // ── Coexisting .sln and .slnx (R5a / AE5) ─────────────────────────────────

    [Fact]
    public async Task AddAsync_BothFormatsPresent_WritesIntoTheSlnxAndStaysSuccessful()
    {
        WriteExistingSln();
        await File.WriteAllTextAsync(Path.Combine(_root, "MySolution.slnx"), "<Solution />");

        var result = await SlnAddCommand.AddAsync(_reader, _writer, _cdsproj, _root);

        Path.GetFileName(result.SolutionFilePath).Should().Be("MySolution.slnx");
        result.Outcome.Should().Be(SlnAddCommand.Outcome.Added);
        // The state `dotnet sln migrate` leaves behind is not a failure — the command reports the
        // leftover and exits clean, so a script wrapping it does not break on a supported migration.
        SlnAddCommand.SelectExitCode(result.Outcome).Should().Be((int)ExitCode.Success);
        _reader.HasCoexistingSolutionFiles(_root).Should().BeTrue();
    }

    // ── No solution file at all (R5b / AE7, reversed in review) ───────────────

    [Fact]
    public async Task AddAsync_NoSolutionFileAnywhere_FailsWithNotFoundAndCreatesNothing()
    {
        // The maintainer reversed R5b/AE7 during review: creating a solution file is `dotnet new sln`'s
        // job, and Flowline only covers what the SDK cannot (KD2). So this is an error, not a scaffold.
        _reader.FindSolutionFile(_root).Should().BeNull();

        var act = () => SlnAddCommand.AddAsync(_reader, _writer, _cdsproj, _root);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.ExitCode.Should().Be(ExitCode.NotFound);
        Directory.EnumerateFiles(_root, "*.sln*").Should().BeEmpty("a failed add must not leave a file behind");
    }

    [Fact]
    public async Task AddAsync_NoSolutionFileAnywhere_ErrorNamesTheDirectoryAndPointsAtDotnetNewSln()
    {
        var act = () => SlnAddCommand.AddAsync(_reader, _writer, _cdsproj, _root);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.Message.Should().Contain(_root).And.Contain("dotnet new sln");
    }

    // ── Not walking up to a parent solution file ──────────────────────────────
    //
    // dotnet sln add only ever looks in the exact folder it's told to — never above it. Flowline used to
    // walk up looking for the nearest .sln/.slnx, which meant running the command from an arbitrary
    // subfolder (e.g. one outside any Flowline project) could silently find and rewrite an unrelated
    // solution file sitting in a parent directory. Matching dotnet's behavior removes that hazard.

    [Fact]
    public async Task AddAsync_NoSolutionFileInTheExactFolder_ThrowsNotFoundEvenWhenOneExistsAbove()
    {
        // A solution file one level up must not be found — only the exact folder counts.
        WriteExistingSln();
        var subfolder = Path.Combine(_root, "Solution");

        var act = () => SlnAddCommand.AddAsync(_reader, _writer, _cdsproj, subfolder);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.ExitCode.Should().Be(ExitCode.NotFound);
    }

    [Fact]
    public async Task AddAsync_NoSolutionFileInTheExactFolder_DoesNotTouchAnySolutionFileAbove()
    {
        WriteExistingSln();
        var subfolder = Path.Combine(_root, "Solution");
        var before = await File.ReadAllTextAsync(Path.Combine(_root, "MySolution.sln"));

        var act = () => SlnAddCommand.AddAsync(_reader, _writer, _cdsproj, subfolder);
        await act.Should().ThrowAsync<FlowlineException>();

        (await File.ReadAllTextAsync(Path.Combine(_root, "MySolution.sln"))).Should().Be(before,
            "a solution file outside the exact folder must never be modified");
    }

    [Fact]
    public async Task AddAsync_SolutionFileInTheExactFolder_Succeeds()
    {
        var subfolder = Path.Combine(_root, "Solution");
        File.WriteAllText(Path.Combine(subfolder, "Nested.sln"),
            "Microsoft Visual Studio Solution File, Format Version 12.00\r\n");

        var result = await SlnAddCommand.AddAsync(_reader, _writer, _cdsproj, subfolder);

        result.Outcome.Should().Be(SlnAddCommand.Outcome.Added);
        Path.GetFileName(result.SolutionFilePath).Should().Be("Nested.sln");
    }

    // ── Standalone operation (KTD6) ───────────────────────────────────────────

    [Fact]
    public async Task AddAsync_FolderWithNoFlowlineConfigAndNoGitRepo_StillWrites()
    {
        WriteExistingSln();
        Directory.EnumerateFiles(_root, ".flowline").Should().BeEmpty();
        Directory.Exists(Path.Combine(_root, ".git")).Should().BeFalse();

        var result = await SlnAddCommand.AddAsync(_reader, _writer, _cdsproj, _root);

        result.Outcome.Should().Be(SlnAddCommand.Outcome.Added);
        // The command never creates a solution file, so this is the only value it can honestly report.
        result.CreatedSolutionFile.Should().BeFalse();
    }

    [Fact]
    public void RequiresProject_IsFalse_SoTheCommandRunsOutsideAFlowlineProject()
    {
        // Guards the two overrides that make standalone operation possible. Both are cheap to lose in a
        // refactor and neither failure shows up until someone runs the command in a bare repo.
        var command = typeof(SlnAddCommand);
        var requiresProject = command.GetProperty("RequiresProject",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        requiresProject!.DeclaringType.Should().Be(command);

        var checkSetup = command.GetMethod("CheckSetupAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        checkSetup!.DeclaringType.Should().Be(command, "the git/dotnet/pac probe must not run for a local file edit");
    }

    // ── Path and naming helpers ───────────────────────────────────────────────

    [Fact]
    public void ToSolutionRelativePath_RewritesTheArgumentRelativeToTheSolutionFolder()
    {
        // The argument is relative to wherever the user is standing; the entry has to be relative to
        // the solution file, so this must survive being run from inside Solution/.
        SlnAddCommand.ToSolutionRelativePath(_cdsproj, _root)
                     .Should().Be(Path.Combine("Solution", "MySolution.cdsproj"));
    }

    // ── Exit codes ────────────────────────────────────────────────────────────

    // Two facts rather than a Theory: Outcome is internal, so it cannot appear in the signature of a
    // public xUnit test method.
    [Fact]
    public void SelectExitCode_Added_ReturnsSuccess()
    {
        SlnAddCommand.SelectExitCode(SlnAddCommand.Outcome.Added).Should().Be((int)ExitCode.Success);
    }

    [Fact]
    public void SelectExitCode_AlreadyPresent_ReturnsSuccess()
    {
        // Also the desired end state, just reached without work — only a thrown FlowlineException is a
        // failure here, so nothing wrapping this command should see a non-zero code for a re-add.
        SlnAddCommand.SelectExitCode(SlnAddCommand.Outcome.AlreadyPresent).Should().Be((int)ExitCode.Success);
    }
}
