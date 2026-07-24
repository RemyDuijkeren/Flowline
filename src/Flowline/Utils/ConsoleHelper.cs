using System.Reflection;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Services;
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

    public static bool IsInteractive(FlowlineSettings? settings) =>
        !CiEnvironment.IsCi() && AnsiConsole.Profile.Capabilities.Interactive;

    internal static string? DetectCIPlatform()
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != null) return "github";
        if (Environment.GetEnvironmentVariable("TF_BUILD") != null) return "azuredevops";
        if (Environment.GetEnvironmentVariable("JENKINS_URL") != null) return "jenkins";
        if (Environment.GetEnvironmentVariable("CI") != null) return "unknown";
        return null;
    }

    /// <summary>
    /// Prompts the user with a confirmation, or automatically accepts if --force &lt;specifier&gt; (or --force all) is
    /// specified. In non-interactive mode without --force, throws instead of prompting.
    /// </summary>
    public static bool Confirm(string prompt, bool defaultValue, FlowlineSettings? settings, string specifier) =>
        AnsiConsole.Console.ConfirmGated(prompt, defaultValue, settings?.HasForce(specifier) == true,
            $"Confirmation required but not in interactive mode. Use --force {specifier} to proceed.");
}
