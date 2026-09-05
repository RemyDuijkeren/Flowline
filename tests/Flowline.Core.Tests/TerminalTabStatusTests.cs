using FluentAssertions;
using Flowline.Core;
using Flowline.Core.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Core.Tests;

public class TerminalTabStatusTests
{
    const string Indeterminate = "\e]9;4;3;0\a";
    const string Remove = "\e]9;4;0;0\a";
    const string Label = "flowline deploy prod";

    sealed class Harness
    {
        public StringWriter Escapes { get; } = new();
        public List<string> Titles { get; } = [];
        public TerminalTabStatus Status { get; }

        public Harness(bool enabled = true)
        {
            var console = new TestConsole();
            console.Profile.Capabilities.Interactive = enabled;
            console.Profile.Capabilities.Ansi = enabled;
            var signals = new TerminalSignals(console, errorRedirected: false, Escapes, Titles.Add);
            Status = TerminalTabStatus.ForTest(signals, Label);
        }

        public string Written => Escapes.ToString();
    }

    [Fact]
    public void FinishBeforeReveal_WritesNothingAtAll()
    {
        var h = new Harness();

        h.Status.Finish(0);

        h.Written.Should().BeEmpty();
        h.Titles.Should().BeEmpty();
    }

    [Fact]
    public void Reveal_SetsIndeterminateProgressAndATitleCarryingTheLabel()
    {
        var h = new Harness();

        h.Status.Reveal();

        h.Written.Should().Be(Indeterminate);
        h.Titles.Should().ContainSingle().Which.Should().Be(Label);
    }

    [Fact]
    public void RevealedThenFailed_ClearsProgressAndMarksTheTitleAsFailed()
    {
        var h = new Harness();

        h.Status.Reveal();
        h.Status.Finish((int)ExitCode.BuildFailed);

        h.Written.Should().Be(Indeterminate + Remove);
        h.Titles.Last().Should().StartWith(FlowlineTheme.ErrorPrefix).And.Contain(Label);
    }

    [Fact]
    public void RevealedThenSucceeded_MarksTheTitleAsSuccess()
    {
        var h = new Harness();

        h.Status.Reveal();
        h.Status.Finish(0);

        h.Written.Should().Be(Indeterminate + Remove);
        h.Titles.Last().Should().StartWith(FlowlineTheme.OkPrefix).And.Contain(Label);
    }

    [Fact]
    public void RevealedThenCancelled_UsesAMarkerDistinctFromFailure()
    {
        var cancelled = new Harness();
        cancelled.Status.Reveal();
        cancelled.Status.Finish((int)ExitCode.Cancelled);

        var failed = new Harness();
        failed.Status.Reveal();
        failed.Status.Finish((int)ExitCode.GeneralError);

        cancelled.Titles.Last().Should().NotBe(failed.Titles.Last());
        cancelled.Titles.Last().Should().StartWith(FlowlineTheme.WarningPrefix).And.Contain(Label);
    }

    // The defect this type exists to prevent: a run that finishes at the same instant the reveal
    // fires must never leave the progress state set. Both orderings are asserted because the two
    // sides race in production and only one of them can win.
    [Fact]
    public void RevealAndFinishAtTheSameInstant_NeverLeavesProgressSet()
    {
        var finishFirst = new Harness();
        finishFirst.Status.Finish(0);
        finishFirst.Status.Reveal();
        finishFirst.Written.Should().BeEmpty("finish claimed the run before the reveal could write");

        var revealFirst = new Harness();
        revealFirst.Status.Reveal();
        revealFirst.Status.Finish(0);
        revealFirst.Written.Should().EndWith(Remove, "the clear must be the last thing written");
    }

    [Fact]
    public void RevealAndFinishRacingOnRealThreads_NeverLeavesProgressSet()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var h = new Harness();
            using var gate = new ManualResetEventSlim();

            var reveal = Task.Run(() => { gate.Wait(); h.Status.Reveal(); });
            var finish = Task.Run(() => { gate.Wait(); h.Status.Finish(0); });
            gate.Set();
            Task.WaitAll(reveal, finish);

            var written = h.Written;
            written.Should().BeOneOf("", Indeterminate + Remove);
        }
    }

    [Fact]
    public void FinishCalledTwice_WritesOnlyOnce()
    {
        var h = new Harness();

        h.Status.Reveal();
        h.Status.Finish(0);
        h.Status.Finish((int)ExitCode.GeneralError);

        h.Written.Should().Be(Indeterminate + Remove);
        h.Titles.Should().HaveCount(2); // the running title, then one outcome title
    }

    [Fact]
    public void SuppressedConsole_WritesNothingWhateverTheExitCode()
    {
        var h = new Harness(enabled: false);

        h.Status.Reveal();
        h.Status.Finish((int)ExitCode.GeneralError);

        h.Written.Should().BeEmpty();
        h.Titles.Should().BeEmpty();
    }
}
