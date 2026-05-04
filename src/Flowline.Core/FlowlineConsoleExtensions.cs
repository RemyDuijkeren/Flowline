using Spectre.Console;

namespace Flowline.Core;

internal static class FlowlineConsoleExtensions
{
    public static void Info(this IAnsiConsole console, string message) => console.MarkupLine(message);

    public static void Skip(this IAnsiConsole console, string message) => console.MarkupLine($"[dim]{message}[/]");

    public static void Verbose(this IAnsiConsole console, string message, FlowlineRuntimeOptions runtimeOptions)
    {
        if (runtimeOptions.IsVerbose)
            console.MarkupLine($"[dim]{message}[/]");
    }

    public static void Warning(this IAnsiConsole console, string message) => console.MarkupLine($"[yellow]Warning: {message}[/]");

    public static void Error(this IAnsiConsole console, string message) => console.MarkupLine($"[red]Error: {message}[/]");
}
