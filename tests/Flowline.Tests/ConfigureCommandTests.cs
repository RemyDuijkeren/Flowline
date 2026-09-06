using FluentAssertions;
using Flowline.Commands;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Configure;

namespace Flowline.Tests;

public class ConfigureCommandTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public ConfigureCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    void WithProject() => File.WriteAllText(Path.Combine(_dir, ProjectConfig.s_configFileName), "{}");

    // ── The --pull artifact seam ─────────────────────────────────────────────

    // This is what decides whether a pull hands `pac` --solution-zip or --solution-folder, and getting it
    // wrong fails inside the subprocess, where the cause is hard to read. It is a pure function, so the
    // branch that needs a real `pac` is the only part these cannot reach.

    [Fact]
    public void ResolveSolutionInput_Folder_IsNotAZip()
    {
        var folder = Path.Combine(_dir, "Solution", "src");
        Directory.CreateDirectory(folder);

        var (path, isZip) = ConfigureCommand.ResolveSolutionInput(folder);

        isZip.Should().BeFalse();
        path.Should().Be(Path.GetFullPath(folder));
    }

    [Fact]
    public void ResolveSolutionInput_File_IsAZip()
    {
        var zip = Path.Combine(_dir, "ContosoCustomizations.zip");
        File.WriteAllText(zip, "not really a zip, but it exists");

        var (path, isZip) = ConfigureCommand.ResolveSolutionInput(zip);

        isZip.Should().BeTrue();
        path.Should().Be(Path.GetFullPath(zip));
    }

    // pac is given the resolved path, so a relative argument has to survive the temp-directory switch a pull
    // runs inside — resolving it late would look for the artifact in the wrong place.
    [Fact]
    public void ResolveSolutionInput_RelativePath_IsMadeAbsolute()
    {
        var folder = Path.Combine(_dir, "Solution");
        Directory.CreateDirectory(folder);
        var previous = Directory.GetCurrentDirectory();

        try
        {
            Directory.SetCurrentDirectory(_dir);

            var (path, _) = ConfigureCommand.ResolveSolutionInput("Solution");

            Path.IsPathRooted(path).Should().BeTrue();
            path.Should().Be(Path.GetFullPath(folder));
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }

    [Fact]
    public void ResolveSolutionInput_NothingAtThePath_ThrowsNotFoundNamingIt()
    {
        var missing = Path.Combine(_dir, "no-such-solution.zip");

        var act = () => ConfigureCommand.ResolveSolutionInput(missing);

        act.Should().Throw<FlowlineException>()
            .Which.ExitCode.Should().Be(ExitCode.NotFound);
    }

    // ── Stand-alone resolution (KTD3) ────────────────────────────────────────

    [Fact]
    public void ResolveStandalone_SolutionNameAndNoProject_IsStandalone()
    {
        ConfigureCommand.ResolveStandalone("ContosoCustomizations", null, _dir).Should().BeTrue();
    }

    [Fact]
    public void ResolveStandalone_SolutionNameButProjectFound_IsNotStandalone()
    {
        // The project wins: inside a checkout the solution comes from the project file, and a flag that
        // disagreed with it would apply a settings file against a solution the checkout does not describe.
        WithProject();

        ConfigureCommand.ResolveStandalone("ContosoCustomizations", null, _dir).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveStandalone_NeitherFlag_IsNotStandalone(string? solutionName)
    {
        ConfigureCommand.ResolveStandalone(solutionName, null, _dir).Should().BeFalse();
    }

    // A stand-alone pull names the solution through the artifact rather than through --solution-name. Judged
    // project mode, it would stop at "No Flowline project found" with the artifact it was handed ignored.
    [Fact]
    public void ResolveStandalone_PullArtifactAndNoProject_IsStandalone()
    {
        ConfigureCommand.ResolveStandalone(null, "ContosoCustomizations.zip", _dir).Should().BeTrue();
    }

    [Fact]
    public void ResolveStandalone_PullArtifactButProjectFound_IsNotStandalone()
    {
        WithProject();

        ConfigureCommand.ResolveStandalone(null, "ContosoCustomizations.zip", _dir).Should().BeFalse();
    }

    // A bare --pull inside no project still is not stand-alone: the flag carries no value, so nothing names
    // the solution.
    [Fact]
    public void ResolveStandalone_BarePullWithNoValue_IsNotStandalone()
    {
        ConfigureCommand.ResolveStandalone(null, null, _dir).Should().BeFalse();
    }

    // ── Flag validation ──────────────────────────────────────────────────────

    [Fact]
    public void ValidateFlags_PullWithSettingsFile_IsRejectedNamingBothFlags()
    {
        var error = ConfigureCommand.ValidateFlags(
            pullRequested: true, settingsFile: "settings.json", standalone: false, solutionName: null, projectFound: true);

        error.Should().NotBeNull();
        error.Should().Contain("--pull").And.Contain("--settings-file");
    }

    [Fact]
    public void ValidateFlags_SolutionNameInsideAProject_IsRejected()
    {
        var error = ConfigureCommand.ValidateFlags(
            pullRequested: false, settingsFile: null, standalone: false, solutionName: "ContosoCustomizations", projectFound: true);

        error.Should().NotBeNull();
        error.Should().Contain("--solution-name");
    }

    [Fact]
    public void ValidateFlags_SolutionNameOutsideAProject_IsAccepted()
    {
        ConfigureCommand.ValidateFlags(
            pullRequested: false, settingsFile: null, standalone: true, solutionName: "ContosoCustomizations", projectFound: false)
            .Should().BeNull();
    }

    [Fact]
    public void ValidateFlags_PullAlone_IsAccepted()
    {
        ConfigureCommand.ValidateFlags(
            pullRequested: true, settingsFile: null, standalone: false, solutionName: null, projectFound: true)
            .Should().BeNull();
    }

    [Fact]
    public void ValidateFlags_SettingsFileAlone_IsAccepted()
    {
        ConfigureCommand.ValidateFlags(
            pullRequested: false, settingsFile: "settings.test.json", standalone: false, solutionName: null, projectFound: true)
            .Should().BeNull();
    }

    // Each rejection names its own flags rather than a shared "invalid mode" — an unattended caller has to
    // know which flag to drop, and one catch-all message tells it nothing.
    [Fact]
    public void ValidateFlags_TheTwoRejections_DoNotShareWording()
    {
        var pullClash = ConfigureCommand.ValidateFlags(true, "settings.json", false, null, true);
        var solutionClash = ConfigureCommand.ValidateFlags(false, null, false, "Contoso", true);

        pullClash.Should().NotBe(solutionClash);
    }

    // ── Messages ─────────────────────────────────────────────────────────────

    [Fact]
    public void BuildStandaloneRoleError_NamesTheRoleAndTheWayOut()
    {
        var error = ConfigureCommand.BuildStandaloneRoleError("prod");

        error.Should().Contain("prod");
        error.Should().Contain("URL", "the message has to say what to pass instead");
    }

    // R11: the run says which file it read and which environment it is about to write, before it writes.
    // There is no confirmation prompt (KTD10), so this line is the only wrong-file-wrong-environment guard
    // an operator gets.
    [Fact]
    public void BuildResolutionNote_NamesTheFileAndTheEnvironment()
    {
        var location = new SettingsFileLocation(
            Path.Combine("C:", "repo", "Solution", "settings.test.json"), SettingsFileSource.RoleConvention, Exists: true);

        var note = ConfigureCommand.BuildResolutionNote(location);

        note.Should().Contain("settings.test.json");
    }

    [Theory]
    [InlineData(SettingsFileSource.Explicit, "--settings-file")]
    [InlineData(SettingsFileSource.RoleConvention, "role convention")]
    [InlineData(SettingsFileSource.SharedFallback, "shared fallback")]
    public void BuildResolutionNote_SaysWhyThatFileWasChosen(SettingsFileSource source, string expected)
    {
        var location = new SettingsFileLocation("settings.json", source, Exists: true);

        ConfigureCommand.BuildResolutionNote(location).Should().Contain(expected);
    }

    [Fact]
    public void BuildDryRunCompleteMessage_SaysNothingWasWrittenAndHowToApply()
    {
        var message = ConfigureCommand.BuildDryRunCompleteMessage("Contoso PROD");

        message.Should().Contain("Contoso PROD");
        message.Should().Contain("untouched");
        message.Should().Contain("--dry-run");
    }

    // Rule 5 of the tone guide: one finish line, always last. A real apply used to end on the summary
    // count and never print one, so a successful run just stopped.
    [Fact]
    public void BuildAppliedMessage_NamesTheEnvironmentAndSaysReRunningIsSafe()
    {
        var message = ConfigureCommand.BuildAppliedMessage("Contoso TEST");

        message.Should().Contain("Contoso TEST");
        message.Should().Contain("Re-run");
    }

    // An interrupted run must not sign off with the success line. It has written to a live environment and
    // stopped partway, so the finish line has to say both halves and point at the re-run.
    [Fact]
    public void BuildCancelledMessage_SaysWhatLandedAndWhatDidnt()
    {
        var message = ConfigureCommand.BuildCancelledMessage("Contoso TEST");

        message.Should().Contain("Contoso TEST");
        message.Should().Contain("the rest weren't");
        message.Should().Contain("Re-run");
        message.Should().NotBe(ConfigureCommand.BuildAppliedMessage("Contoso TEST"));
    }

    [Fact]
    public void BuildPullDryRunMessage_SaysTheFileWasntWritten()
    {
        // A pull's dry run leaves a file unwritten; saying the environment is untouched would describe the
        // wrong thing entirely, since a pull never writes to one.
        var message = ConfigureCommand.BuildPullDryRunMessage("Solution/deploymentSettings.test.json");

        message.Should().Contain("deploymentSettings.test.json");
        message.Should().Contain("wasn't written");
    }

    [Theory]
    [InlineData(1, "1 component in this solution isn't in the file")]
    [InlineData(46, "46 components in this solution aren't in the file")]
    public void BuildUndeclaredWarning_ReadsAsAColleagueWroteIt(int count, string expected)
    {
        ConfigureCommand.BuildUndeclaredWarning(count).Should().StartWith(expected);
    }

    // ── Force vocabulary (KTD10) ─────────────────────────────────────────────

    // configure writes only what the file declares, so it has no destructive scope to gate. Pinned so a
    // later change that adds a confirmation has to break this test first — every CI job already running
    // `configure prod` unattended would stop working.
    [Fact]
    public void ForceVocabulary_CarriesNoConfigureSpecificSpecifier()
    {
        FlowlineSettings.ConfigOnlyValidSpecifiers.Should().BeEquivalentTo(["config", "all"]);
    }
}
