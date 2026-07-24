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
        var saved = FormEventTestHelpers.SaveAndClearCiVars();
        try
        {
            var console = new TestConsole(); // TestConsole defaults to non-interactive.

            var act = () => console.ConfirmGated("Continue?", false, force: false, "confirmation required");

            act.Should().Throw<FlowlineException>()
                .Where(e => e.ExitCode == ExitCode.ForceRequired && e.Message == "confirmation required");
        }
        finally { FormEventTestHelpers.RestoreCiVars(saved); }
    }

    [Fact]
    public void ConfirmGated_InteractiveNoForce_InvokesBeforePromptThenReturnsPromptAnswer()
    {
        var saved = FormEventTestHelpers.SaveAndClearCiVars();
        try
        {
            var console = new TestConsole();
            console.Interactive();
            console.Input.PushTextWithEnter("y");
            var beforePromptCalled = false;

            var result = console.ConfirmGated("Continue?", false, force: false, "unreachable", beforePrompt: () => beforePromptCalled = true);

            beforePromptCalled.Should().BeTrue();
            result.Should().BeTrue();
        }
        finally { FormEventTestHelpers.RestoreCiVars(saved); }
    }
}
