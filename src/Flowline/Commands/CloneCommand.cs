using System.ComponentModel;
using CliWrap;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class CloneCommandSettings : BaseCommandSettings
{
    [CommandArgument(0, "<environment>")]
    [Description("The Power Platform environment to clone")]
    public string Environment { get; set; } = null!;

    [CommandOption("--postfix")]
    [Description("Postfix for the solution name and url for the target (default: Dev)")]
    [DefaultValue("Dev")]
    public string PostFix { get; set; } = "Dev";

    [CommandOption("--fullcopy")]
    [Description("FullCopy (with data) of environment to clone instead of a MinimalCopy (no data)")]
    [DefaultValue(false)]
    public bool FullCopy { get; set; } = false;
}

public class CloneCommand : AsyncCommand<CloneCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CloneCommandSettings settings)
    {
        await PacUtils.AssertPacCliInstalledAsync();

        AnsiConsole.MarkupLine($"Validating [bold]'{settings.Environment}'[/]...");

        var environments = await PacUtils.GetEnvironmentsAsync();
        var sourceEnv = environments.FirstOrDefault(e => e.EnvironmentUrl?.Contains(settings.Environment) == true);

        if (sourceEnv == null)
        {
            AnsiConsole.MarkupLine("[red]Source Environment not found.[/]");
            return 1;
        }

        if (sourceEnv.Type != "Production")
        {
            AnsiConsole.MarkupLine($"[red]Source environment type must be 'Production' to be cloned. Found type: '{sourceEnv.Type}'. Aborting.[/]");
            return 1;
        }

        var urlParts = PacUtils.GetPartsFromEnvUrl(sourceEnv.EnvironmentUrl!);

        var targetName = $"{sourceEnv.DisplayName} {settings.PostFix}";
        var targetEnvDomain = $"{urlParts.EnvDomain}-{settings.PostFix.ToLower()}";
        var targetUrl = $"https://{targetEnvDomain}.{urlParts.RegionDomain}/";

        var targetEnv = environments.FirstOrDefault(e => e.EnvironmentUrl == targetUrl);

        if (targetEnv != null)
        {
            AnsiConsole.MarkupLine($"Target Environment already exists: {targetEnv.EnvironmentUrl}");

            if (!AnsiConsole.Confirm("[yellow]Do you want to overwrite it?[/]", false))
            {
                AnsiConsole.MarkupLine($"[green]Alright, we keep as-is! See [link]{targetEnv.EnvironmentUrl}[/][/]");
                return 0;
            }

            AnsiConsole.MarkupLine("Overwriting existing environment...");
        }
        else
        {
            AnsiConsole.MarkupLine($"Creating environment {targetUrl}...");

            await Cli.Wrap("pac")
                     .WithArguments(args => args
                         .Add("admin")
                         .Add("create")
                         .Add("--name").Add($"{targetName} (cloning)")
                         .Add("--domain").Add(targetEnvDomain)
                         .Add("--region").Add(urlParts.Region))
                .ExecuteAsync();
        }

        environments = await PacUtils.GetEnvironmentsAsync();
        targetEnv = environments.FirstOrDefault(e => e.EnvironmentUrl == targetUrl);

        if (targetEnv == null)
        {
            AnsiConsole.MarkupLine("[red]Target Environment not found.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"Copy '{settings.Environment}' to '{targetEnv.EnvironmentUrl}'...");
        await Cli.Wrap("pac")
                 .WithArguments(args => args
                     .Add("admin")
                     .Add("copy")
                     .Add("--name").Add(targetName)
                     .Add("--source-env").Add(sourceEnv.EnvironmentUrl!)
                     .Add("--target-env").Add(targetEnv.EnvironmentUrl!)
                     .Add("--type").Add(settings.FullCopy ? "FullCopy" : "MinimalCopy"))
            .ExecuteAsync();

        AnsiConsole.MarkupLine($"[green]All done! See [link]{targetEnv.EnvironmentUrl}[/][/]");

        return 0;
    }
}
