using FluentAssertions;
using Flowline.Core.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Core.Tests;

public class TerminalSignalsTests
{
    static (TerminalSignals Signals, StringWriter Escapes, List<string> Titles) Make(
        bool interactive = true, bool ansi = true, bool errorRedirected = false)
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = interactive;
        console.Profile.Capabilities.Ansi = ansi;

        var escapes = new StringWriter();
        var titles = new List<string>();
        return (new TerminalSignals(console, errorRedirected, escapes, titles.Add), escapes, titles);
    }

    [Fact]
    public void NonInteractiveConsole_WritesNothing()
    {
        var (signals, escapes, titles) = Make(interactive: false);

        signals.ShowProgress();
        signals.SetTitle("flowline deploy prod");
        signals.ClearProgress();

        signals.Enabled.Should().BeFalse();
        escapes.ToString().Should().BeEmpty();
        titles.Should().BeEmpty();
    }

    [Fact]
    public void ConsoleWithoutAnsi_WritesNothing()
    {
        var (signals, escapes, titles) = Make(ansi: false);

        signals.ShowProgress();
        signals.SetTitle("flowline deploy prod");
        signals.ClearProgress();

        signals.Enabled.Should().BeFalse();
        escapes.ToString().Should().BeEmpty();
        titles.Should().BeEmpty();
    }

    [Fact]
    public void RedirectedErrorStream_WritesNothing()
    {
        var (signals, escapes, titles) = Make(errorRedirected: true);

        signals.ShowProgress();
        signals.SetTitle("flowline deploy prod");
        signals.ClearProgress();

        signals.Enabled.Should().BeFalse();
        escapes.ToString().Should().BeEmpty();
        titles.Should().BeEmpty();
    }

    [Fact]
    public void ShowProgress_EmitsIndeterminateState()
    {
        var (signals, escapes, _) = Make();

        signals.ShowProgress();

        // OSC 9;4;<state>;<progress> BEL — state 3 is "indeterminate".
        escapes.ToString().Should().Be("\e]9;4;3;0\a");
    }

    [Fact]
    public void ClearProgress_EmitsRemoveStateNotASecondIndeterminate()
    {
        var (signals, escapes, _) = Make();

        signals.ClearProgress();

        // State 0 is "remove". Emitting 3 again would leave the indicator running.
        escapes.ToString().Should().Be("\e]9;4;0;0\a");
    }

    [Fact]
    public void SetTitle_PassesTheComposedStringThrough()
    {
        var (signals, escapes, titles) = Make();

        signals.SetTitle("flowline deploy prod");

        titles.Should().ContainSingle().Which.Should().Be("flowline deploy prod");
        // The title goes through the platform title API, not the escape stream (KTD7).
        escapes.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Gate_IsReadOnceSoAMidRunCapabilityChangeCannotSplitAPair()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Capabilities.Ansi = true;
        var escapes = new StringWriter();
        var signals = new TerminalSignals(console, errorRedirected: false, escapes, _ => { });

        signals.ShowProgress();
        console.Profile.Capabilities.Interactive = false; // would suppress the clear if re-read
        signals.ClearProgress();

        escapes.ToString().Should().Be("\e]9;4;3;0\a\e]9;4;0;0\a");
    }
}
