using FluentAssertions;
using Flowline.Core;
using Flowline.Core.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Tests;

// Program.cs is top-level statements and cannot be exercised directly, so the two things its wiring
// is responsible for — naming the tab and reporting an outcome on every path out of the command app —
// live behind seams these tests drive.
public class TerminalTabStatusWiringTests
{
    const string Remove = "\e]9;4;0;0\a";

    static (TerminalTabStatus Status, StringWriter Escapes, List<string> Titles) Make()
    {
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Profile.Capabilities.Ansi = true;
        var escapes = new StringWriter();
        var titles = new List<string>();
        var signals = new TerminalSignals(console, errorRedirected: false, escapes, titles.Add);
        return (TerminalTabStatus.ForTest(signals, "flowline deploy prod"), escapes, titles);
    }

    [Theory]
    [InlineData(new[] { "deploy", "prod" }, "flowline deploy prod")]
    [InlineData(new[] { "sync" }, "flowline sync")]
    [InlineData(new string[0], "flowline")]
    [InlineData(new[] { "--version" }, "flowline")]
    [InlineData(new[] { "deploy", "prod", "--force" }, "flowline deploy prod")]
    [InlineData(new[] { "sln", "add", "Solution/My.cdsproj" }, "flowline sln add")]
    // An option's value must never reach the title — it stops at the first option, it does not skip it.
    [InlineData(new[] { "sync", "--dev", "https://contoso-dev.crm4.dynamics.com" }, "flowline sync")]
    [InlineData(new[] { "generate", "--client-secret", "s3cr3t" }, "flowline generate")]
    public void LabelFor_NamesTheCommandAndItsTarget(string[] args, string expected)
        => TerminalTabStatus.LabelFor(args, "flowline").Should().Be(expected);

    [Fact]
    public void LabelFor_NeverLeaksAnOptionValueIntoTheTitle()
    {
        const string secret = "super-secret-value";

        var label = TerminalTabStatus.LabelFor(["generate", "--client-secret", secret], "flowline");

        label.Should().NotContain(secret);
    }

    [Fact]
    public void LabelFor_BoundsAnAbsurdlyLongArgument()
    {
        var label = TerminalTabStatus.LabelFor(["deploy", new string('x', 40_000)], "flowline");

        // The platform title setter rejects a very long title, and it is set from a timer callback
        // where the throw would take the process down.
        label.Length.Should().BeLessThanOrEqualTo(200);
    }

    [Fact]
    public async Task RunAsync_ReturnsTheWrappedExitCode()
    {
        var (status, _, _) = Make();

        var result = await status.RunAsync(() => Task.FromResult((int)ExitCode.BuildFailed));

        result.Should().Be((int)ExitCode.BuildFailed);
    }

    [Fact]
    public async Task RunAsync_FailureExitCode_ReportsFailure()
    {
        var (status, escapes, titles) = Make();
        status.Reveal();

        await status.RunAsync(() => Task.FromResult((int)ExitCode.BuildFailed));

        escapes.ToString().Should().EndWith(Remove);
        titles.Last().Should().StartWith(FlowlineTheme.ErrorPrefix);
    }

    [Fact]
    public async Task RunAsync_CancelledExitCode_ReportsCancelledNotFailure()
    {
        var (status, escapes, titles) = Make();
        status.Reveal();

        await status.RunAsync(() => Task.FromResult((int)ExitCode.Cancelled));

        escapes.ToString().Should().EndWith(Remove);
        titles.Last().Should().StartWith(FlowlineTheme.WarningPrefix);
        titles.Last().Should().NotStartWith(FlowlineTheme.ErrorPrefix);
    }

    [Fact]
    public async Task RunAsync_WrappedCallThrows_ReportsFailureAndRethrows()
    {
        var (status, escapes, titles) = Make();
        status.Reveal();

        // Debug builds call PropagateExceptions(), so the exception escapes the command app rather
        // than becoming an exit code. The indicator must still be cleared on the way out.
        var act = async () => await status.RunAsync(() => throw new InvalidOperationException("boom"));

        await act.Should().ThrowAsync<InvalidOperationException>();
        escapes.ToString().Should().EndWith(Remove);
        titles.Last().Should().StartWith(FlowlineTheme.ErrorPrefix);
    }

    [Fact]
    public async Task ProcessExitPath_ClearsAnIndicatorTheWrapperNeverGotToClear()
    {
        var (status, escapes, _) = Make();
        status.Reveal();

        // Environment.Exit terminates without unwinding, so the wrapper's finally never runs; the
        // ProcessExit handler calls Finish instead.
        status.Finish((int)ExitCode.GeneralError);

        escapes.ToString().Should().EndWith(Remove);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task ProcessExitAfterANormalRun_WritesNothingASecondTime()
    {
        var (status, escapes, titles) = Make();
        status.Reveal();

        await status.RunAsync(() => Task.FromResult(0));
        var afterRun = escapes.ToString();
        var titlesAfterRun = titles.Count;

        status.Finish((int)ExitCode.GeneralError); // the handler firing on normal shutdown

        escapes.ToString().Should().Be(afterRun);
        titles.Should().HaveCount(titlesAfterRun);
    }
}
