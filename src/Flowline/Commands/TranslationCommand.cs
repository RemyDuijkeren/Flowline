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

    [CommandOption("--target <TARGET>")]
    [Description("Target URL, dev, test, or prod")]
    public string? Target { get; set; }
}

public class TranslationCommand(IAnsiConsole console, DataverseConnector dataverseConnector, TranslationService translationService, FlowlineRuntimeOptions runtimeOptions)
    : AsyncCommand<TranslationSettings>
{
    private readonly IAnsiConsole Console = console;

    protected override async Task<int> ExecuteAsync([NotNull] CommandContext context, [NotNull] TranslationSettings settings, CancellationToken cancellationToken)
    {
        runtimeOptions.IsVerbose = settings.Verbose;
        runtimeOptions.JsonOutput = settings.JsonOutput;
        runtimeOptions.Force = settings.Force;

        var action = settings.Action.ToLowerInvariant();
        if (action != "export" && action != "import")
        {
            Console.Error("Invalid action. Use 'export' or 'import'");
            return 1;
        }

        var config = ProjectConfig.Load();
        if (config == null)
        {
            Console.Error(".flowline config not found");
            return 1;
        }

        var targetUrl = settings.Target;

        if (string.IsNullOrEmpty(targetUrl))
        {
            targetUrl = config.DevUrl;
            if (string.IsNullOrEmpty(targetUrl))
            {
                Console.Error("Dev URL isn't configured in .flowline");
                return 1;
            }
            Console.Verbose($"Target: dev ({targetUrl})", settings.Verbose);
        }
        else
        {
            targetUrl = targetUrl.ToLowerInvariant() switch
            {
                "dev" => config.DevUrl ?? string.Empty,
                "test" => config.TestUrl ?? string.Empty,
                "prod" => config.ProdUrl ?? string.Empty,
                _ => targetUrl
            };
        }

        if (string.IsNullOrEmpty(targetUrl))
        {
            Console.Error("Target URL isn't configured. Use --target or update .flowline");
            return 1;
        }

        var solutionName = settings.Solution;
        if (string.IsNullOrEmpty(solutionName))
        {
            var projectSolution = config.Solutions.FirstOrDefault();
            solutionName = projectSolution?.Name;
            if (solutionName != null)
            {
                Console.Verbose($"Solution: {solutionName}", settings.Verbose);
            }
        }

        if (action == "export" && string.IsNullOrEmpty(solutionName))
        {
            Console.Error("Solution name is required for export. Use --solution");
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
            IOrganizationServiceAsync2? service = null;

            await Console.Status().FlowlineSpinner().StartAsync("Connecting to Dataverse...", async ctx =>
            {
                var profile = dataverseConnector.FindBestProfile(targetUrl);
                if (profile == null)
                {
                    Console.Error("No PAC profile found — run 'pac auth create' first");
                    return;
                }

                try
                {
                    service = await dataverseConnector.ConnectViaPacAsync(profile, targetUrl, cancellationToken);
                }
                catch (InvalidOperationException ex)
                {
                    Console.Error(ex);
                }
            });

            if (service == null) return 1;
            Console.Success("Connected");

            if (action == "export")
            {
                await Console.Status().FlowlineSpinner().StartAsync(
                    $"Exporting translations for [bold]{solutionName}[/]...",
                    _ => translationService.ExportAsync(service, solutionName!, path));
            }
            else
            {
                await Console.Status().FlowlineSpinner().StartAsync(
                    $"Importing translations from [bold]{path}[/]...",
                    _ => translationService.ImportAsync(service, path));
            }

            Console.Success($"Translations {action}ed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteException(ex);
            return 1;
        }
    }
}
