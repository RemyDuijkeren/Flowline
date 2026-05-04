using System.ComponentModel;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;
using Spectre.Console.Cli;
using Flowline.Core.Services;

namespace Flowline.Commands;

public class SyncWebResourcesCommand : AsyncCommand<SyncWebResourcesCommand.Settings>
{
    private readonly WebResourceService _syncService;

    public SyncWebResourcesCommand(WebResourceService syncService)
    {
        _syncService = syncService;
    }

    public class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<root>")]
        [Description("Root folder of the web resources")]
        public string? Root { get; set; }

        [CommandOption("-s|--solution <name>")]
        [Description("Unique name of the solution")]
        public string? Solution { get; set; }
    }

    protected override Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Implementation logic to connect to Dataverse and call _syncService.SyncSolutionAsync
        // For brevity in this step, I'm focusing on the command structure.
        AnsiConsole.MarkupLine("[green]Syncing web resources...[/]");
        return Task.FromResult(0);
    }
}
