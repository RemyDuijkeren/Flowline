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

public class ProvisionCommand : FlowlineCommand<ProvisionCommand.Settings>
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

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Production URL is required
        var prodEnv = await ResolveAndValidateProdUrlAsync(settings.ProdUrl, settings, cancellationToken);
        if (prodEnv == null) return 1;

        // Prepare the target environment name and url
        var suffix = string.IsNullOrWhiteSpace(settings.Suffix)
            ? (settings.Role == Role.Dev ? "Dev" : "Staging")
            : settings.Suffix;
        var targetDisplayName = $"{prodEnv.DisplayName} {suffix}";
        EnvironmentUrlParts urlParts = PacUtils.GetPartsFromEnvUrl(prodEnv.EnvironmentUrl!);
        var targetUrl = $"https://{urlParts.Organization}-{suffix.ToLower()}.{urlParts.Host}/";

        // TODO: verify if the target environment url is given, is in the same region. Is this needed?
        // if <org> already ends with your suffix, don’t duplicate.
        // If your prod org is named contoso-prod, add a config “swap map” so -prod → -dev/-stg instead of appending.

        string? url = settings.Role switch
        {
            Role.Dev => Config!.GetOrUpdateDevUrl(targetUrl, settings),
            Role.Staging => Config!.GetOrUpdateStagingUrl(targetUrl, settings),
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
                     .ExecuteAsync(cancellationToken)
                     .Task.Spinner();

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

        AnsiConsole.MarkupLine($"Copy '{prodEnv.EnvironmentUrl}' to '{targetUrl}'...");
        var (cmdNameCopy, prefixArgsCopy, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);

        await Cli.Wrap(cmdNameCopy)
                 .WithArguments(args => args
                                        .AddIfNotNull(prefixArgsCopy)
                                        .Add("admin")
                                        .Add("copy")
                                        .Add("--name").Add(targetDisplayName)
                                        .Add("--source-env").Add(prodEnv.EnvironmentUrl!)
                                        .Add("--target-env").Add(targetEnv.EnvironmentUrl!)
                                        .Add("--type").Add(copyType)
                                        .Add("--async"))
                 .WithToolExecutionLog(settings.Verbose)
                 .ExecuteAsync(cancellationToken)
                 .Task.Spinner();

        AnsiConsole.MarkupLine($"[green]All done! See [link]{targetEnv.EnvironmentUrl}[/][/]");

        Config!.Save();
        AnsiConsole.MarkupLine("[dim]Project configuration saved with target environment. You can now run 'sync' command.[/]");

        return 0;

        // TODO: add a different strategy where we import solution(s) from prod, instead of copying the whole environment.
        // should be much faster. also for reset the environment. => use this path also for Development environments.
    }
}
