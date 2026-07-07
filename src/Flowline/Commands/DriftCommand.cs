using System.ComponentModel;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class DriftCommand(IAnsiConsole console, DataverseConnector dataverseConnector, OrphanCleanupService orphanCleanupService, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture) : FlowlineCommand<DriftCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<target>")]
        [Description("Target environment: prod, uat, test, or dev")]
        public string Target { get; set; } = null!;

        [CommandArgument(1, "[solution]")]
        [Description("Solution to check (optional in project mode)")]
        public string? Solution { get; set; }

        [CommandOption("--prod <URL>")]
        [Description("Production environment URL (only used when target is prod)")]
        public string? ProdUrl { get; set; }

        [CommandOption("--uat <URL>")]
        [Description("UAT environment URL (only used when target is uat)")]
        public string? UatUrl { get; set; }

        [CommandOption("--test <URL>")]
        [Description("Test environment URL (only used when target is test)")]
        public string? TestUrl { get; set; }

        [CommandOption("--dev <URL>")]
        [Description("Development environment URL (only used when target is dev)")]
        public string? DevUrl { get; set; }
    }

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var role = ResolveRole(settings.Target);
        // Matches whichever --prod/--uat/--test/--dev override was passed for this target, so the
        // ConfigInvalid error GetAndCheckEnvironmentInfoAsync throws for an unconfigured environment
        // names a flag this command actually accepts (mirroring SyncCommand's single-role pattern,
        // generalized across all four roles since drift's target is chosen at runtime).
        var inputUrl = role switch
        {
            EnvironmentRole.Prod => settings.ProdUrl,
            EnvironmentRole.Uat  => settings.UatUrl,
            EnvironmentRole.Test => settings.TestUrl,
            EnvironmentRole.Dev  => settings.DevUrl,
            _                    => null
        };

        var env = await GetAndCheckEnvironmentInfoAsync(role, inputUrl, settings, cancellationToken);
        var (service, _) = await ConnectToDataverseAsync(dataverseConnector, env.EnvironmentUrl!, cancellationToken);
        // bypassCache: true — drift is a health-check signal with no downstream step (unlike deploy's
        // import, or sync's export) to catch a stale "solution still exists" cache entry. Without this,
        // a solution deleted or renamed in the target could read as "no drift" for up to the solution
        // cache's TTL.
        var (projectSln, _) = await GetAndCheckSolutionAsync(settings.Solution, env.EnvironmentUrl!, includeManaged: null, settings, cancellationToken, bypassCache: true);

        var slnFolder = Path.Combine(RootFolder, AllSolutionsFolderName, projectSln.Name);
        var packageFolder = PackageFolder(slnFolder);
        var packageSrcRoot = Path.Combine(packageFolder, "src");
        var (localComponents, entityLogicalNames, namedComponents) = ComponentClassifier.ParseLocalSource(packageFolder);

        // drift never packs a solution, so there's no real PackagePath value — pass an explicit
        // empty sentinel rather than packageSrcRoot, which means something else in every other caller.
        var postDeployContext = new PostDeployContext(service, projectSln.Name, localComponents, RunMode.NoDelete, string.Empty, env.EnvironmentUrl!, entityLogicalNames, packageSrcRoot, namedComponents);

        // drift has no --no-delete flag of its own — it's always read-only — so suppress the
        // deploy-specific "(--no-delete active)" hint in the printed report entirely.
        var result = await orphanCleanupService.CompareAsync(postDeployContext, cancellationToken, noDeleteHint: null);

        return SelectExitCode(result);
    }

    internal static EnvironmentRole ResolveRole(string target) => target.ToLowerInvariant() switch
    {
        "prod" => EnvironmentRole.Prod,
        "uat"  => EnvironmentRole.Uat,
        "test" => EnvironmentRole.Test,
        "dev"  => EnvironmentRole.Dev,
        _      => throw new FlowlineException(ExitCode.ConfigInvalid, $"Unknown target '{target}' — use prod, uat, test, or dev.")
    };

    internal static int SelectExitCode(CompareResult result) => result switch
    {
        { Skipped: true }             => (int)ExitCode.Inconclusive,
        { Entries.Count: 0 }          => (int)ExitCode.Success,
        _                             => (int)ExitCode.ValidationFailed
    };
}
