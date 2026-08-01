using System.ComponentModel;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Flowline.Commands;

// U5: the discoverable front door for greenfield create (KD1) — thin over SolutionCreateFlow, which
// is the shared logic clone's create-new path (a later unit) will also call. RequiresProject=false:
// like clone, init is how a Flowline project comes to exist, so there is no project yet to require.
public class InitCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService,
    ILoggerFactory loggerFactory, SubprocessCapture capture, CreateEnvironmentResolver createEnvironmentResolver, SolutionCreateFlow solutionCreateFlow) :
    FlowlineCommand<InitCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<name>")]
        [Description("Solution unique name to create")]
        public string? Name { get; set; }

        [CommandOption("--dev <URL>")]
        [Description("Target DEV environment URL (omit to pick from your tenant)")]
        public string? DevUrl { get; set; }

        [CommandOption("--display-name <TEXT>")]
        [Description("Solution display name (defaults to a humanized form of the unique name)")]
        public string? DisplayName { get; set; }

        [CommandOption("--publisher-prefix <PREFIX>")]
        [Description("Publisher prefix — reuses a matching publisher or creates one (omit to pick interactively)")]
        public string? PublisherPrefix { get; set; }

        [CommandOption("--publisher-name <TEXT>")]
        [Description("Friendly name for a newly created publisher (defaults to the prefix)")]
        public string? PublisherName { get; set; }
    }

    protected override bool RequiresProject => false;
    protected override string[] ValidForceSpecifiers => FlowlineSettings.ConfigOnlyValidSpecifiers;

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // R14/R19: refuse a bad name before spending an interactive environment picker on it.
        SolutionNameValidator.EnsureSolutionUniqueName(settings.Name);

        var devEnv = await createEnvironmentResolver.ResolveAsync(settings.DevUrl, settings, cancellationToken);

        var exitCode = await solutionCreateFlow.RunAsync(
            devEnv,
            settings.Name!,
            settings.DisplayName,
            settings.PublisherPrefix,
            settings.PublisherName,
            RootFolder,
            Config!,
            (projectSln, dataverseSolutionFolder, slnFolder, ct) =>
                ValidatePackAndBuildAsync(projectSln, dataverseSolutionFolder, slnFolder, buildRelease: true, skipBuild: false, ct),
            cancellationToken);

        if (exitCode != 0)
            return exitCode;

        Console.Done("Created! Use 'push' and 'sync' to keep it in flow.");
        return 0;
    }
}
