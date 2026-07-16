using System.Text.RegularExpressions;
using CliWrap;
using Flowline.Core;
using Spectre.Console;

namespace Flowline.Diagnostics;

/// <summary>
/// DI-injectable subprocess output capture. Non-error stdout always routes through
/// <c>console.Verbose()</c>, so LoggingRenderHook logs it regardless of --verbose while
/// VerboseFilterHook decides terminal visibility. Replaces the static WithToolExecutionLog
/// extension methods.
/// </summary>
public sealed class SubprocessCapture
{
    readonly IAnsiConsole _console;

    public SubprocessCapture(IAnsiConsole console)
    {
        _console = console;
    }

    /// <summary>
    /// Configures stdout/stderr piping for a command. Error/warning lines and stderr are always
    /// printed. Other stdout lines go through console.Verbose() — always logged, terminal-visible
    /// only with --verbose.
    /// </summary>
    /// <param name="suppressErrors">
    /// Set for probe commands whose non-zero exit is an expected, caller-handled outcome (e.g. git
    /// queries on a repo with no commits yet) — stderr is only logged via console.Verbose(), never
    /// echoed in red, so callers can report their own friendly message without raw tool noise.
    /// </param>
    public Command Apply(Command cmd, StatusContext? ctx = null, Func<string, string>? lineTransform = null, bool suppressErrors = false)
    {
        var prefix = FormatPrefix(cmd);

        return cmd
            .WithStandardOutputPipe(PipeTarget.ToDelegate(line =>
            {
                SetStatusWithExecutionTime(ctx, line);

                if (!suppressErrors && IsErrorLine(line))
                {
                    DisplayErrorMessage(line, prefix);
                    return;
                }

                // Silently consume PAC async operation progress (status already updated above)
                if (line.StartsWith("Processing asynchronous operation...")) return;

                if (string.IsNullOrWhiteSpace(line)) return;

                var display = lineTransform != null ? lineTransform(line) : line;
                // console.Verbose(string) already wraps in [dim]...[/] and escapes internally
                // (VerboseRenderable) — wrapping/escaping here too double-escapes the outer tags, leaving
                // the literal "[dim]"/"[/]" text visible instead of being interpreted as markup.
                _console.Verbose($"{prefix}: {display}");
            }))
            .WithStandardErrorPipe(PipeTarget.ToDelegate(line =>
            {
                if (suppressErrors)
                {
                    _console.Verbose($"{prefix}: {line}");
                    return;
                }

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
