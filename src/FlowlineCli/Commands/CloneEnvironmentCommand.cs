using System.ComponentModel;
using CliWrap;
using Spectre.Console;
using Spectre.Console.Cli;

namespace FlowLineCli.Commands;

public class CloneCommandSettings : FlowlineCommandSettings
{
    [CommandOption("-r|--repo")]
    [Description("Git repository URL")]
    [DefaultValue("https://github.com/AutomateValue/Dataverse01.git")]
    public string GitRemoteUrl { get; set; } = "https://github.com/AutomateValue/Dataverse01.git";
}

public class CloneCommand : AsyncCommand<CloneCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CloneCommandSettings settings)
    {
        await PacUtils.AssertPacCliInstalledAsync();

        AnsiConsole.MarkupLine($"Validating [green]'{settings.Environment}'[/]...");

        var environments = await PacUtils.GetEnvironmentsAsync();
        var sourceEnv = environments.FirstOrDefault(e => e.EnvironmentUrl?.Contains(settings.Environment) == true);

        if (sourceEnv == null)
        {
            AnsiConsole.MarkupLine("[red]Source Environment not found.[/]");
            return 1;
        }

        if (sourceEnv.Type != "Production")
        {
            AnsiConsole.MarkupLine($"[red]Source environment type must be 'Production'. Found: '{sourceEnv.Type}'. Aborting.[/]");
            return 1;
        }

        var urlParts = PacUtils.GetPartsFromEnvUrl(sourceEnv.EnvironmentUrl!);

        var targetName = $"{sourceEnv.DisplayName} Dev";
        var targetEnvDomain = $"{urlParts.EnvDomain}-dev";
        var targetUrl = $"https://{targetEnvDomain}.{urlParts.RegionDomain}/";

        var targetEnv = environments.FirstOrDefault(e => e.EnvironmentUrl == targetUrl);

        if (targetEnv != null)
        {
            AnsiConsole.MarkupLine($"Target Environment already exists: {targetEnv.EnvironmentUrl}");

            if (!AnsiConsole.Confirm("Do you want to overwrite it?", false))
            {
                AnsiConsole.MarkupLine("Aborting operation.");
                return 0;
            }

            AnsiConsole.MarkupLine("Overwriting existing environment...");
        }
        else
        {
            AnsiConsole.MarkupLine($"Creating environment {targetUrl}...");

            // await Cli.Wrap("pac")
            //     .WithArguments($"admin create --name \"{targetName} (cloning)\" --domain {targetEnvDomain} --region {urlParts.Region} --type Sandbox")
            //     .ExecuteAsync();
        }

        environments = await PacUtils.GetEnvironmentsAsync();
        targetEnv = environments.FirstOrDefault(e => e.EnvironmentUrl == targetUrl);

        if (targetEnv == null)
        {
            AnsiConsole.MarkupLine("[red]Target Environment not found.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"Copy '{settings.Environment}' to '{targetEnv.EnvironmentUrl}'...");
        // Uncomment when ready to execute
        // await Cli.Wrap("pac")
        //     .WithArguments($"admin copy --name \"{targetName}\" --source-env \"{sourceEnv.EnvironmentUrl}\" --target-env \"{targetEnv.EnvironmentUrl}\" --type FullCopy")
        //     .ExecuteAsync();

        AnsiConsole.MarkupLine("[green]All done![/]");

        return 0;
    }
}
