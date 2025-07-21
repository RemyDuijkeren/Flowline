using System.ComponentModel;
using Flowline.Config;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class EnvCommandSettings : BaseCommandSettings
{
    [CommandArgument(0, "[environment]")]
    [Description("The environment to set as active. If not specified, shows current environment settings.")]
    public string? Environment { get; set; }
}

public class EnvCommand : Command<EnvCommandSettings>
{
    public override int Execute(CommandContext context, EnvCommandSettings settings)
    {
        var config = ProjectConfig.Load();

        // If no environment is specified, show the current configuration
        if (string.IsNullOrEmpty(settings.Environment))
        {
            AnsiConsole.MarkupLine("Current environment configuration:");

            if (!string.IsNullOrEmpty(config.ProdEnvironment))
                AnsiConsole.MarkupLine($"  Production: [blue]{config.ProdEnvironment}[/]");
            else
                AnsiConsole.MarkupLine("  Production: [gray]Not configured[/]");

            if (!string.IsNullOrEmpty(config.DevEnvironment))
                AnsiConsole.MarkupLine($"  Development: [blue]{config.DevEnvironment}[/]");
            else
                AnsiConsole.MarkupLine("  Development: [gray]Not configured[/]");

            var activeEnv = config.GetCurrentEnvironment();
            if (!string.IsNullOrEmpty(activeEnv))
            {
                var isProd = activeEnv == config.ProdEnvironment;
                AnsiConsole.MarkupLine($"  [green]Active: {activeEnv} ({(isProd ? "Production" : "Development")})[/]");
            }
            else
            {
                AnsiConsole.MarkupLine("  [yellow]No active environment configured[/]");
            }

            return 0;
        }

        // Try to match environment with existing configurations
        if (config.ProdEnvironment.Contains(settings.Environment))
        {
            config.ActiveEnvironment = config.ProdEnvironment;
            config.Save();
            AnsiConsole.MarkupLine($"[green]Active environment set to Production: {config.ProdEnvironment}[/]");
            return 0;
        }

        if (config.DevEnvironment.Contains(settings.Environment))
        {
            config.ActiveEnvironment = config.DevEnvironment;
            config.Save();
            AnsiConsole.MarkupLine($"[green]Active environment set to Development: {config.DevEnvironment}[/]");
            return 0;
        }

        // If not found, check if it's a valid environment and set it as active
        AnsiConsole.MarkupLine("[yellow]Warning: The specified environment does not match any configured environments.[/]");
        if (AnsiConsole.Confirm("Do you want to set it as the active environment anyway?"))
        {
            config.ActiveEnvironment = settings.Environment;
            config.Save();
            AnsiConsole.MarkupLine($"[green]Active environment set to: {settings.Environment}[/]");
            AnsiConsole.MarkupLine("[dim]Note: This environment is not identified as either Production or Development.[/]");
            return 0;
        }

        return 1;
    }
}
