using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Flowline.Utils;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Extensions;
using Command = CliWrap.Command;

namespace Flowline.Commands;

public enum Role { Dev, Staging }

public enum CopyType { Minimal, Full }

public class ProvisionCommand : AsyncCommand<ProvisionCommand.Settings>
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "[role]")]
        [Description("Choose whether to provision `dev` or `staging` (default: dev)")]
        public Role Role { get; set; } = Role.Dev; // dev|staging

        [CommandOption("--prod <URL>")]
        [Description("The production environment to provision the new environment from")]
        public string? ProdUrl { get; set; }

        [CommandOption("--copy <minimal|full>")]
        [Description("Provision environment with data (full) or no data (minimal) from production environment (default: minimal for dev, full for staging)")]
        public CopyType? CopyType { get; set; }

        [CommandOption("--suffix <suffix>")]
        [Description("Override the default suffix used to derive the target url (default: <role name>)")]
        public string? Suffix { get; set; }

        [CommandOption("--allow-overwrite")]
        [Description("Allow overwriting existing environments (default: false)")]
        public bool AllowOverwrite { get; set; } = false;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var rootFolder = Directory.GetCurrentDirectory();
        await AnsiConsole.Status().StartAsync("Validating environment...", async ctx =>
        {
            await DotNetUtils.AssertDotNetInstalledAsync(settings.Verbose, cancellationToken);
            await PacUtils.AssertPacCliInstalledAsync(settings.Verbose, cancellationToken);
            await GitUtils.AssertGitInstalledAsync(settings.Verbose, cancellationToken);
            await GitUtils.AssertGitRepoAsync(rootFolder, settings.Verbose, cancellationToken);
        });

        AnsiConsole.MarkupLine("[green]Valid Environment[/]");

        // Load or create the project configuration
        var config = ProjectConfig.Load();
        if (config != null)
        {
            AnsiConsole.MarkupLine("[yellow]Project configuration (.flowline) already exists. Skip creating[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("No project configuration found. Creating...");
            config = new ProjectConfig();
        }

        // Production URL is required
        var prodUrl = config.GetOrUpdateProdUrl(settings.ProdUrl, settings);
        if (prodUrl == null)
        {
            AnsiConsole.MarkupLine("[red]Production environment is required. Please provide a Dataverse environment URL using --prod <URL>.[/]");
            return 1;
        }

        // Validate Prod URL
        var srcEnvironment = await PacUtils.GetEnvironmentInfoByUrlAsync(prodUrl, settings.Verbose, cancellationToken);
        if (srcEnvironment == null)
        {
            AnsiConsole.MarkupLine("[red]Invalid Production environment. Please provide a valid Dataverse environment URL using --prod <URL> or run clone first.[/]");
            return 1;
        }

        if (srcEnvironment.Type != "Production")
        {
            AnsiConsole.MarkupLine("[red]Production environment must be of type 'Production'.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"  Using Production environment: [bold]{srcEnvironment.DisplayName}[/] ({srcEnvironment.EnvironmentUrl}) - Type: {srcEnvironment.Type})");

        // Prepare the target environment name and url
        var suffix = string.IsNullOrWhiteSpace(settings.Suffix)
            ? (settings.Role == Role.Dev ? "Dev" : "Staging")
            : settings.Suffix;
        var targetDisplayName = $"{srcEnvironment.DisplayName} {suffix}";
        EnvironmentUrlParts urlParts = PacUtils.GetPartsFromEnvUrl(srcEnvironment.EnvironmentUrl!);
        var targetUrl = $"https://{urlParts.Organization}-{suffix.ToLower()}.{urlParts.Host}/";

        // TODO: verify if the target environment url is given, is in the same region. Is this needed?
        // if <org> already ends with your suffix, don’t duplicate.
        // If your prod org is named contoso-prod, add a config “swap map” so -prod → -dev/-stg instead of appending.

        string? url = settings.Role switch
        {
            Role.Dev => config.GetOrUpdateDevUrl(targetUrl, settings),
            Role.Staging => config.GetOrUpdateStagingUrl(targetUrl, settings),
            _ => null
        };

        if (url == null)
        {
            AnsiConsole.MarkupLine("[red]Can't create valid url or mismatch with .flowline.[/]");
            return 1;
        }

        // Validate target environment
        var targetEnv = await PacUtils.GetEnvironmentInfoByUrlAsync(targetUrl, settings.Verbose, cancellationToken);
        if (targetEnv == null)
        {
            AnsiConsole.MarkupLine($"Creating environment {targetUrl}...");

            var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
            await Cli.Wrap(cmdName)
                     .WithArguments(args => args
                                            .AddIfNotNull(prefixArgs)
                                            .Add("admin")
                                            .Add("create")
                                            .Add("--name").Add($"{targetDisplayName} (cloning)")
                                            .Add("--domain").Add($"{urlParts.Organization}-{suffix.ToLower()}")
                                            .Add("--region").Add(urlParts.Region)
                                            .Add("--async"))
                     .WithToolExecutionLog(settings.Verbose)
                     .ExecuteAsync(cancellationToken);

            targetEnv = await PacUtils.GetEnvironmentInfoByUrlAsync(targetUrl, true, cancellationToken);
            if (targetEnv == null)
            {
                AnsiConsole.MarkupLine("[red]Target Environment not found after creating.[/]");
                return 1;
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"Target Environment already exists: [link]{targetEnv.EnvironmentUrl}[/]");
        }

        if (targetEnv.Type == "Production")
        {
            AnsiConsole.MarkupLine("[red]Cannot overwrite production environment.[/]");
            return 1;
        }

        if (!settings.AllowOverwrite)
        {
            AnsiConsole.MarkupLine($"[red]Environment '{targetEnv.DisplayName}' ({targetEnv.EnvironmentUrl}) already exists. Use --allow-overwrite to overwrite.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine("Overwriting existing environment...");
        // reset: empty env with factory settings (https://learn.microsoft.com/en-us/power-platform/admin/reset-environment)?
        // after rest: deploy solution from prod?

        // Staging is always a FullCopy
        string copyType = (settings.Role == Role.Staging || settings.CopyType == CopyType.Full) ? "FullCopy" : "MinimalCopy";

        AnsiConsole.MarkupLine($"Copy '{prodUrl}' to '{targetUrl}'...");
        var (cmdNameCopy, prefixArgsCopy, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
        var pacAdminCopyCmd = Cli.Wrap(cmdNameCopy)
            .WithArguments(args => args
                .AddIfNotNull(prefixArgsCopy)
                .Add("admin")
                .Add("copy")
                .Add("--name").Add(targetDisplayName)
                .Add("--source-env").Add(srcEnvironment.EnvironmentUrl!)
                .Add("--target-env").Add(targetEnv.EnvironmentUrl!)
                .Add("--type").Add(copyType)
                .Add("--async"))
            .WithToolExecutionLog();

        await pacAdminCopyCmd
                 .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]PAC: {s}[/]")))
                 .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                 .ExecuteAsync(cancellationToken);

        AnsiConsole.MarkupLine($"[green]All done! See [link]{targetEnv.EnvironmentUrl}[/][/]");

        config.Save();
        AnsiConsole.MarkupLine("[dim]Project configuration saved with target environment. You can now run 'sync' command.[/]");

        return 0;

        // TODO: add a different strategy where we import solution(s) from prod, instead of copying the whole environment.
        // should be much faster. also for reset the environment. => use this path also for Development environments.
    }
}
