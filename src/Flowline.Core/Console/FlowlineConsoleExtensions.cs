using Flowline.Core.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Flowline.Core.Console;

public static class FlowlineConsoleExtensions
{
    public const string OkPrefix       = "✓ ";
    public const string DonePrefix     = "🚀";
    public const string InfoPrefix     = "· ";
    public const string SkipPrefix     = "↷ ";
    public const string WarningPrefix  = "! ";
    public const string ErrorPrefix    = "✗ ";
    public const string QuestionPrefix = "? ";

    public static void Ok(this IAnsiConsole console, string message) => console.MarkupLine($"[green]{OkPrefix}[/]{message}");
    public static void Done(this IAnsiConsole console, string message) => console.MarkupLine($"\n[bold green]{DonePrefix} {message}[/]");

    public static void Info(this IAnsiConsole console, string message) => console.MarkupLine($"{InfoPrefix}{message}");

    public static void Skip(this IAnsiConsole console, string message) => console.MarkupLine($"[dim]{SkipPrefix}{message}[/]");

    public static void Verbose(this IAnsiConsole console, string message) => console.Write(new VerboseRenderable(message));
    public static void Verbose(this IAnsiConsole console, IRenderable renderable) => console.Write(new VerboseRenderable(renderable));

    public static void Warning(this IAnsiConsole console, string message) => console.MarkupLine($"[yellow]{WarningPrefix}[/]{message}");

    public static void Error(this IAnsiConsole console, string message) => console.MarkupLine($"[red]{ErrorPrefix}[/]{message}");

    public static void Error(this IAnsiConsole console, Exception ex) => console.WriteException(ex);

    // Decorates prompt text handed to Spectre prompt objects (Title/constructor/Confirm) — not a
    // print-and-return-void helper like the others, since prompts consume a string rather than a line.
    public static string Question(string message) => $"[bold cyan]{QuestionPrefix}[/]{message}";

    // Shared force/interactive gate for confirmations, usable from both the CLI layer (which knows
    // about --force flags via FlowlineSettings) and Core call sites that can't reference it.
    public static bool ConfirmGated(this IAnsiConsole console, string message, bool defaultValue, bool force, string nonInteractiveMessage, Action? beforePrompt = null)
    {
        if (force)
        {
            console.Skip($"{message} (--force)");
            return true;
        }

        if (CiEnvironment.IsCi() || !console.Profile.Capabilities.Interactive)
            throw new FlowlineException(ExitCode.ForceRequired, nonInteractiveMessage);

        beforePrompt?.Invoke();
        return console.Confirm(Question(message), defaultValue);
    }
}
