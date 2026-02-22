using System.ComponentModel;
using System.Reflection;
using Flowline.Config;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class StatusCommand : AsyncCommand<StatusCommand.Settings>
{
    public sealed class Settings : FlowlineSettings
    {
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine(
            $"[bold]Flowline[/] version: [green]{Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version}[/]");

        try
        {
            var pacVersion = await PacUtils.AssertPacCliInstalledAsync(cancellationToken);
            AnsiConsole.MarkupLine($"[bold]Power Platform CLI[/] version: [green]{pacVersion}[/]");

            var gitVersion = await GitUtils.AssertGitInstalledAsync(cancellationToken);
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
        AnsiConsole.MarkupLine("\n[bold]Current environment configuration:[/]");

        if (config is not null)
        {
            if (!string.IsNullOrEmpty(config.ProductionEnvironment))
                AnsiConsole.MarkupLine($"  Production: [blue]{config.ProductionEnvironment}[/]");
            else
                AnsiConsole.MarkupLine("  Production: [gray]Not configured[/]");

            if (!string.IsNullOrEmpty(config.StagingEnvironment))
                AnsiConsole.MarkupLine($"  Staging: [blue]{config.StagingEnvironment}[/]");
            else
                AnsiConsole.MarkupLine("  Staging: [gray]Not configured[/]");

            if (!string.IsNullOrEmpty(config.DevelopmentEnvironment))
                AnsiConsole.MarkupLine($"  Development: [blue]{config.DevelopmentEnvironment}[/]");
            else
                AnsiConsole.MarkupLine("  Development: [gray]Not configured[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("  [yellow]No project config foud in path[/]");
        }

        return 0;
    }
}
