using System.Text.RegularExpressions;
using CliWrap;
using Flowline.Core;
using Microsoft.Extensions.Logging;
using Spectre.Console;

namespace Flowline.Diagnostics;

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
    /// Configures stdout/stderr piping for a command. Lines that reach the terminal are captured
    /// by LoggingRenderHook. Lines suppressed from the terminal (non-error, !verbose) are written
    /// directly to ILogger so the log file is always complete.
    /// </summary>
    public Command Apply(Command cmd, StatusContext? ctx = null, Func<string, string>? lineTransform = null)
    {
        var prefix = FormatPrefix(cmd);

        return cmd
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
            {
                SetStatusWithExecutionTime(ctx, line);

                if (IsErrorLine(line))
                {
                    // Always printed to terminal → LoggingRenderHook captures it.
                    DisplayErrorMessage(line, prefix);
                    return;
                }

                // Silently consume PAC async operation progress (status already updated above)
                if (line.StartsWith("Processing asynchronous operation...")) return;

                if (string.IsNullOrWhiteSpace(line)) return;

                if (_options.IsVerbose)
                {
                    var display = lineTransform != null ? lineTransform(line) : line;
                    // Printed to terminal → LoggingRenderHook captures it.
                    _console.MarkupLine($"[dim]{Markup.Escape(prefix)}: {Markup.Escape(display)}[/]");
                }
                else
                {
                    // Suppressed from terminal → LoggingRenderHook never fires; log directly.
                    _logger.LogDebug("{Prefix}: {Line}", prefix, line);
                }
            }))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
            {
                // Always printed to terminal → LoggingRenderHook captures it.
                _console.MarkupLine($"[red]{Markup.Escape(prefix)}: {Markup.Escape(line)}[/]");
            }));
    }

    static readonly string[] s_errorPatterns = ["Error: ", "The reason given was: ", ": error"];
    static readonly string[] s_warningPatterns = [": warning"];

    // dnx runs "dnx <package-id> [args...]" for any tool, so its own name in the log ("dnx:") doesn't
    // say which tool ran. PAC CLI via dnx is the only case Flowline invokes today (GetBestPacCommandAsync
    // always passes "microsoft.powerapps.cli.tool" as dnx's first argument), so relabel it explicitly.
    internal static string FormatPrefix(Command cmd) =>
        cmd.TargetFilePath.Equals("dnx", StringComparison.OrdinalIgnoreCase) &&
        cmd.Arguments.Contains("microsoft.powerapps.cli.tool", StringComparison.OrdinalIgnoreCase)
            ? "pac(dnx)"
            : cmd.TargetFilePath;

    void DisplayErrorMessage(string line, string prefix)
    {
        var color = s_errorPatterns.Any(line.Contains) ? "red" : "yellow";
        _console.MarkupLine($"[{color}]{Markup.Escape(prefix)}: {Markup.Escape(line)}[/]");
    }

    static bool IsErrorLine(string s) =>
        s_errorPatterns.Any(s.Contains) || s_warningPatterns.Any(s.Contains);

    static void SetStatusWithExecutionTime(StatusContext? ctx, string s)
    {
        if (ctx is null || string.IsNullOrWhiteSpace(s) || !s.StartsWith("Processing asynchronous operation...")) return;

        // Extract execution time from e.g. "Processing asynchronous operation... execution time: 00:01:28 and 2.46% of max time allotted"
        var parts = s.Split("... ", 2);
        if (parts.Length < 2) return;
        var execution = parts[1];

        // Append execution part to ctx.Status, replacing any existing "(…)" suffix
        var indexOf = ctx.Status.IndexOf(" (", StringComparison.Ordinal);
        var status = indexOf == -1 ? ctx.Status : ctx.Status[..indexOf];
        ctx.Status($"{status} ([italic]{execution}[/])");
    }

    static readonly Regex s_sensitiveArgPattern =
        new(@"(?<dashFlag>--client-secret)\s+(?:""[^""]*""|\S+)|(?<colonFlag>/mfaClientSecret:)(?:""[^""]*""|\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string RedactSensitiveArgs(string cmdStr) =>
        s_sensitiveArgPattern.Replace(cmdStr, m =>
            m.Groups["dashFlag"].Success ? $"{m.Groups["dashFlag"].Value} ***" : $"{m.Groups["colonFlag"].Value}***");
}
