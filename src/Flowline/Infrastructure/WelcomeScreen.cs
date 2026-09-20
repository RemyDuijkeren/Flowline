using Flowline.Core.Console;
using Flowline.Utils;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Flowline.Infrastructure;

/// <summary>
/// The welcome banner every interactive command prints before its own output.
/// </summary>
/// <remarks>
/// Presentation, not engine, so it stays in <c>Flowline</c> rather than moving to
/// <c>Flowline.Core</c> with the confirmation helpers it used to sit beside. U8 gives it a
/// role-named home; it is parked here until that folder exists.
/// </remarks>
public static class WelcomeScreen
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
}
