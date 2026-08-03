using Flowline.Core.Console;
using Flowline.Utils;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Flowline.Infrastructure;

/// <summary>
/// The <see cref="FlowlineSettings"/>-aware half of Flowline's console helpers — everything that needs
/// to read <c>--force</c> off the parsed settings.
/// </summary>
/// <remarks>
/// Separate from <see cref="FlowlineConsoleExtensions"/> by necessity, not style: that one lives in
/// <c>Flowline.Core</c>, which must never reference <c>Flowline</c>, and <see cref="FlowlineSettings"/>
/// is defined here. Don't merge them — 18 Core files depend on the Core half.
/// </remarks>
public static class SettingsConsoleExtensions
{
    public static void WriteWelcomeScreen(this IAnsiConsole console)
    {
        console.Write(new Text(FlowlineTheme.TextLogo, new Style(FlowlineTheme.PrimaryColor)));
        console.WriteLine();

        var version = FlowlineVersion.Display;
        var versionText = new Text(
            $"Flowline CLI v{version} ({Environment.OSVersion}, CLR:{Environment.Version}, {(Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit")})",
            new Style(FlowlineTheme.PrimaryColor));

        console.Write(versionText);
        console.WriteLine();
    }

    /// <summary>
    /// Prompts the user with a confirmation, or automatically accepts if --force &lt;specifier&gt; (or --force all) is
    /// specified. In non-interactive mode without --force, throws instead of prompting.
    /// </summary>
    public static bool Confirm(this IAnsiConsole console, string prompt, bool defaultValue, FlowlineSettings? settings, string specifier) =>
        console.ConfirmGated(prompt, defaultValue, settings?.HasForce(specifier) == true,
            $"Confirmation required but not in interactive mode. Use --force {specifier} to proceed.");

    /// <inheritdoc cref="Confirm"/>
    public static Task<bool> ConfirmAsync(this IAnsiConsole console, string prompt, bool defaultValue, FlowlineSettings? settings, string specifier, CancellationToken cancellationToken) =>
        console.ConfirmGatedAsync(prompt, defaultValue, settings?.HasForce(specifier) == true,
            $"Confirmation required but not in interactive mode. Use --force {specifier} to proceed.", cancellationToken);
}
