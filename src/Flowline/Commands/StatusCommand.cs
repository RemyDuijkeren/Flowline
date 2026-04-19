using System.ComponentModel;
using System.Reflection;
using Flowline.Config;
using Flowline.Utils;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class StatusCommand : AsyncCommand<StatusCommand.Settings>
{
    public sealed class Settings : FlowlineSettings
    {
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine(
            $"[bold]Flowline[/] version: [green]{Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version}[/]");

        try
        {
            var dotNetVersion = await DotNetUtils.AssertDotNetInstalledAsync(false, cancellationToken);
            AnsiConsole.MarkupLine($"[bold].NET SDK[/] version: [green]{dotNetVersion}[/]");

            var (pacVersion, pacInstallType) = await PacUtils.AssertPacCliInstalledAsync(false, cancellationToken);
            AnsiConsole.MarkupLine($"[bold]Power Platform CLI[/] version: [green]{pacVersion}[/] ({pacInstallType})");

            var gitVersion = await GitUtils.AssertGitInstalledAsync(false, cancellationToken);
            AnsiConsole.MarkupLine($"[bold]Git[/] version: [green]{gitVersion}[/]");

            if (settings.Verbose)
            {
                AnsiConsole.MarkupLine("\n[bold]Environment Information:[/]");
                AnsiConsole.MarkupLine($"Operating System: [green]{Environment.OSVersion}[/]");
                AnsiConsole.MarkupLine($".NET Runtime: [green]{Environment.Version}[/]");
                AnsiConsole.MarkupLine($"64-bit OS: [green]{Environment.Is64BitOperatingSystem}[/]");
            }
        }
        catch
        {
            // PAC CLI and Git checks will exit the application if not found
        }

        // Show the current configuration
        var config = ProjectConfig.Load();
        AnsiConsole.MarkupLine("\n[bold]Configuration[/]");

        if (config is not null)
        {
            if (!string.IsNullOrEmpty(config.ProdUrl))
                AnsiConsole.MarkupLine($"  Production: [blue]{config.ProdUrl}[/]");
            else
                AnsiConsole.MarkupLine("  Production: [gray]Not configured[/]");

            if (!string.IsNullOrEmpty(config.StagingUrl))
                AnsiConsole.MarkupLine($"  Staging: [blue]{config.StagingUrl}[/]");
            else
                AnsiConsole.MarkupLine("  Staging: [gray]Not configured[/]");

            if (!string.IsNullOrEmpty(config.DevUrl))
                AnsiConsole.MarkupLine($"  Development: [blue]{config.DevUrl}[/]");
            else
                AnsiConsole.MarkupLine("  Development: [gray]Not configured[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("  [yellow]No .flowline config found[/]");
        }

        return 0;
    }
}
