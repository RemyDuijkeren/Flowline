using System.ComponentModel;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Utils;
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
        [CommandArgument(0, "[name]")]
        [Description("Solution unique name to create (omit to enter one interactively)")]
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
        var name = await ResolveNameAsync(settings.Name, cancellationToken);

        // R14/R19: refuse a bad name before spending an interactive environment picker on it.
        SolutionNameValidator.EnsureSolutionUniqueName(name);

        var devEnv = await createEnvironmentResolver.ResolveCreateTargetAsync(settings.DevUrl, settings, cancellationToken);
        if (devEnv is null)
            return 0; // user chose "+ Create new environment" — resolver already emitted the provision advice

        var exitCode = await solutionCreateFlow.RunAsync(
            devEnv,
            name,
            settings.DisplayName,
            settings.PublisherPrefix,
            settings.PublisherName,
            RootFolder,
            Config!,
            settings,
            (projectSln, dataverseSolutionFolder, slnFolder, ct) =>
                ValidatePackAndBuildAsync(projectSln, dataverseSolutionFolder, slnFolder, buildRelease: true, skipBuild: false, ct),
            cancellationToken);

        if (exitCode != 0)
            return exitCode;

        Console.Done("Created! Use 'push' and 'sync' to keep it in flow.");
        return 0;
    }

    // The name argument is optional so `flowline init` alone works like `flowline clone` alone: prompt
    // for it (same prompt clone's create-new path uses). Asked before the environment picker so the
    // name-validation refusal (R14/R19) still lands before any picker time is spent. No flag, no TTY —
    // error naming the argument rather than prompting into a dead terminal (R13).
    internal async Task<string> ResolveNameAsync(string? name, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return name;

        if (!IsInteractive())
            throw new FlowlineException(ExitCode.ValidationFailed,
                "Solution name is required — run 'flowline init <name>', or run this interactively to enter one.");

        return await Console.PromptAsync(new TextPrompt<string>(FlowlineConsoleExtensions.Question("Solution unique name:")), cancellationToken);
    }

    bool IsInteractive() => Console.Profile.Capabilities.Interactive;
}
