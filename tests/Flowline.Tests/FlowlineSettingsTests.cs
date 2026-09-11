using Flowline.Commands;
using FluentAssertions;
using Spectre.Console.Cli;

namespace Flowline.Tests;

public class FlowlineSettingsTests
{
    [Fact]
    public void HasForce_IsCaseInsensitive_ForSpecifierValue()
    {
        var settings = new FlowlineSettings { Force = ["CONFIG"] };

        settings.HasForce("config").Should().BeTrue();
    }

    [Fact]
    public void HasForce_IsCaseInsensitive_ForAllValue()
    {
        var settings = new FlowlineSettings { Force = ["ALL"] };

        settings.HasForce("config").Should().BeTrue();
    }

    [Fact]
    public void ValidateForce_IsCaseInsensitive_AcceptsMixedCaseValidValue()
    {
        var act = () => FlowlineSettings.ValidateForce(["Config"], ["config", "all"], "clone");

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateForce_EmptyForce_DoesNotThrow()
    {
        var act = () => FlowlineSettings.ValidateForce([], ["config", "all"], "clone");

        act.Should().NotThrow();
    }

    // -- U1: DataverseSettings option surface (R1/R30) -- CommandApp parse seam, exit-code-only
    // (Spectre renders parse errors via AnsiConsole, not Console.Out, so message content isn't
    // capturable via simple redirection — same limitation PushCommandTests and InitCommandTests
    // document). A no-op wrapper command drives Spectre's real parser against the real Settings
    // shape without needing the command's full DI graph, which parsing itself never touches.
    //
    // No matching "an unknown option fails to parse" negative test exists here: verified against
    // this repo's actual Spectre.Console.Cli configuration (in-process CommandApp probes and the
    // real Release exe — 'flowline diff --no-cache' and 'flowline diff --totally-bogus-flag-xyz'
    // both exit non-zero only for the unrelated "no .cdsproj" reason) that an unrecognized option is
    // silently absorbed, never rejected. R30 is enforced by the reflection checks below instead —
    // the property genuinely isn't there for diff/scaffold/status/sln add to bind.

    sealed class NoOpPushSettingsCommand : Command<PushCommand.Settings>
    {
        protected override int Execute(CommandContext context, PushCommand.Settings settings, CancellationToken cancellationToken) => 0;
    }

    [Fact]
    public void CommandApp_PushNoCache_ParsesSuccessfully()
    {
        var app = new CommandApp();
        app.Configure(c => c.AddCommand<NoOpPushSettingsCommand>("push"));

        var exitCode = app.Run(["push", "--no-cache"]);

        exitCode.Should().Be(0);
    }

    // -- Option membership (R30) -- Spectre renders --help via AnsiConsole, not Console.Out, so
    // content isn't capturable via simple redirection (same limitation documented above). Asserting
    // property presence directly proves the same thing --help would show: which options a command's
    // Settings type carries, mirroring the AutoSwitchProfile_ShortAndLongForm_ParseToSameProperty
    // reflection check in ProfileResolutionServiceTests.

    [Fact]
    public void PushSettings_ExposesNoCacheAutoSwitchProfileAndEnv()
    {
        typeof(PushCommand.Settings).GetProperty(nameof(DataverseSettings.NoCache)).Should().NotBeNull();
        typeof(PushCommand.Settings).GetProperty(nameof(DataverseSettings.AutoSwitchProfile)).Should().NotBeNull();
        typeof(PushCommand.Settings).GetProperty(nameof(EnvironmentSettings.Env)).Should().NotBeNull();
    }

    [Theory]
    [InlineData(typeof(PullCommand.Settings))]
    [InlineData(typeof(GenerateCommand.Settings))]
    [InlineData(typeof(InitCommand.Settings))]
    [InlineData(typeof(CloneCommand.Settings))]
    public void FlagAddressedCommands_ExposeEnv(Type settings)
    {
        settings.GetProperty(nameof(EnvironmentSettings.Env)).Should().NotBeNull();
    }

    // KD1/R2: the target-is-the-point commands keep their positional and never show this --env.
    [Theory]
    [InlineData(typeof(DeployCommand.Settings))]
    [InlineData(typeof(DriftCommand.Settings))]
    [InlineData(typeof(SettingsPushCommand.Settings))]
    [InlineData(typeof(SettingsPullCommand.Settings))]
    public void PositionalTargetCommands_DoNotExposeEnv(Type settings)
    {
        settings.GetProperty(nameof(EnvironmentSettings.Env)).Should().BeNull();
        settings.GetProperty(nameof(DataverseSettings.NoCache)).Should().NotBeNull();
    }

    // Provision keeps its positional role too, but it still needs an --env — for the Production source
    // it copies from, not the environment it acts on — so it declares its own copy directly on
    // DataverseSettings rather than inheriting EnvironmentSettings's.
    [Fact]
    public void ProvisionSettings_DeclaresItsOwnEnv_NotInheritedFromEnvironmentSettings()
    {
        var property = typeof(ProvisionCommand.Settings).GetProperty(nameof(ProvisionCommand.Settings.Env));

        property.Should().NotBeNull();
        property!.DeclaringType.Should().Be(typeof(ProvisionCommand.Settings));
    }

    [Fact]
    public void StatusSettings_HasNeitherNoCacheNorAutoSwitchProfile()
    {
        typeof(StatusCommand.Settings).GetProperty(nameof(DataverseSettings.NoCache)).Should().BeNull();
        typeof(StatusCommand.Settings).GetProperty(nameof(DataverseSettings.AutoSwitchProfile)).Should().BeNull();
    }

    [Fact]
    public void ScaffoldSettings_HasNeitherNoCacheNorAutoSwitchProfile()
    {
        typeof(ScaffoldCommand.Settings).GetProperty(nameof(DataverseSettings.NoCache)).Should().BeNull();
        typeof(ScaffoldCommand.Settings).GetProperty(nameof(DataverseSettings.AutoSwitchProfile)).Should().BeNull();
    }

    [Fact]
    public void SlnAddSettings_HasNeitherNoCacheNorAutoSwitchProfile()
    {
        typeof(SlnAddCommand.Settings).GetProperty(nameof(DataverseSettings.NoCache)).Should().BeNull();
        typeof(SlnAddCommand.Settings).GetProperty(nameof(DataverseSettings.AutoSwitchProfile)).Should().BeNull();
    }
}
