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

    public static Command WithToolExecutionLog(this Command command, bool verbose = true)
    {
        if (verbose)
        {
            AnsiConsole.MarkupLine($"[dim]Executing: [white italic]{Markup.Escape(command.ToString())}[/][/]");

            return command.WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim][underline]{command.TargetFilePath}[/]: {s}[/]")))
                          .WithStandardErrorPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[red]{command.TargetFilePath}: {s}[/]")));


        }

        return command.WithStandardErrorPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[red]{command.TargetFilePath}: {s}[/]")));
    }
}
