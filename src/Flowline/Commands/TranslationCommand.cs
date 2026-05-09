using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Utils;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class TranslationSettings : FlowlineSettings
{
    [CommandArgument(0, "<action>")]
    [Description("export or import")]
    public string Action { get; set; } = string.Empty;

    [CommandArgument(1, "[path]")]
    [Description("Translation ZIP path")]
    public string? Path { get; set; }

    [CommandOption("-s|--solution <NAME>")]
    [Description("Solution name")]
    public string? Solution { get; set; }

    [CommandOption("--pac-profile <NAME>")]
    [Description("PAC profile name or email")]
    public string? PacProfile { get; set; }

    [CommandOption("--target <TARGET>")]
    [Description("Target URL, dev, staging, or prod")]
    public string? Target { get; set; }
}

public class TranslationCommand(DataverseConnector dataverseConnector, TranslationService translationService, FlowlineRuntimeOptions runtimeOptions)
    : AsyncCommand<TranslationSettings>
{
    protected override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] TranslationSettings settings, CancellationToken cancellationToken)
    {
        runtimeOptions.IsVerbose = settings.Verbose;
        runtimeOptions.JsonOutput = settings.JsonOutput;
        runtimeOptions.Force = settings.Force;

        var action = settings.Action.ToLowerInvariant();
        if (action != "export" && action != "import")
        {
            AnsiConsole.MarkupLine("[red]Invalid action. Use 'export' or 'import'.[/]");
            return 1;
        }

        var config = ProjectConfig.Load();
        if (config == null)
        {
            AnsiConsole.MarkupLine("[red].flowline config not found.[/]");
            return 1;
        }

        var targetUrl = settings.Target;

        if (string.IsNullOrEmpty(targetUrl))
        {
            targetUrl = config.DevUrl;
            if (string.IsNullOrEmpty(targetUrl))
            {
                AnsiConsole.MarkupLine("[red]Dev URL isn't configured in .flowline.[/]");
                return 1;
            }
            AnsiConsole.MarkupLine($"[dim]Target: dev ({targetUrl})[/]");
        }
        else
        {
            targetUrl = targetUrl.ToLowerInvariant() switch
            {
                "dev" => config.DevUrl ?? string.Empty,
                "staging" => config.StagingUrl ?? string.Empty,
                "prod" => config.ProdUrl ?? string.Empty,
                _ => targetUrl
            };
        }

        if (string.IsNullOrEmpty(targetUrl))
        {
            AnsiConsole.MarkupLine("[red]Target URL isn't configured. Use --target or update .flowline.[/]");
            return 1;
        }

        var solutionName = settings.Solution;
        if (string.IsNullOrEmpty(solutionName))
        {
            var projectSolution = config.Solutions.FirstOrDefault();
            solutionName = projectSolution?.Name;
            if (solutionName != null)
            {
                AnsiConsole.MarkupLine($"[dim]Solution: {solutionName}[/]");
            }
        }

        if (action == "export" && string.IsNullOrEmpty(solutionName))
        {
            AnsiConsole.MarkupLine("[red]Solution name is required for export. Use --solution.[/]");
            return 1;
        }

        var path = settings.Path;
        if (string.IsNullOrEmpty(path))
        {
            path = action == "export" 
                ? $"{solutionName}_translations.zip" 
                : "translations.zip";
        }

        try
        {
            IOrganizationServiceAsync2 service;
            
            if (!string.IsNullOrEmpty(settings.PacProfile))
            {
                var profiles = dataverseConnector.GetPacProfiles();
                var profile = profiles.FirstOrDefault(p => 
                    p.Name?.Equals(settings.PacProfile, StringComparison.OrdinalIgnoreCase) == true ||
                    p.User?.Equals(settings.PacProfile, StringComparison.OrdinalIgnoreCase) == true);

                if (profile == null)
                {
                    AnsiConsole.MarkupLine($"[red]PAC profile '{settings.PacProfile}' not found.[/]");
                    return 1;
                }
                
                service = await dataverseConnector.ConnectViaPacAsync(profile, targetUrl, cancellationToken);
            }
            else
            {
                var profile = dataverseConnector.FindBestProfile(targetUrl);

                if (profile != null)
                {
                    if (profile.IsUniversal)
                    {
                        AnsiConsole.MarkupLine($"[dim]Using universal PAC profile for {targetUrl}[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[dim]Using PAC profile for {targetUrl}[/]");
                    }

                    service = await dataverseConnector.ConnectViaPacAsync(profile, targetUrl, cancellationToken);
                }
                else
                {
                    // No PAC profile — fall back to DATAVERSE_CONNECTION env var or prompt
                    var connectionString = Environment.GetEnvironmentVariable("DATAVERSE_CONNECTION");
                    if (string.IsNullOrWhiteSpace(connectionString))
                    {
                        AnsiConsole.MarkupLine("[red]No PAC profile found and DATAVERSE_CONNECTION is not set. Run 'pac auth create' first.[/]");
                        return 1;
                    }

                    service = dataverseConnector.Connect(connectionString);
                }
            }

            if (action == "export")
            {
                await AnsiConsole.Status().FlowlineSpinner().StartAsync(
                    $"Exporting translations for [bold]{solutionName}[/]...",
                    _ => translationService.ExportAsync(service, solutionName!, path));
            }
            else
            {
                await AnsiConsole.Status().FlowlineSpinner().StartAsync(
                    $"Importing translations from [bold]{path}[/]...",
                    _ => translationService.ImportAsync(service, path));
            }

            AnsiConsole.MarkupLine($"[green]Translations {action}ed[/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }
}
