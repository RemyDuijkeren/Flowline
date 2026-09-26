using FluentAssertions;
using Flowline.Core;
using Flowline.Core.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Core.Tests;

public class FlowlineConsoleExtensionsTests
{
    [Fact]
    public void Verbose_WritesVerboseRenderableToConsole()
    {
        var console = new TestConsole();
        var options = new FlowlineRuntimeOptions();

        console.Verbose("test message");

        console.Output.Should().Contain("test message");
    }

    [Fact]
    public void Verbose_EmitsVerboseRenderableUnconditionally()
    {
        // Without VFH in the pipeline, VerboseRenderable always reaches the console —
        // suppression is VFH's responsibility, not Console.Verbose's.
        var console = new TestConsole();
        var options = new FlowlineRuntimeOptions { IsVerbose = false };

        console.Verbose("always written");

        console.Output.Should().Contain("always written");
    }

    [Fact]
    public void ConfirmGated_Force_ReturnsTrueWithoutPromptingEvenWhenInteractive()
    {
        var console = new TestConsole();
        console.Interactive();
        // No input pushed — if ConfirmGated tried to prompt, TestConsole would throw on the empty queue.

        var result = console.ConfirmGated("Continue?", false, force: true, "unreachable");

        result.Should().BeTrue();
    }

    [Fact]
    public void ConfirmGated_Force_SkipsBeforePromptAndPrintsSkipLine()
    {
        var console = new TestConsole();
        console.Interactive();
        var beforePromptCalled = false;

        console.ConfirmGated("Continue?", false, force: true, "unreachable", beforePrompt: () => beforePromptCalled = true);

        beforePromptCalled.Should().BeFalse();
        console.Output.Should().Contain("Continue? (--force)");
    }

    [Fact]
    public void ConfirmGated_NonInteractiveNoForce_ThrowsForceRequiredWithGivenMessage()
    {
        var console = new TestConsole(); // TestConsole defaults to non-interactive.

        var act = () => console.ConfirmGated("Continue?", false, force: false, "confirmation required");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ForceRequired && e.Message == "confirmation required");
    }

    [Fact]
    public void ConfirmGated_InteractiveNoForce_InvokesBeforePromptThenReturnsPromptAnswer()
    {
        var console = new TestConsole();
        console.Interactive();
        console.Input.PushTextWithEnter("y");
        var beforePromptCalled = false;

        var result = console.ConfirmGated("Continue?", false, force: false, "unreachable", beforePrompt: () => beforePromptCalled = true);

        beforePromptCalled.Should().BeTrue();
        result.Should().BeTrue();
    }

    // Confirm: --force auto-accepts the matching specifier, and a non-interactive run without it names the
    // specifier it needs. Interactivity is a TestConsole capability: default off, .Interactive() opts in.

    [Fact]
    public void Confirm_NonInteractive_ForceContainsConfig_ReturnsTrueWithoutPrompting()
    {
        var options = new FlowlineRuntimeOptions { Force = ["config"] };

        new TestConsole().Confirm("Overwrite it?", false, options, "config").Should().BeTrue();
    }

    [Fact]
    public void Confirm_NonInteractive_ForceContainsAll_ReturnsTrueWithoutPrompting()
    {
        var options = new FlowlineRuntimeOptions { Force = ["all"] };

        new TestConsole().Confirm("Overwrite it?", false, options, "config").Should().BeTrue();
    }

    [Fact]
    public void Confirm_NonInteractive_ForceEmpty_ThrowsForceRequiredNamingConfig()
    {
        var options = new FlowlineRuntimeOptions { Force = [] };

        var act = () => new TestConsole().Confirm("Overwrite it?", false, options, "config");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ForceRequired && e.Message.Contains("--force config"));
    }

    [Fact]
    public void Confirm_NonInteractive_ForceContainsMatchingSpecifier_ReturnsTrueWithoutPrompting()
    {
        var options = new FlowlineRuntimeOptions { Force = ["first-import"] };

        new TestConsole().Confirm("Continue?", false, options, "first-import").Should().BeTrue();
    }

    [Fact]
    public void Confirm_NonInteractive_ForceContainsDifferentSpecifier_ThrowsNamingRequestedSpecifier()
    {
        var options = new FlowlineRuntimeOptions { Force = ["config"] };

        var act = () => new TestConsole().Confirm("Continue?", false, options, "first-import");

        act.Should().Throw<FlowlineException>()
            .Where(e => e.ExitCode == ExitCode.ForceRequired && e.Message.Contains("--force first-import"));
    }

    [Fact]
    public void Confirm_Interactive_Force_ReturnsTrueWithoutPromptingEvenWhenInteractive()
    {
        // No input pushed — if Confirm tried to prompt, TestConsole would throw on the empty queue.
        var console = new TestConsole().Interactive();
        var options = new FlowlineRuntimeOptions { Force = ["first-import"] };

        console.Confirm("Continue?", false, options, "first-import").Should().BeTrue();
    }
}
