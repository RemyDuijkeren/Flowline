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

    // ── Stand-alone resolution (KTD3) ────────────────────────────────────────

    [Fact]
    public void ResolveStandalone_SolutionNameAndNoProject_IsStandalone()
    {
        ConfigureCommand.ResolveStandalone("ContosoCustomizations", _dir).Should().BeTrue();
    }

    [Fact]
    public void ResolveStandalone_SolutionNameButProjectFound_IsNotStandalone()
    {
        // The project wins: inside a checkout the solution comes from the project file, and a flag that
        // disagreed with it would apply a settings file against a solution the checkout does not describe.
        WithProject();

        ConfigureCommand.ResolveStandalone("ContosoCustomizations", _dir).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveStandalone_NoSolutionName_IsNotStandalone(string? solutionName)
    {
        ConfigureCommand.ResolveStandalone(solutionName, _dir).Should().BeFalse();
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

        var note = ConfigureCommand.BuildResolutionNote("Contoso TEST", location);

        note.Should().Contain("settings.test.json");
        note.Should().Contain("Contoso TEST");
    }

    [Theory]
    [InlineData(SettingsFileSource.Explicit, "--settings-file")]
    [InlineData(SettingsFileSource.RoleConvention, "role convention")]
    [InlineData(SettingsFileSource.SharedFallback, "shared fallback")]
    public void BuildResolutionNote_SaysWhyThatFileWasChosen(SettingsFileSource source, string expected)
    {
        var location = new SettingsFileLocation("settings.json", source, Exists: true);

        ConfigureCommand.BuildResolutionNote("TEST", location).Should().Contain(expected);
    }

    [Fact]
    public void BuildDryRunCompleteMessage_SaysNothingWasWrittenAndHowToApply()
    {
        var message = ConfigureCommand.BuildDryRunCompleteMessage("Contoso PROD");

        message.Should().Contain("Contoso PROD");
        message.Should().Contain("untouched");
        message.Should().Contain("--dry-run");
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
