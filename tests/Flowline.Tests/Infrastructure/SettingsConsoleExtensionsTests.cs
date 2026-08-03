using Flowline.Core;
using Flowline.Infrastructure;
using FluentAssertions;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Tests.Infrastructure;

// Confirm takes the console it prompts on, so these no longer swap the static AnsiConsole.Console and
// no longer need a non-parallel collection to serialize that swap. Interactivity is just a TestConsole
// capability: default is non-interactive, .Interactive() opts in.
public class SettingsConsoleExtensionsTests
{
    [Fact]
    public void Confirm_NonInteractive_ForceContainsConfig_ReturnsTrueWithoutPrompting()
    {
        var settings = new FlowlineSettings { Force = ["config"] };

        new TestConsole().Confirm("Overwrite it?", false, settings, "config").Should().BeTrue();
    }

    [Fact]
    public void Confirm_NonInteractive_ForceContainsAll_ReturnsTrueWithoutPrompting()
    {
        var settings = new FlowlineSettings { Force = ["all"] };

        new TestConsole().Confirm("Overwrite it?", false, settings, "config").Should().BeTrue();
    }

    [Fact]
    public void Confirm_NonInteractive_ForceEmpty_ThrowsForceRequiredNamingConfig()
    {
        var settings = new FlowlineSettings { Force = [] };

        var act = () => new TestConsole().Confirm("Overwrite it?", false, settings, "config");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ForceRequired && e.Message.Contains("--force config"));
    }

    [Fact]
    public void Confirm_NonInteractive_ForceContainsMatchingSpecifier_ReturnsTrueWithoutPrompting()
    {
        var settings = new FlowlineSettings { Force = ["first-import"] };

        new TestConsole().Confirm("Continue?", false, settings, "first-import").Should().BeTrue();
    }

    [Fact]
    public void Confirm_NonInteractive_ForceContainsDifferentSpecifier_ThrowsNamingRequestedSpecifier()
    {
        var settings = new FlowlineSettings { Force = ["config"] };

        var act = () => new TestConsole().Confirm("Continue?", false, settings, "first-import");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ForceRequired && e.Message.Contains("--force first-import"));
    }

    [Fact]
    public void Confirm_Interactive_Force_ReturnsTrueWithoutPromptingEvenWhenInteractive()
    {
        // No input pushed — if Confirm tried to prompt, TestConsole would throw on the empty queue.
        var console = new TestConsole().Interactive();
        var settings = new FlowlineSettings { Force = ["first-import"] };

        console.Confirm("Continue?", false, settings, "first-import").Should().BeTrue();
    }

    [Fact]
    public void WriteWelcomeScreen_WritesLogoAndVersion()
    {
        var console = new TestConsole();

        console.WriteWelcomeScreen();

        console.Output.Should().Contain("Flowline CLI v");
    }
}
