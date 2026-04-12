using CliWrap;
using Spectre.Console;

namespace Flowline.Utils;

public static class CommandExtensions
{
    public static Command WithExecutionLog(this Command command, bool verbose = true)
    {
        if (verbose)
        {
            AnsiConsole.MarkupLine($"[dim]Executing: [white italic]{Markup.Escape(command.ToString())}[/][/]");
        }
        return command;
    }

    public static Command WithToolExecutionLog(this Command command, bool verbose = true, StatusContext? ctx = null)
    {
        if (verbose)
        {
            AnsiConsole.MarkupLine($"[dim]Executing: [white italic]{Markup.Escape(command.ToString())}[/][/]");

            return command
                   .WithStandardOutputPipe(PipeTarget.ToDelegate(s =>
                   {
                       if (!string.IsNullOrWhiteSpace(s) && ctx is not null)
                       {
                           ctx.Status(s);
                           //ctx.Status(s.StartsWith("Processing asynchronous operation...") ? $"Cloning... {s}[/]" : s);
                       }

                       // Skip if the output is PAC async operation progress
                       if (!s.StartsWith("Processing asynchronous operation..."))
                       {
                           AnsiConsole.MarkupLineInterpolated($"[dim]{command.TargetFilePath}: {s}[/]");
                       }

                       // For PAC async operation errors, we want to output the error message explicitly
                       if (s.Contains("Error: ") || s.Contains("The reason given was: "))
                       {
                           AnsiConsole.MarkupLine($"[red]{s}[/]");
                       }
                   }))
                   .WithStandardErrorPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[red]{command.TargetFilePath}: {s}[/]")));
        }

        return command.WithStandardErrorPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[red]{command.TargetFilePath}: {s}[/]")));
    }
}
