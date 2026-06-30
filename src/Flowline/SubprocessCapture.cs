using CliWrap;
using Flowline.Core;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Flowline;

/// <summary>
/// DI-injectable subprocess output capture. Always routes stdout/stderr to Serilog via ILogger,
/// regardless of --verbose. Replaces the static WithToolExecutionLog extension methods.
/// </summary>
public sealed class SubprocessCapture
{
    readonly ILogger<SubprocessCapture> _logger;
    readonly FlowlineRuntimeOptions _options;
    readonly IAnsiConsole _console;

    public SubprocessCapture(ILogger<SubprocessCapture> logger, FlowlineRuntimeOptions options, IAnsiConsole console)
    {
        _logger = logger;
        _options = options;
        _console = console;
    }

    /// <summary>
    /// Configures stdout/stderr piping for a command. All output is logged via ILogger (Serilog).
    /// Error/warning lines always print to terminal; non-error lines print only when verbose.
    /// VerboseOutputBuffer is intentionally not written — ILogger covers the buffered-log concern.
    /// </summary>
    public Command Apply(Command cmd, StatusContext? ctx = null, Func<string, string>? lineTransform = null)
    {
        var prefix = cmd.TargetFilePath;

        return cmd
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
            {
                _logger.LogDebug("{Line}", line);

                SetStatusWithExecutionTime(ctx, line);

                if (IsErrorLine(line))
                {
                    DisplayErrorMessage(line, prefix);
                    return;
                }

                // Silently consume PAC async operation progress (status already updated above)
                if (line.StartsWith("Processing asynchronous operation...")) return;

                if (string.IsNullOrWhiteSpace(line)) return;

                if (_options.IsVerbose)
                {
                    var display = lineTransform != null ? lineTransform(line) : line;
                    _console.MarkupLine($"[dim]{Markup.Escape(prefix)}: {Markup.Escape(display)}[/]");
                }
            }))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
            {
                _logger.LogDebug("{Line}", line);
                // Stderr is always immediately visible — do not buffer in VerboseOutputBuffer;
                // that would cause double-print in FlushBufferedVerboseOutput on error.
                _console.MarkupLine($"[red]{Markup.Escape(prefix)}: {Markup.Escape(line)}[/]");
            }));
    }

    void DisplayErrorMessage(string line, string prefix)
    {
        if (line.Contains("Error: ") || line.Contains("The reason given was: "))
        {
            _console.MarkupLine($"[red]{Markup.Escape(prefix)}: {Markup.Escape(line)}[/]");
            return;
        }
        if (line.Contains(": error"))
        {
            _console.MarkupLine($"[red]{Markup.Escape(prefix)}: {Markup.Escape(line)}[/]");
            return;
        }
        if (line.Contains(": warning"))
        {
            _console.MarkupLine($"[yellow]{Markup.Escape(prefix)}: {Markup.Escape(line)}[/]");
        }
    }

    static bool IsErrorLine(string s) =>
        s.Contains("Error: ") || s.Contains("The reason given was: ") || s.Contains(": error") || s.Contains(": warning");

    static void SetStatusWithExecutionTime(StatusContext? ctx, string s)
    {
        if (ctx is null || string.IsNullOrWhiteSpace(s) || !s.StartsWith("Processing asynchronous operation...")) return;

        // Extract execution time from e.g. "Processing asynchronous operation... execution time: 00:01:28 and 2.46% of max time allotted"
        var execution = s.Split("... ")[1];

        // Append execution part to ctx.Status, replacing any existing "(…)" suffix
        var indexOf = ctx.Status.IndexOf(" (", StringComparison.Ordinal);
        var status = indexOf == -1 ? ctx.Status : ctx.Status[..indexOf];
        ctx.Status($"{status} ([italic]{execution}[/])");
    }
}
