using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class BranchEnvCommandSettings : BaseCommandSettings
{
    [CommandArgument(0, "<environment>")]
    [Description("The Power Platform environment to branch")]
    public string Environment { get; set; } = null!;

    [CommandOption("--postfix")]
    [Description("Postfix for the environment display name and url for the target (default: Dev)")]
    [DefaultValue("Dev")]
    public string PostFix { get; set; } = "Dev";

    [CommandOption("--fullcopy")]
    [Description("FullCopy (with data) of environment to branch instead of a MinimalCopy (no data)")]
    [DefaultValue(false)]
    public bool FullCopy { get; set; } = false;
}

public class BranchEnvCommand : AsyncCommand<BranchEnvCommandSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, BranchEnvCommandSettings settings)
    {
        await GitUtils.AssertGitInstalledAsync();
        await PacUtils.AssertPacCliInstalledAsync();

        AnsiConsole.MarkupLine($"Validating [bold]'{settings.Environment}'[/]...");

        var sourceEnv = await PacUtils.GetEnvironmentByUrlAsync(settings.Environment);
        if (sourceEnv == null)
        {
            AnsiConsole.MarkupLine("[red]Source Environment not found.[/]");
            return 1;
        }

        if (sourceEnv.Type != "Production")
        {
            AnsiConsole.MarkupLine($"[red]Source environment type must be 'Production' to be copied. Found type: '{sourceEnv.Type}'. Aborting.[/]");
            return 1;
        }

        var urlParts = PacUtils.GetPartsFromEnvUrl(sourceEnv.EnvironmentUrl!);

        var targetName = $"{sourceEnv.DisplayName} {settings.PostFix}";
        var targetEnvDomain = $"{urlParts.EnvDomain}-{settings.PostFix.ToLower()}";
        var targetUrl = $"https://{targetEnvDomain}.{urlParts.RegionDomain}/";
        var targetEnv = await PacUtils.GetEnvironmentByUrlAsync(targetUrl);

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
                     .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]PAC: {s}[/]")))
                     .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                     .ExecuteAsync();

            targetEnv = await PacUtils.GetEnvironmentByUrlAsync(targetUrl);
            if (targetEnv == null)
            {
                AnsiConsole.MarkupLine("[red]Target Environment not found after creating.[/]");
                return 1;
            }
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
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]PAC: {s}[/]")))
                 .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                 .ExecuteAsync();

        // Save both source (production) and target (development) environments to configuration
        var config = ProjectConfig.Load();
        config.ProductionEnvironment = sourceEnv.EnvironmentUrl!;
        config.SandboxEnvironment = targetEnv.EnvironmentUrl!;
        config.BranchEnvironment = targetEnv.EnvironmentUrl!; // Set development as active by default
        config.Save();

        AnsiConsole.MarkupLine($"[green]All done! See [link]{targetEnv.EnvironmentUrl}[/][/]");
        AnsiConsole.MarkupLine("[dim]Project configuration saved with target environment. You can now run 'sync' command.[/]");

        return 0;
    }
}
