using System.ComponentModel;
using System.Reflection;
using Flowline.Config;
using Flowline.Utils;
using Flowline.Validation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class StatusCommand(IAnsiConsole console) : AsyncCommand<StatusCommand.Settings>
{
    private readonly IAnsiConsole Console = console;

    public sealed class Settings : FlowlineSettings
    {
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        WelcomeScreen();

        // Console.MarkupLine(
        //     $"[bold]Flowline[/] version: [green]{Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version}[/]");

        try
        {
            var dotNet = await FlowlineValidator.Default.EnsureDotNetAsync(settings, cancellationToken);
            Console.MarkupLine($"[bold].NET SDK[/] version: [green]{dotNet.Version}[/]");

            var pac = await FlowlineValidator.Default.EnsurePacCliAsync(settings, cancellationToken);
            Console.MarkupLine($"[bold]Power Platform CLI[/] version: [green]{pac.Version}[/] ({pac.InstallType})");

            var git = await FlowlineValidator.Default.EnsureGitAsync(settings, cancellationToken);
            Console.MarkupLine($"[bold]Git[/] version: [green]{git.Version}[/]");

            if (settings.Verbose)
            {
                Console.MarkupLine("\n[bold]Environment Information:[/]");
                Console.MarkupLine($"Operating System: [green]{Environment.OSVersion}[/]");
                Console.MarkupLine($".NET Runtime: [green]{Environment.Version}[/]");
                Console.MarkupLine($"64-bit OS: [green]{Environment.Is64BitOperatingSystem}[/]");
            }
        }
        catch
        {
            // PAC CLI and Git checks will exit the application if not found
        }

        // Show the current configuration
        var config = ProjectConfig.Load();
        Console.MarkupLine("\n[bold]Configuration[/]");

        if (config is not null)
        {
            if (!string.IsNullOrEmpty(config.ProdUrl))
                Console.MarkupLine($"  Production: [blue]{config.ProdUrl}[/]");
            else
                Console.MarkupLine("  Production: [gray]Not configured[/]");

            if (!string.IsNullOrEmpty(config.TestUrl))
                Console.MarkupLine($"  Test: [blue]{config.TestUrl}[/]");
            else
                Console.MarkupLine("  Test: [gray]Not configured[/]");

            if (!string.IsNullOrEmpty(config.DevUrl))
                Console.MarkupLine($"  Development: [blue]{config.DevUrl}[/]");
            else
                Console.MarkupLine("  Development: [gray]Not configured[/]");
        }
        else
        {
            Console.MarkupLine("  [yellow]No .flowline config found[/]");
        }

        return 0;
    }

    void WelcomeScreen()
    {
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        var versionText = new Text($"Version {version}", new Style(Color.Turquoise2));

        Console.MarkupLine("[turquoise2]____ _    ____ _ _ _ _    _ _  _ ____[/]");
        Console.MarkupLine("[turquoise2]|___ |    |  | | | | |    | |\\ | |___[/]");
        Console.MarkupLine("[turquoise2]|    |___ |__| |_|_| |___ | | \\| |___[/]");
        Console.Write(versionText);
        Console.WriteLine();
    }
}
