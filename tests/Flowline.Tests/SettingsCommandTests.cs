using FluentAssertions;
using Flowline.Commands;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Configure;

namespace Flowline.Tests;

public class SettingsCommandTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public SettingsCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    void WithProject() => File.WriteAllText(Path.Combine(_dir, ProjectConfig.s_configFileName), "{}");

    // ── The --from artifact seam ─────────────────────────────────────────────

    // This is what decides whether a capture hands `pac` --solution-zip or --solution-folder, and getting it
    // wrong fails inside the subprocess, where the cause is hard to read. It is a pure function, so the
    // branch that needs a real `pac` is the only part these cannot reach.

    [Fact]
    public void ResolveSolutionInput_Folder_IsNotAZip()
    {
        var folder = Path.Combine(_dir, "Solution", "src");
        Directory.CreateDirectory(folder);

        var (path, isZip) = SettingsSupport.ResolveSolutionInput(folder);

        isZip.Should().BeFalse();
        path.Should().Be(Path.GetFullPath(folder));
    }

    [Fact]
    public void ResolveSolutionInput_File_IsAZip()
    {
        var zip = Path.Combine(_dir, "ContosoCustomizations.zip");
        File.WriteAllText(zip, "not really a zip, but it exists");

        var (path, isZip) = SettingsSupport.ResolveSolutionInput(zip);

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

            var (path, _) = SettingsSupport.ResolveSolutionInput("Solution");

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

        var act = () => SettingsSupport.ResolveSolutionInput(missing);

        act.Should().Throw<FlowlineException>()
            .Which.ExitCode.Should().Be(ExitCode.NotFound);
    }

    // ── Stand-alone resolution (KTD3) ────────────────────────────────────────

    [Fact]
    public void ResolveStandalone_SolutionNameAndNoProject_IsStandalone()
    {
        SettingsSupport.ResolveStandalone("ContosoCustomizations", null, _dir).Should().BeTrue();
    }

    [Fact]
    public void ResolveStandalone_SolutionNameButProjectFound_IsNotStandalone()
    {
        // The project wins: inside a checkout the solution comes from the project file, and a flag that
        // disagreed with it would apply a settings file against a solution the checkout does not describe.
        WithProject();

        SettingsSupport.ResolveStandalone("ContosoCustomizations", null, _dir).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveStandalone_NeitherFlag_IsNotStandalone(string? solutionName)
    {
        SettingsSupport.ResolveStandalone(solutionName, null, _dir).Should().BeFalse();
    }

    // A stand-alone pull names the solution through the artifact rather than through --solution-name. Judged
    // project mode, it would stop at "No Flowline project found" with the artifact it was handed ignored.
    [Fact]
    public void ResolveStandalone_PullArtifactAndNoProject_IsStandalone()
    {
        SettingsSupport.ResolveStandalone(null, "ContosoCustomizations.zip", _dir).Should().BeTrue();
    }

    [Fact]
    public void ResolveStandalone_PullArtifactButProjectFound_IsNotStandalone()
    {
        WithProject();

        SettingsSupport.ResolveStandalone(null, "ContosoCustomizations.zip", _dir).Should().BeFalse();
    }

    // A bare --pull inside no project still is not stand-alone: the flag carries no value, so nothing names
    // the solution.
    [Fact]
    public void ResolveStandalone_BarePullWithNoValue_IsNotStandalone()
    {
        SettingsSupport.ResolveStandalone(null, null, _dir).Should().BeFalse();
    }

    // ── Flag validation ──────────────────────────────────────────────────────

    // The --pull with --settings-file contradiction the single leaf carried is gone with the flag: the
    // grammar makes that pair unreachable rather than invalid, and --settings-file now names a capture's
    // destination. One rejection is left.

    [Fact]
    public void ValidateFlags_SolutionNameInsideAProject_IsRejected()
    {
        var error = SettingsSupport.ValidateFlags(standalone: false, solutionName: "ContosoCustomizations", projectFound: true);

        error.Should().NotBeNull();
        error.Should().Contain("--solution-name");
    }

    [Fact]
    public void ValidateFlags_SolutionNameOutsideAProject_IsAccepted()
    {
        SettingsSupport.ValidateFlags(standalone: true, solutionName: "ContosoCustomizations", projectFound: false)
            .Should().BeNull();
    }

    [Fact]
    public void ValidateFlags_NoFlags_IsAccepted()
    {
        SettingsSupport.ValidateFlags(standalone: false, solutionName: null, projectFound: true)
            .Should().BeNull();
    }

    // ── Messages ─────────────────────────────────────────────────────────────

    [Fact]
    public void BuildStandaloneRoleError_NamesTheRoleAndTheWayOut()
    {
        var error = SettingsSupport.BuildStandaloneRoleError("prod");

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

        var note = SettingsSupport.BuildResolutionNote(location);

        note.Should().Contain("settings.test.json");
    }

    [Theory]
    [InlineData(SettingsFileSource.Explicit, "--settings-file")]
    [InlineData(SettingsFileSource.RoleConvention, "role convention")]
    [InlineData(SettingsFileSource.SharedFallback, "shared fallback")]
    public void BuildResolutionNote_SaysWhyThatFileWasChosen(SettingsFileSource source, string expected)
    {
        var location = new SettingsFileLocation("settings.json", source, Exists: true);

        SettingsSupport.BuildResolutionNote(location).Should().Contain(expected);
    }

    [Fact]
    public void BuildDryRunCompleteMessage_SaysNothingWasWrittenAndHowToApply()
    {
        var message = SettingsSupport.BuildDryRunCompleteMessage("Contoso PROD");

        message.Should().Contain("Contoso PROD");
        message.Should().Contain("untouched");
        message.Should().Contain("--dry-run");
    }

    // Rule 5 of the tone guide: one finish line, always last. A real apply used to end on the summary
    // count and never print one, so a successful run just stopped.
    [Fact]
    public void BuildAppliedMessage_NamesTheEnvironmentAndSaysReRunningIsSafe()
    {
        var message = SettingsSupport.BuildAppliedMessage("Contoso TEST");

        message.Should().Contain("Contoso TEST");
        message.Should().Contain("Re-run");
    }

    // An interrupted run must not sign off with the success line. It has written to a live environment and
    // stopped partway, so the finish line has to say both halves and point at the re-run.
    [Fact]
    public void BuildCancelledMessage_SaysWhatLandedAndWhatDidnt()
    {
        var message = SettingsSupport.BuildCancelledMessage("Contoso TEST");

        message.Should().Contain("Contoso TEST");
        message.Should().Contain("the rest weren't");
        message.Should().Contain("Re-run");
        message.Should().NotBe(SettingsSupport.BuildAppliedMessage("Contoso TEST"));
    }

    [Fact]
    public void BuildPullDryRunMessage_SaysTheFileWasntWritten()
    {
        // A pull's dry run leaves a file unwritten; saying the environment is untouched would describe the
        // wrong thing entirely, since a pull never writes to one.
        var message = SettingsSupport.BuildPullDryRunMessage("Solution/deploymentSettings.test.json");

        message.Should().Contain("deploymentSettings.test.json");
        message.Should().Contain("wasn't written");
    }

    [Theory]
    [InlineData(1, "1 component in this solution isn't in the file")]
    [InlineData(46, "46 components in this solution aren't in the file")]
    public void BuildUndeclaredWarning_ReadsAsAColleagueWroteIt(int count, string expected)
    {
        SettingsSupport.BuildUndeclaredWarning(count).Should().StartWith(expected);
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

    // ── The bare branch form ─────────────────────────────────────────────────

    // KTD18: the seven operations are two kinds of thing, and a flat list says so nowhere. Registration
    // order is what groups them, so the order here is the contract, not a presentation detail.

    [Fact]
    public void Groups_ListTheWholeFileOperationsBeforeTheComponentKinds()
    {
        var names = SettingsCommand.Groups.SelectMany(g => g.Operations.Select(o => o.Name)).ToArray();

        names.Should().Equal("push", "pull", "flow", "workflow", "plugin", "envvar", "connref");
    }

    [Fact]
    public void Groups_SeparateWholeFileWorkFromSingleComponentWork()
    {
        SettingsCommand.Groups.Should().HaveCount(2);
        SettingsCommand.Groups[0].Operations.Select(o => o.Name).Should().BeEquivalentTo("push", "pull");
        SettingsCommand.Groups[1].Operations.Should().HaveCount(5);
    }

    [Fact]
    public void Groups_EveryOperationCarriesADescription()
    {
        SettingsCommand.Groups
            .SelectMany(g => g.Operations)
            .Should().OnlyContain(o => !string.IsNullOrWhiteSpace(o.Description));
    }

    // ── Capture destination ──────────────────────────────────────────────────

    // KTD17: the one thing a capture must never do is fall back to the shared, un-suffixed file. That file
    // is a live fallback for every environment, so one environment's connection ids written there would be
    // applied everywhere.
    [Fact]
    public void BuildUninferrableRoleError_NamesTheTargetAndTheFlagThatFixesIt()
    {
        var error = SettingsSupport.BuildUninferrableRoleError("https://contoso.crm4.dynamics.com");

        error.Should().Contain("contoso.crm4.dynamics.com");
        error.Should().Contain("--settings-file");
    }

    [Fact]
    public void BuildSweepDestinationError_SaysWhyOnePathCannotServeASweep()
    {
        var error = SettingsSupport.BuildSweepDestinationError();

        error.Should().Contain("--settings-file");
        error.Should().Contain("every configured environment");
    }

    // ── Stand-alone detection after the flag rename ──────────────────────────

    // The artifact flag is --from now, not --pull, but the rule it feeds is unchanged: a flag naming the
    // solution plus no project to read one from.
    [Fact]
    public void ResolveStandalone_ArtifactFlagOutsideAProject_IsStandalone()
    {
        SettingsSupport.ResolveStandalone(null, "artifacts/ContosoCustomizations.zip", _dir).Should().BeTrue();
    }
}
