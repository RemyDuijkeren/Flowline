using Flowline.Core;
using Spectre.Console;

namespace Flowline;

public class AnsiConsoleOutput : IFlowlineOutput
{
    public bool IsVerbose { get; set; }

    public void Info(string message)    => AnsiConsole.MarkupLine(message);
    public void Skip(string message)    => AnsiConsole.MarkupLine($"[dim]{message}[/]");
    public void Verbose(string message) { if (IsVerbose) AnsiConsole.MarkupLine($"[dim]{message}[/]"); }
    public void Warning(string message) => AnsiConsole.MarkupLine($"[yellow]Warning: {message}[/]");
    public void Error(string message)   => AnsiConsole.MarkupLine($"[red]Error: {message}[/]");
}
