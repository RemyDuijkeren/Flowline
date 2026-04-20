using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using Flowline.Config;
using Flowline.Core.Services;
using Flowline.Utils;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class TranslationSettings : FlowlineSettings
{
    [CommandArgument(0, "<action>")]
    [Description("Action to perform: 'export' or 'import'")]
    public string Action { get; set; } = string.Empty;

    [CommandArgument(1, "[path]")]
    [Description("Path to the translation zip file")]
    public string? Path { get; set; }

    [CommandOption("-s|--solution <NAME>")]
    [Description("The unique name of the solution (required for export)")]
    public string? Solution { get; set; }

    [CommandOption("--pac-profile <NAME>")]
    [Description("Use a specific PAC authentication profile name or email")]
    public string? PacProfile { get; set; }

    [CommandOption("--target <TARGET>")]
    [Description("The target environment URL or alias (e.g., 'dev', 'staging', 'prod')")]
    public string? Target { get; set; }
}

public class TranslationCommand(AuthenticationService authService, TranslationSyncService translationService)
    : AsyncCommand<TranslationSettings>
{
    protected override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] TranslationSettings settings, CancellationToken cancellationToken)
    {
        var action = settings.Action.ToLowerInvariant();
        if (action != "export" && action != "import")
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Invalid action. Use 'export' or 'import'.");
            return 1;
        }

        var config = ProjectConfig.Load();
        if (config == null)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Project configuration is not loaded.");
            return 1;
        }

        var targetUrl = settings.Target;

        if (string.IsNullOrEmpty(targetUrl))
        {
            targetUrl = config.DevUrl;
            if (string.IsNullOrEmpty(targetUrl))
            {
                AnsiConsole.MarkupLine("[red]Error:[/] Dev URL is not configured in .flowline.");
                return 1;
            }
            AnsiConsole.MarkupLine($"[grey]No target specified, using default dev URL: {targetUrl}[/]");
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
            AnsiConsole.MarkupLine("[red]Error:[/] Target URL is not configured. Use --target or configure it in .flowline.");
            return 1;
        }

        var solutionName = settings.Solution;
        if (string.IsNullOrEmpty(solutionName))
        {
            var projectSolution = config.Solutions.FirstOrDefault();
            solutionName = projectSolution?.Name;
            if (solutionName != null)
            {
                AnsiConsole.MarkupLine($"[grey]No solution specified, using default solution from config: {solutionName}[/]");
            }
        }

        if (action == "export" && string.IsNullOrEmpty(solutionName))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] Solution name is required for export. Use --solution.");
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
                var profiles = authService.GetPacProfiles();
                var profile = profiles.FirstOrDefault(p => 
                    p.Name?.Equals(settings.PacProfile, StringComparison.OrdinalIgnoreCase) == true ||
                    p.User?.Equals(settings.PacProfile, StringComparison.OrdinalIgnoreCase) == true);

                if (profile == null)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] PAC profile '{settings.PacProfile}' not found.");
                    return 1;
                }
                
                service = authService.ConnectViaPac(profile, targetUrl);
            }
            else
            {
                // Fallback to target URL with PAC silent auth if possible, or explicit connection string
                var profiles = authService.GetPacProfiles().ToList();
                
                // 1. Try environment-specific profile (matched by URL if possible)
                // Note: PAC profiles might not always have Resource filled or it might not match our targetUrl perfectly
                var profile = profiles.FirstOrDefault(p => 
                    p.Resource?.TrimEnd('/').Equals(targetUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) == true);

                // 2. Try UNIVERSAL profile
                profile ??= profiles.FirstOrDefault(p => p.IsUniversal);

                if (profile != null)
                {
                    if (profile.IsUniversal)
                    {
                        AnsiConsole.MarkupLine($"[grey]Found UNIVERSAL PAC profile, connecting silently to {targetUrl}...[/]");
                    }
                    else
                    {
                        AnsiConsole.MarkupLine($"[grey]Found matching PAC profile for {targetUrl}, connecting silently...[/]");
                    }
                    
                    service = authService.ConnectViaPac(profile, targetUrl);
                }
                else
                {
                    // If no PAC profile matches, we might need a connection string from environment
                    var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    var tokenCachePath = Path.Combine(localAppData, ".IdentityService");
                    
                    var connectionString = Environment.GetEnvironmentVariable("DATAVERSE_CONNECTION") 
                                           ?? $"AuthType=OAuth;Url={targetUrl};ClientId=51f81489-12ee-4a9e-aaae-a2591f45987d;RedirectUri=http://localhost;TokenCacheStorePath={tokenCachePath};LoginPrompt=Auto";
                    
                    service = authService.Connect(connectionString);
                }
            }

            AnsiConsole.MarkupLine($"[yellow]Performing {action} on {targetUrl}...[/]");
            
            if (action == "export")
            {
                await translationService.ExportAsync(service, solutionName!, path);
            }
            else
            {
                await translationService.ImportAsync(service, path);
            }

            AnsiConsole.MarkupLine($"[green]Successfully completed {action}![/]");
            return 0;
        }
        catch (Exception ex)
        {
            AnsiConsole.WriteException(ex);
            return 1;
        }
    }
}
