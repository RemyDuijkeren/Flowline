using Spectre.Console;

namespace Flowline.Core;

public static class FlowlineConsoleExtensions
{
    public static void Ok(this IAnsiConsole console, string message) => console.MarkupLine($"[green]✓[/] {message}");
    public static void Done(this IAnsiConsole console, string message) => console.MarkupLine($"\n[bold green]:rocket: {message}[/]");

    public static void Info(this IAnsiConsole console, string message) => console.MarkupLine(message);

    public static void Skip(this IAnsiConsole console, string message) => console.MarkupLine($"[dim]{message}[/]");

    public static void Verbose(this IAnsiConsole console, string message, bool isVerbose)
    {
        if (isVerbose)
            console.MarkupLine($"[dim]{message}[/]");
    }

    public static void Verbose(this IAnsiConsole console, string message, FlowlineRuntimeOptions options)
    {
        if (options.IsVerbose)
            console.MarkupLine($"[dim]{Markup.Escape(message)}[/]");
        else
            options.VerboseOutput.Append(message);
    }

    public static void Warning(this IAnsiConsole console, string message) => console.MarkupLine($"[yellow]Warning:[/] {message}");

    public static void Error(this IAnsiConsole console, string message) => console.MarkupLine($"[red]Error:[/] {message}");

    public static void Error(this IAnsiConsole console, Exception ex) => console.WriteException(ex);
}
