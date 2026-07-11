using System.Text;
using CliWrap;
using Flowline.Core;
using Flowline.Diagnostics;
using FluentAssertions;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Tests;

public class SubprocessCaptureTests
{
    readonly TestConsole _console = new();
    readonly FlowlineRuntimeOptions _options = new();

    public SubprocessCaptureTests()
    {
        // Matches Program.cs wiring — verbose gating is VerboseFilterHook's job, not SubprocessCapture's.
        _console.Pipeline.Attach(new VerboseFilterHook(_options));
    }

    SubprocessCapture CreateCapture() => new(_console);

    // Cli.Wrap("echo") is never executed — used only to get a Command with TargetFilePath = "echo"
    static Command BaseCmd() => Cli.Wrap("echo");

    static async Task PumpStdoutAsync(Command cmd, params string[] lines)
    {
        var data = string.Join("\n", lines) + "\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(data));
        await cmd.StandardOutputPipe.CopyFromAsync(stream, CancellationToken.None);
    }

    static async Task PumpStderrAsync(Command cmd, params string[] lines)
    {
        var data = string.Join("\n", lines) + "\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(data));
        await cmd.StandardErrorPipe.CopyFromAsync(stream, CancellationToken.None);
    }

    // Test 1: non-error stdout + IsVerbose=false → suppressed from terminal (VerboseFilterHook's job;
    // LoggingRenderHook still logs it in production — covered by LoggingRenderHookTests)
    [Fact]
    public async Task Apply_NonErrorStdout_NotVerbose_NoTerminalOutput()
    {
        _options.IsVerbose = false;
        var cmd = CreateCapture().Apply(BaseCmd());

        await PumpStdoutAsync(cmd, "normal output");

        _console.Output.Should().BeEmpty();
    }

    // Test 2: non-error stdout + IsVerbose=true → terminal output (dim)
    [Fact]
    public async Task Apply_NonErrorStdout_Verbose_PrintsToTerminal()
    {
        _options.IsVerbose = true;
        var cmd = CreateCapture().Apply(BaseCmd());

        await PumpStdoutAsync(cmd, "normal output");

        _console.Output.Should().Contain("normal output");
    }

    // Regression: SubprocessCapture used to wrap its message in a literal "[dim]...[/]" before passing to
    // console.Verbose(string) — which already wraps and escapes internally (VerboseRenderable) — so the
    // caller's own literal tags got escaped and shown as visible "[dim]"/"[/]" text instead of being
    // interpreted as markup.
    [Fact]
    public async Task Apply_NonErrorStdout_Verbose_DoesNotLeakLiteralMarkupTags()
    {
        _options.IsVerbose = true;
        var cmd = CreateCapture().Apply(BaseCmd());

        await PumpStdoutAsync(cmd, "normal output");

        _console.Output.Should().NotContain("[dim]");
        _console.Output.Should().NotContain("[/]");
    }

    // Test 3: error-matching stdout → always printed to terminal, regardless of verbosity
    [Fact]
    public async Task Apply_ErrorStdout_NotVerbose_PrintsToTerminal()
    {
        _options.IsVerbose = false;
        var cmd = CreateCapture().Apply(BaseCmd());

        await PumpStdoutAsync(cmd, "Error: something failed");

        _console.Output.Should().Contain("Error: something failed");
    }

    // Test 4: warning-matching stdout → always printed to terminal, regardless of verbosity
    [Fact]
    public async Task Apply_WarningStdout_NotVerbose_PrintsToTerminal()
    {
        _options.IsVerbose = false;
        var cmd = CreateCapture().Apply(BaseCmd());

        await PumpStdoutAsync(cmd, "MyPlugin: warning CS8600: Converting null literal");

        _console.Output.Should().Contain("warning CS8600");
    }

    // Test 5: stderr → always printed to terminal in red
    [Fact]
    public async Task Apply_Stderr_PrintsRed()
    {
        var cmd = CreateCapture().Apply(BaseCmd());

        await PumpStderrAsync(cmd, "error on stderr");

        _console.Output.Should().Contain("error on stderr");
    }

    // Test 6: lineTransform — transformed line displayed on terminal when verbose
    [Fact]
    public async Task Apply_LineTransform_DisplaysTransformed()
    {
        _options.IsVerbose = true;
        var cmd = CreateCapture().Apply(BaseCmd(), lineTransform: s => s.ToUpperInvariant());

        await PumpStdoutAsync(cmd, "hello");

        _console.Output.Should().Contain("HELLO");
    }

    // Test 7: PAC progress line with real ctx → SetStatusWithExecutionTime updates ctx.Status
    [Fact]
    public async Task Apply_PacProgressLine_WithCtx_StatusUpdated()
    {
        var capture = CreateCapture();
        string statusAfter = "";
        const string pacLine = "Processing asynchronous operation... execution time: 00:01:28 and 2.46% of max time allotted";

        await _console.Status().StartAsync("Initial", async ctx =>
        {
            var cmd = capture.Apply(BaseCmd(), ctx: ctx);
            await PumpStdoutAsync(cmd, pacLine);
            statusAfter = ctx.Status;
        });

        statusAfter.Should().Contain("execution time:");
    }

    // Test 8: PAC progress line with null ctx → SetStatusWithExecutionTime is a no-op, no exception
    [Fact]
    public async Task Apply_PacProgressLine_NullCtx_NoException()
    {
        var cmd = CreateCapture().Apply(BaseCmd(), ctx: null);
        const string pacLine = "Processing asynchronous operation... execution time: 00:01:28 and 2.46% of max time allotted";

        Func<Task> act = async () => await PumpStdoutAsync(cmd, pacLine);

        await act.Should().NotThrowAsync();
    }

    // Test 9: dnx running PAC CLI → prefix relabeled to "pac(dnx)" so the log line isn't ambiguous
    [Fact]
    public async Task Apply_DnxRunningPac_PrefixIsRelabeled()
    {
        _options.IsVerbose = true;
        var cmd = Cli.Wrap("dnx").WithArguments(["microsoft.powerapps.cli.tool", "--yes", "solution", "online-version"]);
        var wrapped = CreateCapture().Apply(cmd);

        await PumpStdoutAsync(wrapped, "Connected to... AutomateValue");

        _console.Output.Should().Contain("pac(dnx): Connected to... AutomateValue");
        _console.Output.Should().NotContain("dnx: Connected");
    }

    // Test 10: dnx running some other tool → prefix stays "dnx" (only relabel PAC CLI specifically)
    [Fact]
    public async Task Apply_DnxRunningOtherTool_PrefixStaysDnx()
    {
        _options.IsVerbose = true;
        var cmd = Cli.Wrap("dnx").WithArguments(["some.other.tool", "--yes"]);
        var wrapped = CreateCapture().Apply(cmd);

        await PumpStdoutAsync(wrapped, "doing something");

        _console.Output.Should().Contain("dnx: doing something");
    }

    // Test 11: non-dnx command → prefix unchanged (matches TargetFilePath, as before)
    [Fact]
    public async Task Apply_NonDnxCommand_PrefixIsTargetFilePath()
    {
        _options.IsVerbose = true;
        var cmd = CreateCapture().Apply(BaseCmd());

        await PumpStdoutAsync(cmd, "hello world");

        _console.Output.Should().Contain("echo: hello world");
    }
}
