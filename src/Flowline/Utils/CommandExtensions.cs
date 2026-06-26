using System.Text.RegularExpressions;
using CliWrap;
using Spectre.Console;

namespace Flowline.Utils;

public static class CommandExtensions
{
    public static Command WithExecutionLog(this Command command, bool verbose = true)
    {
        if (verbose)
        {
            AnsiConsole.MarkupLine($"[dim]Executing: [italic]{Markup.Escape(RedactSensitiveArgs(command.ToString()))}[/][/]");
        }
        return command;
    }

    public static Command WithToolExecutionLog(this Command command, bool verbose = true, StatusContext? ctx = null, Func<string, string>? lineTransform = null, string? toolDisplayName = null, SubprocessBuffer? buffer = null)
    {
        var prefix = toolDisplayName ?? command.TargetFilePath;
        if (verbose)
        {
            var cmdStr = command.ToString();
            var execLine = Markup.Escape(RedactSensitiveArgs(cmdStr));
            AnsiConsole.MarkupLine($"[dim]Executing: [italic]{execLine}[/][/]");

            return command
                   .WithStandardOutputPipe(PipeTarget.ToDelegate(s =>
                   {
                       SetStatusWithExecutionTime(ctx, s);

                       // Skip if the output is an error message
                       if (DisplayErrorMessage(s, prefix))
                       {
                           buffer?.Append(s);
                           return;
                       }

                       // Skip if the output is PAC async operation progress
                       if (s.StartsWith("Processing asynchronous operation...")) return;

                       // Skip if the output is empty
                       if (string.IsNullOrWhiteSpace(s)) return;

                       var display = lineTransform != null ? lineTransform(s) : s;
                       AnsiConsole.MarkupLine($"[dim]{Markup.Escape(prefix)}: {Markup.Escape(display)}[/]");
                   }))
                   .WithStandardErrorPipe(PipeTarget.ToDelegate(s =>
                   {
                       buffer?.Append(s);
                       AnsiConsole.MarkupLine($"[red]{Markup.Escape(prefix)}: {Markup.Escape(s)}[/]");
                   }));
        }

        if (buffer == null)
        {
            return command
                   .WithStandardOutputPipe(PipeTarget.ToDelegate(s =>
                   {
                       SetStatusWithExecutionTime(ctx, s);
                       DisplayErrorMessage(s, prefix);
                   }))
                   .WithStandardErrorPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLine($"[red]{Markup.Escape(prefix)}: {Markup.Escape(s)}[/]")));
        }

        return command
               .WithStandardOutputPipe(PipeTarget.ToDelegate(s =>
               {
                   SetStatusWithExecutionTime(ctx, s);
                   if (IsErrorLine(s)) buffer.Append(s);
               }))
               .WithStandardErrorPipe(PipeTarget.ToDelegate(s => buffer.Append(s)));
    }

    static readonly Regex s_sensitiveArgPattern =
        new(@"(?<dashFlag>--client-secret)\s+(?:""[^""]*""|\S+)|(?<colonFlag>/mfaClientSecret:)(?:""[^""]*""|\S+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    static string RedactSensitiveArgs(string cmdStr) =>
        s_sensitiveArgPattern.Replace(cmdStr, m =>
            m.Groups["dashFlag"].Success ? $"{m.Groups["dashFlag"].Value} ***" : $"{m.Groups["colonFlag"].Value}***");

    static void SetStatusWithExecutionTime(StatusContext? ctx, string s)
    {
        if (ctx is null || string.IsNullOrWhiteSpace(s) || !s.StartsWith("Processing asynchronous operation...")) return;

        // cut execution part from s (Processing asynchronous operation... execution time: 00:01:28 and 2.46% of max time allotted)
        var execution = s.Split("... ")[1];

        // append execution part to ctx.Status
        var indexOf = ctx.Status.IndexOf(" (", StringComparison.Ordinal);
        var status = (indexOf == -1) ? ctx.Status : ctx.Status[..indexOf];
        ctx.Status($"{status} ([italic]{execution}[/])");
    }

    static bool IsErrorLine(string s) =>
        s.Contains("Error: ") || s.Contains("The reason given was: ") || s.Contains(": error") || s.Contains(": warning");

    static bool DisplayErrorMessage(string s, string? targetFilePath = null)
    {
        // For PAC async operation errors, we want to output the error message explicitly
        if (s.Contains("Error: ") || s.Contains("The reason given was: "))
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(targetFilePath ?? string.Empty)}: {Markup.Escape(s)}[/]");
            return true;
        }

        // For dotnet errors, we want to output the error message explicitly
        if (s.Contains(": error"))
        {
            AnsiConsole.MarkupLine($"[red]{Markup.Escape(targetFilePath ?? string.Empty)}: {Markup.Escape(s)}[/]");
            return true;
        }
        if (s.Contains(": warning"))
        {
            AnsiConsole.MarkupLine($"[yellow]{Markup.Escape(targetFilePath ?? string.Empty)}: {Markup.Escape(s)}[/]");
            return true;
        }

        return false;
    }
}
