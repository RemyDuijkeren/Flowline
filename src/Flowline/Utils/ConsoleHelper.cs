using System.Reflection;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Flowline.Utils;

public static class ConsoleHelper
{
    public static void WelcomeScreen(IAnsiConsole console)
    {
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        var versionText = new Text($"Version {version}", new Style(Color.Turquoise2));

        console.MarkupLine("[turquoise2]____ _    ____ _ _ _ _    _ _  _ ____[/]");
        console.MarkupLine("[turquoise2]|___ |    |  | | | | |    | |\\ | |___[/]");
        console.MarkupLine("[turquoise2]|    |___ |__| |_|_| |___ | | \\| |___[/]");
        console.Write(versionText);
        console.WriteLine();
    }

    /// <summary>
    /// Detects if the current run is in interactive mode.
    /// Non-interactive mode is typically detected via Environment.UserInteractive,
    /// if the output is redirected, or if explicit CLI flags like --json are set.
    /// </summary>
    /// <param name="settings">The current Flowline settings which may contain flags like --json.</param>
    /// <returns>True if interactive, false otherwise.</returns>
    public static bool IsInteractive(FlowlineSettings? settings)
    {
        // Explicitly set non-interactive flags
        if (settings is { JsonOutput: true })
        {
            return false;
        }

        // CI Environment detection
        if (Environment.GetEnvironmentVariable("CI") != null ||
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != null ||
            Environment.GetEnvironmentVariable("TF_BUILD") != null || // Azure DevOps
            Environment.GetEnvironmentVariable("GITLAB_CI") != null ||
            Environment.GetEnvironmentVariable("JENKINS_URL") != null)
        {
            return false;
        }

        return AnsiConsole.Profile.Capabilities.Interactive;
    }

    /// <summary>
    /// Prompts the user with a confirmation, or automatically accepts if in non-interactive mode and --force is specified.
    /// </summary>
    public static bool Confirm(string prompt, bool defaultValue, FlowlineSettings? settings)
    {
        if (!IsInteractive(settings))
        {
            if (settings?.Force == true)
            {
                return true;
            }

            AnsiConsole.MarkupLine("[red]Confirmation required but not in interactive mode. Use --force to override.[/]");
            Environment.Exit(1);
            return false;
        }

        return AnsiConsole.Confirm(prompt, defaultValue);
    }
}
