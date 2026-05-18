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

    public static Command WithToolExecutionLog(this Command command, bool verbose = true, StatusContext? ctx = null)
    {
        if (verbose)
        {
            AnsiConsole.MarkupLine($"[dim]Executing: [italic]{Markup.Escape(command.ToString())}[/][/]");

            return command
                   .WithStandardOutputPipe(PipeTarget.ToDelegate(s =>
                   {
                       // if (!string.IsNullOrWhiteSpace(s) && ctx is not null
                       //     && s.StartsWith("Processing asynchronous operation..."))
                       // {
                       //     // cut execution part from s (Processing asynchronous operation... execution time: 00:01:28 and 2.46% of max time allotted)
                       //     var execution = s.Split("execution time:")[0];
                       //
                       //     // take ctx.Status until the (execution time: 00:01:28 and 2.46% of max time allotted)
                       //     var status = ctx.Status[..ctx.Status.IndexOf(" (", StringComparison.Ordinal)];
                       //
                       //     ctx.Status($"{status} ({execution})");
                       // }

                       // Skip if the output is an error message
                       if (DisplayErrorMessage(s, command.TargetFilePath)) return;

                       // Skip if the output is PAC async operation progress
                       if (s.StartsWith("Processing asynchronous operation...")) return;

                       // Skip if the output is empty
                       if (string.IsNullOrWhiteSpace(s)) return;

                       AnsiConsole.MarkupLineInterpolated($"[dim]{command.TargetFilePath}: {s}[/]");
                   }))
                   .WithStandardErrorPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[red]{command.TargetFilePath}: {s}[/]")));
        }

        return command
               .WithStandardOutputPipe(PipeTarget.ToDelegate(s => DisplayErrorMessage(s, command.TargetFilePath)))
               .WithStandardErrorPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[red]{command.TargetFilePath}: {s}[/]")));
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
