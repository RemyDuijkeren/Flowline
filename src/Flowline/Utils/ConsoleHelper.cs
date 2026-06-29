using System.Reflection;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Flowline.Utils;

public static class ConsoleHelper
{
    internal static readonly Color s_welcomeColor = Color.Turquoise2; //Turquoise2, Plum4, DarkMagenta, DarkMagenta_1
    public static void WelcomeScreen(IAnsiConsole console)
    {
        // Future Smooth
        var welcomeText = new Text(
            """
            ╭─╴╷  ╭─╮╷ ╷╷  ╷╭╮╷╭─╴
            ├╴ │  │ ││╷││  ││╰┤├╴
            ╵  ╰─╴╰─╯╰┴╯╰─╴╵╵ ╵╰─╴
            """, new Style(s_welcomeColor));

        console.Write(welcomeText);
        console.WriteLine();

        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        var versionText = new Text($"Flowline CLI v{version} ({Environment.OSVersion}, CLR:{Environment.Version}, {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})", new Style(s_welcomeColor));

        console.Write(versionText);
        console.WriteLine();
    }

    public static bool IsInteractive(FlowlineSettings? settings)
    {
        // CI Environment detection
        if (Environment.GetEnvironmentVariable("CI") != null || // Most CI systems
            Environment.GetEnvironmentVariable("TF_BUILD") != null || // Azure DevOps
            Environment.GetEnvironmentVariable("JENKINS_URL") != null || // Jenkins
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != null) // GitHub Actions
        {
            return false;
        }

        return AnsiConsole.Profile.Capabilities.Interactive;
    }

    internal static string? DetectCIPlatform()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != null) return "github";
        if (Environment.GetEnvironmentVariable("TF_BUILD") != null) return "azuredevops";
        if (Environment.GetEnvironmentVariable("JENKINS_URL") != null) return "jenkins";
        if (Environment.GetEnvironmentVariable("CI") != null) return "unknown";
        return null;
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

            throw new FlowlineException(ExitCode.ForceRequired, "Confirmation required but not in interactive mode. Use --force to proceed.");
        }

        return AnsiConsole.Confirm(prompt, defaultValue);
    }
}
