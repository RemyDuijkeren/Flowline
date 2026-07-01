using Spectre.Console;

namespace Flowline.Core;

public static class FlowlineConsoleExtensions
{
    public const string OkPrefix      = "✓ ";
    public const string DonePrefix    = "🚀";
    public const string InfoPrefix    = "· ";
    public const string SkipPrefix    = "↷ ";
    public const string WarningPrefix = "Warning: ";
    public const string ErrorPrefix   = "Error: ";

    public static void Ok(this IAnsiConsole console, string message) => console.MarkupLine($"[green]{OkPrefix}[/]{message}");
    public static void Done(this IAnsiConsole console, string message) => console.MarkupLine($"\n[bold green]{DonePrefix} {message}[/]");

    public static void Info(this IAnsiConsole console, string message) => console.MarkupLine($"{InfoPrefix}{message}");

    public static void Skip(this IAnsiConsole console, string message) => console.MarkupLine($"[dim]{SkipPrefix}{message}[/]");

    public static void Verbose(this IAnsiConsole console, string message) => console.Write(new VerboseMarkup(message));

    public static void Warning(this IAnsiConsole console, string message) => console.MarkupLine($"[yellow]{WarningPrefix}[/]{message}");

    public static void Error(this IAnsiConsole console, string message) => console.MarkupLine($"[red]{ErrorPrefix}[/]{message}");

    public static void Error(this IAnsiConsole console, Exception ex) => console.WriteException(ex);
}
