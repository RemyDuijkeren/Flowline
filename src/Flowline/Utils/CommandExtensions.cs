using CliWrap;
using Spectre.Console;

namespace Flowline.Utils;

public static class CommandExtensions
{
    public static Command WithExecutionLog(this Command command, bool verbose = true)
    {
        if (verbose)
        {
            AnsiConsole.MarkupLine($"[dim]Executing: [italic]{Markup.Escape(command.ToString())}[/][/]");
        }
        return command;
    }

    public static Command WithToolExecutionLog(this Command command, bool verbose = true, StatusContext? ctx = null, Func<string, string>? lineTransform = null, string? toolDisplayName = null)
    {
        var prefix = toolDisplayName ?? command.TargetFilePath;
        if (verbose)
        {
            AnsiConsole.MarkupLine($"[dim]Executing: [italic]{Markup.Escape(prefix.ToString())}[/][/]");

            return command
                   .WithStandardOutputPipe(PipeTarget.ToDelegate(s =>
                   {
                       SetStatusWithExecutionTime(ctx, s);

                       // Skip if the output is an error message
                       if (DisplayErrorMessage(s, prefix)) return;

                       // Skip if the output is PAC async operation progress
                       if (s.StartsWith("Processing asynchronous operation...")) return;

                       // Skip if the output is empty
                       if (string.IsNullOrWhiteSpace(s)) return;

                       var display = lineTransform != null ? lineTransform(s) : s;
                       AnsiConsole.MarkupLineInterpolated($"[dim]{prefix}: {display}[/]");
                   }))
                   .WithStandardErrorPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[red]{prefix}: {s}[/]")));
        }

        return command
               .WithStandardOutputPipe(PipeTarget.ToDelegate(s =>
               {
                   SetStatusWithExecutionTime(ctx, s);
                   DisplayErrorMessage(s, prefix);
               }))
               .WithStandardErrorPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[red]{prefix}: {s}[/]")));
    }

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

    static bool DisplayErrorMessage(string s, string? targetFilePath = null)
    {
        // For PAC async operation errors, we want to output the error message explicitly
        if (s.Contains("Error: ") || s.Contains("The reason given was: "))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{targetFilePath}: {s}[/]");
            return true;
        }

        // For dotnet errors, we want to output the error message explicitly
        if (s.Contains(": error"))
        {
            AnsiConsole.MarkupLineInterpolated($"[red]{targetFilePath}: {s}[/]");
            return true;
        }
        if (s.Contains(": warning"))
        {
            AnsiConsole.MarkupLineInterpolated($"[yellow]{targetFilePath}: {s}[/]");
            return true;
        }

        return false;
    }
}
