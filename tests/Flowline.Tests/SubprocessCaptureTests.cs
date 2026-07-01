using System.Text;
using CliWrap;
using Flowline.Core;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Testing;
using Xunit;

namespace Flowline.Tests;

public class SubprocessCaptureTests
{
    readonly CaptureLogger _logger = new();
    readonly TestConsole _console = new();
    readonly FlowlineRuntimeOptions _options = new();

    SubprocessCapture CreateCapture() => new(_logger, _options, _console);

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

    // Test 1: canary — verifies pipe mechanism works and ILogger.Debug fires
    [Fact]
    public async Task Apply_StdoutLine_LogsDebug()
    {
        var cmd = CreateCapture().Apply(BaseCmd());

        await PumpStdoutAsync(cmd, "hello world");

        _logger.Entries.Should().ContainSingle(e => e.Level == LogLevel.Debug && e.Message.Contains("hello world"));
    }

    // Test 2: non-error stdout + IsVerbose=false → no terminal output, debug still logged
    [Fact]
    public async Task Apply_NonErrorStdout_NotVerbose_NoTerminalOutput()
    {
        _options.IsVerbose = false;
        var cmd = CreateCapture().Apply(BaseCmd());

        await PumpStdoutAsync(cmd, "normal output");

        _logger.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Message.Contains("normal output"));
        _console.Output.Should().BeEmpty();
    }

    // Test 3: non-error stdout + IsVerbose=true → terminal output (dim); LoggingRenderHook handles log capture in production
    [Fact]
    public async Task Apply_NonErrorStdout_Verbose_PrintsToTerminal()
    {
        _options.IsVerbose = true;
        var cmd = CreateCapture().Apply(BaseCmd());

        await PumpStdoutAsync(cmd, "normal output");

        _console.Output.Should().Contain("normal output");
        _logger.Entries.Should().BeEmpty(); // LRH (not direct ILogger) captures terminal lines
    }

    // Test 4: error-matching stdout → always printed to terminal; LRH captures in production
    [Fact]
    public async Task Apply_ErrorStdout_NotVerbose_PrintsToTerminal()
    {
        _options.IsVerbose = false;
        var cmd = CreateCapture().Apply(BaseCmd());

        await PumpStdoutAsync(cmd, "Error: something failed");

        _console.Output.Should().Contain("Error: something failed");
        _logger.Entries.Should().BeEmpty(); // LRH (not direct ILogger) captures terminal lines
    }

    // Test 5: warning-matching stdout → always printed to terminal; LRH captures in production
    [Fact]
    public async Task Apply_WarningStdout_NotVerbose_PrintsToTerminal()
    {
        _options.IsVerbose = false;
        var cmd = CreateCapture().Apply(BaseCmd());

        await PumpStdoutAsync(cmd, "MyPlugin: warning CS8600: Converting null literal");

        _console.Output.Should().Contain("warning CS8600");
        _logger.Entries.Should().BeEmpty(); // LRH (not direct ILogger) captures terminal lines
    }

    // Test 6: stderr → always printed to terminal in red; LRH captures in production
    [Fact]
    public async Task Apply_Stderr_PrintsRed()
    {
        var cmd = CreateCapture().Apply(BaseCmd());

        await PumpStderrAsync(cmd, "error on stderr");

        _console.Output.Should().Contain("error on stderr");
        _logger.Entries.Should().BeEmpty(); // LRH (not direct ILogger) captures terminal lines
    }

    // Test 7: lineTransform — transformed line displayed on terminal when verbose; LRH handles log capture
    [Fact]
    public async Task Apply_LineTransform_DisplaysTransformed()
    {
        _options.IsVerbose = true;
        var cmd = CreateCapture().Apply(BaseCmd(), lineTransform: s => s.ToUpperInvariant());

        await PumpStdoutAsync(cmd, "hello");

        _console.Output.Should().Contain("HELLO");
    }

    // Test 8: PAC progress line with real ctx → SetStatusWithExecutionTime updates ctx.Status
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

    // Test 9: PAC progress line with null ctx → SetStatusWithExecutionTime is a no-op, no exception
    [Fact]
    public async Task Apply_PacProgressLine_NullCtx_NoException()
    {
        var cmd = CreateCapture().Apply(BaseCmd(), ctx: null);
        const string pacLine = "Processing asynchronous operation... execution time: 00:01:28 and 2.46% of max time allotted";

        Func<Task> act = async () => await PumpStdoutAsync(cmd, pacLine);

        await act.Should().NotThrowAsync();
    }

    private sealed class CaptureLogger : ILogger<SubprocessCapture>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));

        public bool IsEnabled(LogLevel logLevel) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    }
}
