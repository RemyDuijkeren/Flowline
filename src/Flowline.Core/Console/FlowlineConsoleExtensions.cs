using Flowline.Core.Services;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Flowline.Core.Console;

public static class FlowlineConsoleExtensions
{
    public static void Ok(this IAnsiConsole console, string message) => console.MarkupLine($"[green]{FlowlineTheme.OkPrefix}[/] {message}");

    public static void Done(this IAnsiConsole console, string message) => console.MarkupLine($"\n[bold green]{FlowlineTheme.DonePrefix} {message}[/]");

    public static void CannotContinue(this IAnsiConsole console, string message, string nextStep)
    {
        console.MarkupLine($"{Environment.NewLine}[bold yellow]{FlowlineTheme.StopPrefix} {Markup.Escape(message)}[/]");
        console.MarkupLine($"[dim]Next:[/] {Markup.Escape(nextStep)}");
    }

    public static void Info(this IAnsiConsole console, string message) => console.MarkupLine($"{FlowlineTheme.InfoPrefix} {message}");

    public static void Skip(this IAnsiConsole console, string message) => console.MarkupLine($"[dim]{FlowlineTheme.SkipPrefix} {message}[/]");

    public static void Verbose(this IAnsiConsole console, string message) => console.Write(new VerboseRenderable(message));

    public static void Verbose(this IAnsiConsole console, IRenderable renderable) => console.Write(new VerboseRenderable(renderable));

    public static void Warning(this IAnsiConsole console, string message) => console.MarkupLine($"[yellow]{FlowlineTheme.WarningPrefix} {message}[/]");

    public static void Error(this IAnsiConsole console, string message) => console.MarkupLine($"[red]{FlowlineTheme.ErrorPrefix}[/] {message}");

    public static void Error(this IAnsiConsole console, Exception ex) => console.WriteException(ex);

    // Decorates prompt text handed to Spectre prompt objects (Title/constructor/Confirm) — not a
    // print-and-return-void helper like the others, since prompts consume a string rather than a line.
    public static string Question(string message) => $"[bold italic cyan]{FlowlineTheme.QuestionPrefix} {message}[/]";

    // Shared force/interactive gate for confirmations, usable from both the CLI layer (which knows
    // about --force flags via FlowlineSettings) and Core call sites that can't reference it.
    public static bool ConfirmGated(this IAnsiConsole console, string message, bool defaultValue, bool force, string nonInteractiveMessage, Action? beforePrompt = null)
    {
        if (force)
        {
            console.Skip($"{message} (--force)");
            return true;
        }

        if (!console.Profile.Capabilities.Interactive)
            throw new FlowlineException(ExitCode.ForceRequired, nonInteractiveMessage);

        beforePrompt?.Invoke();
        return console.Confirm(Question(message), defaultValue);
    }

    /// <summary>Cancellable sibling of <see cref="ConfirmGated"/> — observes the token via
    /// PromptAsync so Ctrl+C unwinds the confirmation instead of blocking on it.</summary>
    public static async Task<bool> ConfirmGatedAsync(this IAnsiConsole console, string message, bool defaultValue, bool force, string nonInteractiveMessage, CancellationToken cancellationToken, Action? beforePrompt = null)
    {
        if (force)
        {
            console.Skip($"{message} (--force)");
            return true;
        }

        if (!console.Profile.Capabilities.Interactive)
            throw new FlowlineException(ExitCode.ForceRequired, nonInteractiveMessage);

        beforePrompt?.Invoke();
        return await console.PromptAsync(new ConfirmationPrompt(Question(message)) { DefaultValue = defaultValue }, cancellationToken);
    }
}
