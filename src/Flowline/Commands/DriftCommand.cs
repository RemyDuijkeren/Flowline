using System.ComponentModel;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Core.OrphanCleanup;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Utils;
using Flowline.Validation;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class DriftCommand(IAnsiConsole console, DataverseConnector dataverseConnector, OrphanCleanupService orphanCleanupService, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture) : FlowlineCommand<DriftCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<target>")]
        [Description("Target environment: prod, uat, test, dev, or a URL")]
        public string Target { get; set; } = null!;
    }

    protected override string[] ValidForceSpecifiers => FlowlineSettings.ConfigOnlyValidSpecifiers;

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var (env, profile) = await ResolveEnvironmentAsync(settings.Target, settings, cancellationToken);
        var (service, _) = await ConnectToDataverseAsync(dataverseConnector, env.EnvironmentUrl!, cancellationToken, profile);
        // bypassCache: true — drift is a health-check signal with no downstream step (unlike deploy's
        // import, or sync's export) to catch a stale "solution still exists" cache entry. Without this,
        // a solution deleted or renamed in the target could read as "no drift" for up to the solution
        // cache's TTL.
        var (projectSln, _) = await GetAndCheckSolutionAsync(null, env.EnvironmentUrl!, includeManaged: null, settings, cancellationToken, bypassCache: true);

        var slnFolder = RootFolder;
        // Resolved, not composed: the package folder is wherever the solution file says the .cdsproj lives.
        var layout = await SolutionFileLayout.LoadAsync(slnFolder, cancellationToken);
        var packageFolder = layout.DataverseSolutionFolder;

        // drift has no --no-delete flag of its own — it's always read-only — so suppress the
        // deploy-specific "(--no-delete active)" hint in the printed report entirely. OrphanCleanupService
        // owns parsing committed source itself here — drift has no packing step or RunMode choice of its
        // own, so it only needs to say where the source lives (unlike DeployCommand, which builds
        // PostDeployContext directly because it also carries PackagePath/RunMode from its own packing step).
        var result = await orphanCleanupService.CompareAsync(packageFolder, service, projectSln.UniqueName, env.EnvironmentUrl!, cancellationToken, noDeleteHint: null);

        return SelectExitCode(result);
    }

    // <target> accepts a role keyword or a raw URL, mirroring DeployCommand's target-argument shape —
    // unlike KTD2's original role-only design, which forced a --prod/--uat/--test/--dev override per
    // role even though drift only ever acts on one role per invocation (those flags exist on CloneCommand
    // because clone can configure all four roles in one run; drift can't and shouldn't inherit that shape).
    // A role keyword still resolves via the shared, role-generic GetAndCheckEnvironmentInfoAsync (config
    // lookup + Production-type safety check); anything else is treated as a literal URL, matching how
    // DeployCommand's ResolveTargetUrl falls through to the raw target string.
    async Task<(EnvironmentInfo Info, PacProfile Profile)> ResolveEnvironmentAsync(string target, Settings settings, CancellationToken ct)
    {
        var role = TryResolveRole(target);
        if (role is not null)
            return await GetAndCheckEnvironmentInfoAsync(role.Value, null, settings, ct);

        var profile = await ProfileResolutionService.ResolveAsync(target, ct);
        var env = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking [bold]{target}[/]...",
            _ => FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(target, profile, settings, ct));
        if (env == null)
            throw new FlowlineException(ExitCode.ConnectionFailed, $"Environment not found — check the URL '{target}' or your PAC login.");

        Console.Ok($"Env [bold]{env.DisplayName}[/] ({env.EnvironmentUrl}) exists");
        return (env, profile);
    }

    internal static EnvironmentRole? TryResolveRole(string target) => target.ToLowerInvariant() switch
    {
        "prod" => EnvironmentRole.Prod,
        "uat"  => EnvironmentRole.Uat,
        "test" => EnvironmentRole.Test,
        "dev"  => EnvironmentRole.Dev,
        _      => null
    };

    internal static int SelectExitCode(CompareResult result) => result switch
    {
        { Skipped: true }             => (int)ExitCode.Inconclusive,
        { Entries.Count: 0 }          => (int)ExitCode.Success,
        _                             => (int)ExitCode.ValidationFailed
    };
}
