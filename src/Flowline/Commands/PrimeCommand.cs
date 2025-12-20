using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public enum Role { Dev, Staging }

public class PrimeCommand : AsyncCommand<PrimeCommand.Settings>
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "[role]")]
        [Description("Environment role (dev|staging) (default: dev)")]
        public Role Role { get; set; } = Role.Dev; // dev|staging

        [CommandOption("--suffix <suffix>")]
        [Description("Postfix for the environment display name and url for the target (default: Dev)")]
        [DefaultValue("Dev")]
        public string? Suffix { get; set; }

        [CommandOption("--fullcopy")]
        [Description("FullCopy (with data) of environment to branch instead of a MinimalCopy (no data) (default: false, staging is always a FullCopy)")]
        public bool? FullCopy { get; set; }

        [CommandOption("-e|--environment <target-url>")]
        [Description("explicit override for the target environment url (default: Dev or Staging suffix)")]
        public string? Environment { get; set; }

        [CommandOption("-s|--source <source-url>")]
        [Description("the source environment url (default: Production environment url from config)")]
        public string? Source { get; set; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        await GitUtils.AssertGitInstalledAsync();
        await PacUtils.AssertPacCliInstalledAsync();

        AnsiConsole.MarkupLine($"Validating [bold]'{settings.Role}'[/]...");

        var config = ProjectConfig.Load();

        // prepare/check the source environment
        var sourceUrl = settings.Source ?? config?.ProductionEnvironment;
        if (string.IsNullOrEmpty(sourceUrl))
        {
            AnsiConsole.MarkupLine("[red]No source environment configured. Please run 'init' first or provide a source environment using -s <source-url> or --source <source-url>.[/]");
            return 1;
        }

        var sourceEnv = await PacUtils.GetEnvironmentInfoByUrlAsync(sourceUrl);
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

        // prepare/check the target environment
        var suffix = settings.Suffix ?? (settings.Role == Role.Dev ? "Dev" : "Staging");
        var targetName = $"{sourceEnv.DisplayName} {suffix}";
        EnvironmentUrlParts urlParts = PacUtils.GetPartsFromEnvUrl(sourceEnv.EnvironmentUrl!);
        var targetUrl = settings.Environment ?? $"https://{urlParts.Organization}-{suffix.ToLower()}.{urlParts.Host}/";

        // TODO: verify if the target environment url is given, is in the same region. Is this needed?
        // if <org> already ends with your suffix, don’t duplicate.
        // If your prod org is named contoso-prod, add a config “swap map” so -prod → -dev/-stg instead of appending.

        if (targetUrl == sourceUrl)
        {
            AnsiConsole.MarkupLine("[red]Target environment url must be different from source environment url.[/]");
            return 1;
        }

        var targetEnv = await PacUtils.GetEnvironmentInfoByUrlAsync(targetUrl);
        if (targetEnv != null)
        {
            AnsiConsole.MarkupLine($"Target Environment already exists: {targetEnv.EnvironmentUrl}");
            if (targetEnv.Type == "Production")
            {
                AnsiConsole.MarkupLine("[red]Cannot overwrite production environment.[/]");
                return 1;
            }

            if (!AnsiConsole.Confirm("[yellow]Do you want to overwrite it?[/]", false))
            {
                AnsiConsole.MarkupLine($"[green]Alright, we keep as-is! See [link]{targetEnv.EnvironmentUrl}[/][/]");
                SaveConfig(config, sourceEnv, targetEnv);
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
                                            .Add("--domain").Add($"{urlParts.Organization}-{suffix.ToLower()}")
                                            .Add("--region").Add(urlParts.Region))
                     .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]PAC: {s}[/]")))
                     .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                     .ExecuteAsync();

            targetEnv = await PacUtils.GetEnvironmentInfoByUrlAsync(targetUrl);
            if (targetEnv == null)
            {
                AnsiConsole.MarkupLine("[red]Target Environment not found after creating.[/]");
                return 1;
            }
        }

        // Staging is always a FullCopy
        string copyType = (settings.Role == Role.Staging || settings.FullCopy is null or true) ? "FullCopy" : "MinimalCopy";

        AnsiConsole.MarkupLine($"Copy '{sourceUrl}' to '{targetUrl}'...");
        await Cli.Wrap("pac")
                 .WithArguments(args => args
                                        .Add("admin")
                                        .Add("copy")
                                        .Add("--name").Add(targetName)
                                        .Add("--source-env").Add(sourceEnv.EnvironmentUrl!)
                                        .Add("--target-env").Add(targetEnv.EnvironmentUrl!)
                                        .Add("--type").Add(copyType))
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]PAC: {s}[/]")))
                 .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                 .ExecuteAsync();

        SaveConfig(config, sourceEnv, targetEnv);

        AnsiConsole.MarkupLine($"[green]All done! See [link]{targetEnv.EnvironmentUrl}[/][/]");
        AnsiConsole.MarkupLine("[dim]Project configuration saved with target environment. You can now run 'sync' command.[/]");

        return 0;

        // TODO: add a reset option to reset an existing environment to the production environment.

        // TODO: add a different strategy where we import solution(s) from prod, instead of copying the whole environment.
        // should be much faster. also for reset the environment. => use this path also for Development environments.
        // Staging environments should always be a FullCopy of the Production environment!
    }

    static void SaveConfig(ProjectConfig? config, EnvironmentInfo sourceEnv, EnvironmentInfo targetEnv)
    {
        // Save both source (production) and target (development) environments to configuration
        if (config is not null)
        {
            config.ProductionEnvironment = sourceEnv.EnvironmentUrl!;
            config.DevelopmentEnvironment = targetEnv.EnvironmentUrl!;
            config.Save();
        }
    }
}
