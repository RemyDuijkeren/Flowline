using System.ComponentModel;
using Flowline.Config;
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

public class DriftCommand(IAnsiConsole console, DataverseConnector dataverseConnector, OrphanCleanupService orphanCleanupService, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture, NuGetVersionClient nuGetVersionClient) : FlowlineCommand<DriftCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<target>")]
        [Description("Target environment: prod, uat, test, dev, or a URL")]
        public string Target { get; set; } = null!;

        [CommandOption("--path <zip>")]
        [Description("Compare this pre-built solution zip against the target instead of your local checkout")]
        public string? Path { get; set; }
    }

    protected override string[] ValidForceSpecifiers => FlowlineSettings.ConfigOnlyValidSpecifiers;

    // U4/KTD4: same standalone rule as deploy — reused directly rather than reimplemented, so the two
    // commands' "am I standalone" definitions can never drift apart.
    protected override bool IsStandalone(Settings settings) => DeployCommand.ResolveStandalone(settings.Path, Directory.GetCurrentDirectory());

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var standalone = IsStandalone(settings);

        // The artifact route keys off the flag itself, not the mode — `--path` names the thing to compare,
        // and that is true inside a project as much as outside one. Gating it on `standalone` (as this
        // first shipped) made `drift <target> --path <zip>` inside a project silently compare the checkout
        // instead, reporting confident orphan results about an input the caller never named. Deploy has
        // always keyed its own artifact route this way (`usingExplicitArtifact`); drift was the outlier.
        var usingArtifact = !string.IsNullOrWhiteSpace(settings.Path);

        // R15: a role keyword resolves through Config in project mode, but standalone's Config is a bare
        // `new ProjectConfig()` (see FlowlineCommand.cs) — every role would fall through to a
        // config-shaped "URL is required" message pointing at a .flowline that was never expected to
        // exist here. Caught before ResolveEnvironmentAsync reaches that route.
        //
        // Checked before the manifest read below so an unresolvable target is diagnosed ahead of the
        // artifact, matching deploy — it resolves its target at the top of ExecuteFlowlineAsync, so
        // `deploy uat --path bad.zip` reports the target. Reading the zip first would have drift report
        // the artifact for that same invocation, and one command's diagnosis shouldn't depend on which
        // of the two you reached for.
        if (standalone && TryResolveRole(settings.Target) is not null)
            throw new FlowlineException(ExitCode.ConfigInvalid, BuildStandaloneRoleError(settings.Target));

        // R5/R7: when an artifact is named, it is the only identity source — read (and validated) before
        // any network call, so a corrupt or missing zip fails fast rather than after a round trip to the
        // target. Without `--path`, identity still comes from GetAndCheckSolutionAsync below (unchanged).
        ProjectSolution? artifactSln = null;
        if (usingArtifact)
        {
            var artifactManifest = DeployCommand.ReadArtifactSolutionManifest(settings.Path!);
            artifactSln = DeployCommand.ResolveStandaloneSolution(settings.Path!, artifactManifest);

            // R14: standalone only — inside a project the mode is not news, and the Goal Capsule scopes
            // this work out of changing project-mode output. Same rule as U3's deploy note.
            if (standalone)
                Console.Info(DeployCommand.BuildStandaloneIdentityNote(Path.GetFileName(settings.Path!)));
        }

        var (env, profile) = await ResolveEnvironmentAsync(settings.Target, settings, cancellationToken);
        var (service, _) = await ConnectToDataverseAsync(dataverseConnector, env.EnvironmentUrl!, cancellationToken, profile);

        if (usingArtifact)
        {
            // Mirrors push/generate's own standalone solution check (FlowlineCommand.cs) — confirms the
            // artifact's solution actually exists in the target before spending time unpacking it.
            //
            // bypassCache: true for the same reason the project-mode call below passes it — drift is a
            // health-check signal with no downstream step to catch a stale "solution still exists" entry,
            // so a solution deleted or renamed in the target would otherwise read as "no drift" for the
            // cache's TTL. push and generate keep the cached read: they have downstream work that would
            // surface a stale answer.
            await GetAndCheckStandaloneSolutionAsync(artifactSln!.UniqueName, env.EnvironmentUrl!, settings, cancellationToken, bypassCache: true);

            var tmpUnpackDir = Directory.CreateTempSubdirectory("flowline-drift-").FullName;
            return await RunInTempDirAsync(tmpUnpackDir, async () =>
            {
                // R9: the artifact's own managed flag drives the unpack's package type — a mismatch
                // unpacks wrong (commit 710c132). Mirrors DeployCommand.cs's temp-unpack call.
                await PacUtils.UnpackSolutionAsync(settings.Path!, tmpUnpackDir, artifactSln.IncludeManaged, _capture, cancellationToken);

                // R12/KTD4: primitives overload, read-only mode, checkoutSolutionSrcRoot: null — a temp
                // unpack has no git history behind it, so every entry's provenance verdict stays
                // Undetermined rather than being resolved against an unrelated checkout. Do NOT route
                // through the convenience overload above: it composes `<folder>/src` and pins
                // checkoutSolutionSrcRoot to that same path, both wrong for a temp unpack (which IS the
                // src root, not its parent).
                var artifactResult = await orphanCleanupService.CompareAsync(
                    tmpUnpackDir, service, artifactSln.UniqueName, env.EnvironmentUrl!, RunMode.NoDelete, cancellationToken,
                    noDeleteHint: null, checkoutSolutionSrcRoot: null);

                return SelectExitCode(artifactResult);
            }, Logger);
        }

        // bypassCache: true — drift is a health-check signal with no downstream step (unlike deploy's
        // import, or sync's export) to catch a stale "solution still exists" cache entry. Without this,
        // a solution deleted or renamed in the target could read as "no drift" for up to the solution
        // cache's TTL.
        var (projectSln, _) = await GetAndCheckSolutionAsync(null, env.EnvironmentUrl!, includeManaged: null, settings, cancellationToken, bypassCache: true);

        var slnFolder = RootFolder;
        // Resolved, not composed: the Dataverse solution folder is wherever the solution file says the .cdsproj lives.
        var layout = await SolutionFileLayout.LoadAsync(slnFolder, cancellationToken);
        var dataverseSolutionFolder = layout.DataverseSolutionFolder;

        // drift has no --no-delete flag of its own — it's always read-only — so suppress the
        // deploy-specific "(--no-delete active)" hint in the printed report entirely. OrphanCleanupService
        // owns parsing committed source itself here — drift has no packing step or RunMode choice of its
        // own, so it only needs to say where the source lives (unlike DeployCommand, which builds
        // PostDeployContext directly because it also carries PackagePath/RunMode from its own packing step).
        var result = await orphanCleanupService.CompareAsync(dataverseSolutionFolder, service, projectSln.UniqueName, env.EnvironmentUrl!, cancellationToken, noDeleteHint: null);

        return SelectExitCode(result);
    }

    // R15: pure so the wording is unit-testable without a live PAC CLI or Dataverse connection, mirroring
    // DeployCommand.ResolveTargetUrl's own standalone branch. Assumes the caller already confirmed
    // `target` resolves to a role (TryResolveRole is not null) — this only builds the message.
    internal static string BuildStandaloneRoleError(string target) =>
        $"Can't resolve '{target}' — no config here to check (standalone mode). Pass the environment URL instead.";

    // R13: extracted from the standalone branch above purely so the cleanup guarantee (temp dir removed
    // on success AND on failure) is testable without a live PAC CLI or Dataverse connection. Same
    // swallow-on-cleanup-failure shape as DeployCommand's own temp-unpack `finally` block — a locked/
    // in-use temp file must never mask whatever exception was already propagating from `action`.
    internal static async Task<int> RunInTempDirAsync(string tmpDir, Func<Task<int>> action, ILogger logger)
    {
        try
        {
            return await action();
        }
        finally
        {
            try
            {
                if (Directory.Exists(tmpDir))
                    Directory.Delete(tmpDir, recursive: true);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to clean up temp unpack directory {TmpUnpackDir}", tmpDir);
            }
        }
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
