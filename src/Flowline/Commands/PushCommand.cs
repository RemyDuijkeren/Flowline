using System.ComponentModel;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Models;
using Flowline.Core.Console;
using Flowline.Core.Services;
using Flowline.Core.FormEvents;
using Flowline.Core.Plugins;
using Flowline.Core.WebResources;
using Flowline.Diagnostics;
using Flowline.Services;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;
using Flowline.Utils;
using Flowline.Validation;

namespace Flowline.Commands;

public class PushCommand(IAnsiConsole console, DataverseConnector dataverseConnector, PluginService pluginService, WebResourceService webResourceService, FormEventService formEventService, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture)
    : FlowlineCommand<PushCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture)
{
    [Flags]
    public enum PushScope
    {
        None = 0,
        AssemblyOnly = 1,
        Plugins = 2,
        WebResources = 4,
        // Additive, not a replacement for WebResources' existing bundling: WebResources alone still runs
        // both web resource sync and form event registration, unchanged. FormEvents lets that registration
        // step run on its own — reconciling // flowline:onload/onsave annotations against an already-built
        // dist/ folder without also syncing web resource content — e.g. after editing only an annotation.
        FormEvents = 8,
        All = WebResources | Plugins
    }

    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "[solution]")]
        [Description("Solution to push (optional in project mode)")]
        public string? Solution { get; set; }

        [CommandOption("-s|--scope <SCOPE>")]
        [Description("Limit the push scope: all, webresources, formevents, plugins, or assemblyonly. Can be used more than once.")]
        public PushScope[] Scopes { get; set; } = [];

        [CommandOption("-p|--pluginFile <PATH>")]
        [Description("Prebuilt plugin file (.dll) to push without using a Flowline project")]
        public string? PluginFile { get; set; }

        [CommandOption("-w|--webresources <PATH>")]
        [Description("Web resource folder to push without using a Flowline project")]
        public string? WebResources { get; set; }

        [CommandOption("--dev <url>")]
        [Description("Use this dev environment URL")]
        public string? DevUrl { get; set; }

        [CommandOption("--no-delete")]
        [Description("Push without deleting any Dataverse assets that are missing from source")]
        [DefaultValue(false)]
        public bool NoDelete { get; set; } = false;

        [CommandOption("--no-build")]
        [Description("Skip 'dotnet build' and push the existing Release artifacts")]
        [DefaultValue(false)]
        public bool NoBuild { get; set; } = false;

        [CommandOption("--no-publish")]
        [Description("Skip publishing web resources and form event handlers after sync")]
        [DefaultValue(false)]
        public bool NoPublish { get; set; } = false;

        [CommandOption("--dry-run")]
        [Description("Preview changes without touching Dataverse")]
        [DefaultValue(false)]
        public bool DryRun { get; set; } = false;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        if (!IsStandaloneMode(settings))
            return await base.ExecuteAsync(context, settings, cancellationToken).ConfigureAwait(false);

        InitializeRuntimeOptions(settings);
        await CheckSetupAsync(settings, cancellationToken).ConfigureAwait(false);
        return await ExecuteFlowlineAsync(context, settings, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task CheckSetupAsync(Settings settings, CancellationToken cancellationToken)
    {
        if (!IsStandaloneMode(settings))
        {
            await base.CheckSetupAsync(settings, cancellationToken).ConfigureAwait(false);
            return;
        }

        await Console.Status().FlowlineSpinner().StartAsync("Checking your setup...", async ctx =>
        {
            await FlowlineValidator.Default.EnsurePacCliAsync(settings, cancellationToken);
        });

        Console.Ok("All good, let's go!");
    }

    internal static readonly string[] ValidSpecifiers =
        ["delete-orphans", "recreate-assembly", "delete-form-handlers", "config", "all"];
    protected override string[] ValidForceSpecifiers => ValidSpecifiers;

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var standaloneMode = IsStandaloneMode(settings);

        if (standaloneMode) ValidateStandaloneMode(settings, RootFolder);

        // R8: a passed [solution] no longer selects among multiple configured solutions (only one
        // exists) — it now just needs to match the one already configured. Checked before any
        // Dataverse round-trip (env/solution lookups happen further down in ResolveEnvironmentAndSolutionAsync).
        if (!standaloneMode && Config!.Solution != null)
            ValidateSolutionMatchesConfig(settings.Solution, Config.Solution.UniqueName);

        var runMode = ResolveRunMode(settings);
        var standaloneParams = ResolveStandaloneParameters(settings, standaloneMode);

        var environmentUrl = "";
        if (standaloneMode)
            environmentUrl = ResolveStandaloneEnvironmentUrl(settings, dataverseConnector);

        var (devEnv, solutionName, pluginPackageMode, resolvedProfile) = await ResolveEnvironmentAndSolutionAsync(settings, standaloneMode, environmentUrl, standaloneParams, cancellationToken);

        if (!standaloneMode)
            environmentUrl = devEnv.EnvironmentUrl!;

        Logger.LogInformation("target={EnvironmentUrl} solution={SolutionName}", environmentUrl, solutionName);

        var pushScope = ResolveScope(settings, standaloneMode);
        var pushAssemblyOnly = pushScope.HasFlag(PushScope.AssemblyOnly);
        Logger.LogInformation("scope={Scope} mode={RunMode} standalone={Standalone}", pushScope, runMode, standaloneMode);

        // A solution can hold more than one plugin project, so this is a list — every confirmed project is
        // pushed in this one invocation, each registered independently under the same Dataverse solution.
        var pluginTargets = (pushAssemblyOnly || pushScope.HasFlag(PushScope.Plugins))
            ? await PreparePluginsForPushAsync(standaloneMode, settings, pluginPackageMode, standaloneParams, cancellationToken)
            : [];
        // FormEvents reads its annotations from the same built dist/ folder web resource sync uses, so
        // either scope alone needs it prepared — WebResources still implies FormEvents (unchanged default
        // bundling); FormEvents lets the registration step run on its own, against an already-pushed dist/.
        var runFormEvents = pushScope.HasFlag(PushScope.WebResources) || pushScope.HasFlag(PushScope.FormEvents);
        var webResourcesSyncFolder = runFormEvents
            ? await PrepareWebResourcesForPushAsync(standaloneMode, settings, standaloneParams, cancellationToken)
            : null;

        var (conn, _) = await ConnectToDataverseAsync(dataverseConnector, environmentUrl, cancellationToken, resolvedProfile);

        var pushedChanges = false;

        // Every project's assemblies, resolved once before the first sync runs: each pass has to know the
        // whole set up front or it reads the others as orphans (see PluginService.ExcludePushedAssemblies).
        var pushedAssemblyNames = CollectPushedAssemblyNames(pluginTargets);

        for (var i = 0; i < pluginTargets.Count; i++)
        {
            var target = pluginTargets[i];

            if (DescribePluginPushHeader(pluginTargets, i) is { } header)
                Console.Info(header);

            try
            {
                // R1/KD1/KD6: a .nupkg alongside the classic .dll (or an explicit --pluginFile .nupkg) routes
                // to the shared package entry point regardless of --scope assemblyonly — there is no separate
                // "assembly only" package variant, since the package path always reconciles steps (KD4).
                if (IsPackagePush(target.PushPath))
                {
                    Logger.LogInformation("Pushing plugin package: {Nupkg}", target.PushPath);
                    // Standalone mode already reflected this .nupkg to resolve the assembly name (R2a) — reuse
                    // that list instead of paying for a second AnalyzePackage pass over the same file.
                    pushedChanges |= target.ReflectedAssemblies != null
                        ? await pluginService.SyncSolutionFromPackageAsync(conn, target.ReflectedAssemblies,
                            await File.ReadAllBytesAsync(target.PushPath, cancellationToken).ConfigureAwait(false),
                            target.PushPath, target.AssemblyName, solutionName, runMode,
                            settings.HasForce("delete-orphans"), cancellationToken).ConfigureAwait(false)
                        : await pluginService.SyncSolutionFromPackageAsync(conn, target.PushPath, target.AssemblyName, solutionName, runMode,
                            settings.HasForce("delete-orphans"), cancellationToken).ConfigureAwait(false);
                }
                else if (pushAssemblyOnly)
                {
                    Logger.LogInformation("Pushing assembly only: {Dll}", target.PushPath);
                    pushedChanges |= await pluginService.SyncAssemblyOnlyAsync(conn, target.PushPath, solutionName, runMode, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Logger.LogInformation("Pushing plugins: {Dll}", target.PushPath);
                    pushedChanges |= await pluginService.SyncSolutionAsync(conn, target.PushPath, solutionName, runMode,
                        settings.HasForce("delete-orphans"), settings.HasForce("recreate-assembly"), cancellationToken,
                        pushedAssemblyNames).ConfigureAwait(false);
                }
            }
            // Rethrow, not recover: the only thing added is which project it was and what the org holds
            // now, which the failure itself cannot know. The global handler prints Message and not the
            // inner chain, so the original reason is carried in the new message rather than nested out of
            // sight. Cancellation is left alone — Program.cs maps it to ExitCode.Cancelled.
            catch (Exception ex) when (ex is not OperationCanceledException
                                    && DescribePluginPushFailure(pluginTargets, i, ex.Message) != null)
            {
                Logger.LogError(ex, "Plugin project push failed: {Project}", target.ProjectName);
                throw new FlowlineException(
                    ex is FlowlineException fe ? fe.ExitCode : ExitCode.GeneralError,
                    DescribePluginPushFailure(pluginTargets, i, ex.Message)!, ex);
            }
        }

        if (settings.NoPublish && !runFormEvents)
            Console.Warning("--no-publish has no effect: web resources/form events not in scope.");

        if (webResourcesSyncFolder != null)
        {
            var dryRun = runMode == RunMode.DryRun;
            var publishAfterSync = !settings.NoPublish;
            // U2: resolved once per push — one form-event identity cache file per environment, so a later
            // rename-detection unit (U3) has data from every successful resolution to suggest from.
            var formEventCachePath = FlowlineStoragePaths.GetFormEventCachePath(environmentUrl);

            // KTD12: cleanup runs before web resources are created/updated/deleted — removes stale/orphaned
            // form event handlers (R14) so a pending web-resource delete never trips Dataverse's
            // "referenced by N other components" dependency fault.
            pushedChanges |= await formEventService.CleanupOrphanedAsync(conn, webResourcesSyncFolder, solutionName, settings.HasForce("delete-form-handlers"), dryRun, publishAfterSync, formEventCachePath, cancellationToken).ConfigureAwait(false);

            if (pushScope.HasFlag(PushScope.WebResources))
            {
                Logger.LogInformation("Pushing web resources: {Folder}", webResourcesSyncFolder);
                pushedChanges |= await webResourceService.SyncSolutionAsync(conn, webResourcesSyncFolder, solutionName, publishAfterSync: publishAfterSync, runMode: runMode, cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            if (settings.NoPublish)
                Console.Skip("Publish — skipping (--no-publish active).");

            // R10a: registration runs strictly after web resources are pushed, same scope gate — new/updated
            // handlers can only reference libraries that already exist in Dataverse.
            pushedChanges |= await formEventService.RegisterAsync(conn, webResourcesSyncFolder, solutionName, settings.HasForce("delete-form-handlers"), dryRun, publishAfterSync, formEventCachePath, cancellationToken).ConfigureAwait(false);
        }

        Console.Done(runMode == RunMode.DryRun
            ? "Air push complete. Dataverse remains oblivious. Now do it for real without --dry-run!"
            : !pushedChanges
                ? "Nothing to push — already up to date."
                : standaloneMode
                    ? "Assets pushed! (•ᴗ•)و"
                    : "Assets pushed! Use 'sync' to keep it in flow. (•ᴗ•)و");

        return 0;
    }

    private static RunMode ResolveRunMode(Settings settings) =>
        settings.DryRun ? RunMode.DryRun
            : settings.NoDelete ? RunMode.NoDelete
            : RunMode.Normal;

    private StandaloneParams ResolveStandaloneParameters(Settings settings, bool standaloneMode)
    {
        if (!standaloneMode) return new StandaloneParams();

        var solutionName = ResolveStandaloneSolutionName(settings);
        var dllPath = !string.IsNullOrWhiteSpace(settings.PluginFile) ? ResolveStandalonePluginFilePath(settings) : null;
        var webResourcesPath = !string.IsNullOrWhiteSpace(settings.WebResources) ? ResolveStandaloneWebResourcesPath(settings) : null;

        return new StandaloneParams { SolutionName = solutionName, DllPath = dllPath, WebResourcesPath = webResourcesPath };
    }

    private async Task<(EnvironmentInfo, string, PluginPackageMode, PacProfile)> ResolveEnvironmentAndSolutionAsync(
        Settings settings,
        bool standaloneMode,
        string environmentUrl,
        StandaloneParams standaloneParams,
        CancellationToken cancellationToken)
    {
        EnvironmentInfo devEnv;
        PacProfile profile;
        string solutionName;
        SolutionInfo slnInfo;
        var pluginPackageMode = PluginPackageMode.Auto;

        if (standaloneMode)
        {
            (devEnv, profile) = await GetAndCheckStandaloneEnvironmentAsync(environmentUrl, settings, cancellationToken).ConfigureAwait(false);
            slnInfo = await GetAndCheckStandaloneSolutionAsync(standaloneParams.SolutionName!, environmentUrl, settings, cancellationToken).ConfigureAwait(false);
            solutionName = standaloneParams.SolutionName!;
        }
        else
        {
            (devEnv, profile) = await GetAndCheckEnvironmentInfoAsync(EnvironmentRole.Dev, settings.DevUrl, settings, cancellationToken);
            var (projectSln, slnInfoResult) = await GetAndCheckSolutionAsync(settings.Solution, devEnv.EnvironmentUrl!, cancellationToken: cancellationToken, settings: settings);
            slnInfo = slnInfoResult;
            solutionName = projectSln.UniqueName;
            pluginPackageMode = projectSln.PluginPackageMode;
        }

        if (slnInfo.IsManaged)
            throw new FlowlineException(ExitCode.ValidationFailed, "Managed solutions are not supported for push.");

        return (devEnv, solutionName, pluginPackageMode, profile);
    }

    /// <summary>One plugin artifact ready to push, with the assembly name Dataverse should see.</summary>
    /// <param name="ProjectName">
    /// The solution-file project this came from — what the user has to go fix when the push fails, and
    /// the only thing that distinguishes one project's push output from another's.
    /// </param>
    /// <param name="ReflectedAssemblies">
    /// Every plugin-bearing assembly inside the artifact, set for every <c>.nupkg</c> target in both
    /// modes and null for a classic <c>.dll</c> (which is its own single assembly). Reflecting up front
    /// spares the push a second pass over the same file, and is the only way
    /// <see cref="CollectPushedAssemblyNames"/> learns a package's non-primary assembly names.
    /// </param>
    internal sealed record PluginPushTarget(
        string PushPath,
        string AssemblyName,
        string ProjectName,
        List<PluginAssemblyMetadata>? ReflectedAssemblies = null);

    /// <summary>Every assembly name this push owns, so no project's sync treats another's as an orphan.</summary>
    /// <remarks>
    /// Includes a package's non-primary assemblies when they're already known: a multi-assembly
    /// <c>.nupkg</c> in one project and a classic <c>.dll</c> in another is a reachable shape, and the
    /// classic project's orphan sweep is the one that would otherwise flag the package's extra assemblies.
    /// </remarks>
    internal static IReadOnlyCollection<string> CollectPushedAssemblyNames(IReadOnlyList<PluginPushTarget> targets) =>
        targets.SelectMany(t => (t.ReflectedAssemblies ?? []).Select(a => a.Name).Prepend(t.AssemblyName))
               .Where(n => !string.IsNullOrWhiteSpace(n))
               .Distinct(StringComparer.OrdinalIgnoreCase)
               .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
               .ToList();

    /// <summary>Section header naming the project about to push, or <c>null</c> when there's only one.</summary>
    /// <remarks>
    /// With one project the header is pure noise — the sync's own output already names the one assembly,
    /// and N=1 must read exactly as it did (R7). With more, every line below it repeats verbatim per
    /// project ("Solution found", "Registration plan ready: ..."), so without a header the user cannot
    /// tell whose output they're reading.
    /// </remarks>
    internal static string? DescribePluginPushHeader(IReadOnlyList<PluginPushTarget> targets, int index) =>
        targets.Count <= 1 ? null : $"[bold]{targets[index].ProjectName}[/] — pushing";

    /// <summary>
    /// Failure message for one project in a multi-project push: what broke, and what the org holds now.
    /// </summary>
    /// <remarks>
    /// Fail-fast, not continue-and-collect. Push writes to a live org, so once one project fails the run
    /// is already in a partial state; carrying on would deepen it while the user watches, and a second
    /// failure downstream of the first is far more likely to be a consequence than new information. So
    /// the run stops — but "stopped" is only safe if it's legible, hence naming the failed project, what
    /// already landed, and what was never tried. <c>null</c> for a single-project push: there is nothing
    /// to attribute, and the original exception reaches the user unchanged (R7).
    /// </remarks>
    internal static string? DescribePluginPushFailure(IReadOnlyList<PluginPushTarget> targets, int failedIndex, string reason)
    {
        if (targets.Count <= 1) return null;

        var failed = targets[failedIndex].ProjectName;
        var pushed = targets.Take(failedIndex).Select(t => t.ProjectName).ToList();
        var notAttempted = targets.Skip(failedIndex + 1).Select(t => t.ProjectName).ToList();

        var sentences = new List<string> { $"'{failed}' failed to push: {EndSentence(reason)}" };
        if (pushed.Count > 0) sentences.Add($"Already in the org: {string.Join(", ", pushed)}.");
        if (notAttempted.Count > 0) sentences.Add($"Not attempted: {string.Join(", ", notAttempted)}.");
        sentences.Add($"Fix '{failed}', then push again.");

        return string.Join(" ", sentences);
    }

    static string EndSentence(string text) =>
        text.Length > 0 && ".!?".Contains(text[^1]) ? text : text + ".";

    private async Task<List<PluginPushTarget>> PreparePluginsForPushAsync(
        bool standaloneMode,
        Settings settings,
        PluginPackageMode pluginPackageMode,
        StandaloneParams standaloneParams,
        CancellationToken cancellationToken) =>
        standaloneMode
            ? PrepareStandalonePluginForPush(settings, standaloneParams)
            : await PrepareProjectPluginsForPushAsync(settings, pluginPackageMode, cancellationToken).ConfigureAwait(false);

    // Standalone mode has no solution file and no project context, so none of the discovery below applies:
    // --pluginFile named the artifact outright and ResolveStandalonePluginFilePath already resolved it.
    private List<PluginPushTarget> PrepareStandalonePluginForPush(Settings settings, StandaloneParams standaloneParams)
    {
        var pushPath = standaloneParams.DllPath;
        if (pushPath == null || !File.Exists(pushPath))
            throw new FlowlineException(ExitCode.NotFound, $"Plugin file not found: {settings.PluginFile}");

        // For a .nupkg the filename typically embeds its NuGet version (e.g. "MyPlugins.1.0.0.nupkg"), so
        // the filename minus extension does NOT match the assembly name inside the package (R2a) and
        // SyncSolutionFromPackageAsync's primary-assembly match would fail. Reflect it to get the real
        // name instead of guessing; the reflected list rides along so the push doesn't re-analyze it.
        // No solution file, so no project name to report — the artifact the user named is the identity.
        var projectName = Path.GetFileName(pushPath);

        PluginPushTarget target;
        if (IsPackagePush(pushPath))
        {
            var (assemblyName, reflectedAssemblies) = ResolveStandalonePackageAssemblyName(pushPath, Console);
            target = new PluginPushTarget(pushPath, assemblyName, projectName, reflectedAssemblies);
        }
        else
        {
            target = new PluginPushTarget(pushPath, Path.GetFileNameWithoutExtension(pushPath), projectName);
        }

        Console.Verbose($"Found {pushPath}");
        Console.Info($"[bold]{ConsolePath.FormatRelativePath(pushPath)}[/] found");

        return [target];
    }

    // Project mode discovers plugin projects through the solution file rather than a fixed Plugins/ folder
    // (R1/KD1), and resolves each one's real assembly name and output path by reflecting what it actually
    // built (R4/KD3, R2/KD2). Build strategy is per candidate project, not one solution-wide build: a
    // solution build would also run the WebResources npm target, which `--scope plugins` explicitly didn't
    // ask for and which PrepareWebResourcesForPushAsync already owns when it is in scope.
    private async Task<List<PluginPushTarget>> PrepareProjectPluginsForPushAsync(
        Settings settings,
        PluginPackageMode pluginPackageMode,
        CancellationToken cancellationToken)
    {
        var reader = new MsBuildSolutionReader();
        var solutionFile = reader.FindSolutionFile(RootFolder)
            ?? throw new FlowlineException(ExitCode.NotFound,
                "No solution file here — Flowline finds plugin projects through it. Run 'flowline clone' first.");

        var projects = await reader.ReadProjectsAsync(solutionFile, cancellationToken).ConfigureAwait(false);
        var candidates = PluginProjectResolver.EnumerateCandidates(projects, RootFolder);
        var targets = new List<PluginPushTarget>();

        foreach (var candidate in candidates)
        {
            var preFilterSkip = PluginProjectResolver.DescribePreFilterSkip(candidate.ProjectPath);
            if (preFilterSkip != null)
            {
                Console.Verbose($"Skipped {candidate.ProjectName} — {preFilterSkip}");
                continue;
            }

            var target = await PrepareProjectPluginForPushAsync(candidate, settings, pluginPackageMode, cancellationToken).ConfigureAwait(false);
            if (target != null) targets.Add(target);
        }

        // R8/AE9: zero plugin projects is a valid, common state (a WebResources-only solution) under the
        // default scope — only an explicit `--scope plugins`/`--scope assemblyonly` request means the user
        // specifically wanted a plugin push, so only that case is actually an error.
        if (targets.Count == 0 && settings.Scopes.Length > 0)
            throw new FlowlineException(ExitCode.NotFound,
                "No plugin project found — nothing the solution file references builds an IPlugin or CodeActivity type. " +
                "Run again with --verbose to see what got skipped.");

        return targets;
    }

    private async Task<PluginPushTarget?> PrepareProjectPluginForPushAsync(
        PluginProjectCandidate candidate,
        Settings settings,
        PluginPackageMode pluginPackageMode,
        CancellationToken cancellationToken)
    {
        var projectFolder = Path.GetDirectoryName(candidate.ProjectPath)!;
        var didBuild = false;

        if (settings.NoBuild)
            Console.Skip($"Build {candidate.ProjectName} — skipping (--no-build active)");
        else
        {
            didBuild = true;
            if (await DotNetUtils.BuildSolutionAsync(projectFolder, DotnetBuild.Release, _capture, cancellationToken) != 0)
                throw new FlowlineException(ExitCode.BuildFailed, $"{candidate.ProjectName} build failed — fix errors above.");
        }

        // R3: reflection read the output and found no plugin-bearing assembly, so this simply isn't a
        // plugin project. Not an error — but never silent either, or it reads exactly like "my plugin
        // didn't get registered". The case where reflection could NOT read the output is a different
        // animal and ResolvePluginAssembly throws on it rather than returning null here: the discovered
        // set is what the orphan sweeps treat as having local source, so guessing wrong deletes a live
        // registration. Undeterminable fails the push; only a definite verdict skips.
        var pluginsDll = PluginProjectResolver.ResolvePluginAssembly(candidate, note => Console.Verbose(note));
        if (pluginsDll == null)
        {
            Console.Verbose($"Skipped {candidate.ProjectName} — no IPlugin or CodeActivity type in its build output");
            return null;
        }

        // R1/KD1: a .nupkg anywhere under the build output root routes to the package deployment path
        // automatically under PluginPackageMode.Auto (U2/R2's default); Nupkg requires one and Dll opts
        // back into the classic path regardless. KD4: the mode is one per-solution setting, applied
        // identically to every discovered project.
        var pushPath = ResolvePluginPushPath(pluginsDll, candidate.BuildOutputRoot, pluginPackageMode);

        // NuGet's Pack step (produces the .nupkg the line above just found) has its own incremental check
        // that isn't aware of the recompiled DLL — if nothing else driving the nuspec changed (e.g. no new
        // commit, since versioning is git-derived via MinVer), `dotnet build` recompiles the assembly but
        // leaves a previously-packed, now-stale .nupkg in place. Detected using paths already resolved
        // above (no new path assumptions); self-heals with one forced rebuild rather than silently pushing
        // stale content or failing the push outright. Only meaningful right after a build we just ran.
        if (didBuild && IsPackagePush(pushPath) && File.GetLastWriteTimeUtc(pushPath) < File.GetLastWriteTimeUtc(pluginsDll))
        {
            Console.Warning($"[bold]{ConsolePath.FormatRelativePath(pushPath)}[/] is older than the assembly just built — " +
                "NuGet's Pack step didn't regenerate it (the package version likely didn't change). Forcing a full rebuild...");
            if (await DotNetUtils.BuildSolutionAsync(projectFolder, DotnetBuild.Release, _capture, cancellationToken, rebuild: true) != 0)
                throw new FlowlineException(ExitCode.BuildFailed, $"{candidate.ProjectName} build failed — fix errors above.");
            pushPath = ResolvePluginPushPath(pluginsDll, candidate.BuildOutputRoot, pluginPackageMode);
        }

        Console.Verbose($"Found {pushPath}");
        Console.Info($"[bold]{ConsolePath.FormatRelativePath(pushPath)}[/] found");

        // Read off the built artifact, not assumed from the folder or project name — that's what makes a
        // custom <AssemblyName> resolve (R4).
        return BuildProjectPushTarget(pushPath, Path.GetFileNameWithoutExtension(pluginsDll), candidate.ProjectName, Console);
    }

    /// <summary>Builds one project's push target, reflecting a <c>.nupkg</c> once if that's what it packed.</summary>
    /// <remarks>
    /// Project mode used to leave <see cref="PluginPushTarget.ReflectedAssemblies"/> null, which cost it
    /// twice: the push re-analyzed the same <c>.nupkg</c>, and — the part that mattered —
    /// <see cref="CollectPushedAssemblyNames"/> could never see a package's NON-primary assemblies, so
    /// every sibling project's orphan sweep read them as having no local source. The assembly name still
    /// comes off the built <c>.dll</c>, not the package: that is what resolves a custom
    /// <c>&lt;AssemblyName&gt;</c> (R4), and it is the name the package's primary-assembly match expects.
    /// </remarks>
    internal static PluginPushTarget BuildProjectPushTarget(string pushPath, string assemblyName, string projectName, IAnsiConsole console) =>
        IsPackagePush(pushPath)
            ? new PluginPushTarget(pushPath, assemblyName, projectName, new PluginAssemblyReader(console).AnalyzePackage(pushPath))
            : new PluginPushTarget(pushPath, assemblyName, projectName);

    private async Task<string?> PrepareWebResourcesForPushAsync(
        bool standaloneMode,
        Settings settings,
        StandaloneParams standaloneParams,
        CancellationToken cancellationToken)
    {
        if (standaloneMode) return standaloneParams.WebResourcesPath;

        // The solution file identifies the WebResources project, so its folder follows it wherever it moved.
        // A plugin-only / migrated repo may have no resolvable WebResources project though — push plugins
        // anyway (user decision B, push only): null means none is confidently identified, so skip web
        // resources and continue. A genuine tie still throws (propagates) rather than being swallowed.
        var layout = await SolutionFileLayout.LoadAsync(RootFolder, cancellationToken);
        if (layout.WebResourcesProjectPath is null)
        {
            Console.Warning("No WebResources project found — skipping web resources. Plugins are still pushed.");
            return null;
        }
        var webResourcesFolder = Path.GetDirectoryName(layout.WebResourcesProjectPath)!;
        var webResourcesSyncFolder = Path.Combine(webResourcesFolder, "dist");

        if (settings.NoBuild)
            Console.Skip("Build web resources - skipping (--no-build active)");
        else if (await DotNetUtils.BuildSolutionAsync(webResourcesFolder, DotnetBuild.Release, _capture, cancellationToken) != 0)
            throw new FlowlineException(ExitCode.BuildFailed, "Web resources build failed — fix errors above.");

        EnsureBuiltWebResources(webResourcesSyncFolder);

        Console.Verbose($"Found {webResourcesSyncFolder}");
        Console.Info($"[bold]{ConsolePath.FormatRelativePath(webResourcesSyncFolder)}[/] found");

        return webResourcesSyncFolder;
    }

    // Safety guard: an empty 'dist' makes push compute deletes for every remote web
    // resource. Refuse before that can happen — independent of --no-build.
    internal static void EnsureBuiltWebResources(string distFolder)
    {
        if (!Directory.Exists(distFolder) || !Directory.EnumerateFiles(distFolder, "*", SearchOption.AllDirectories).Any())
            throw new FlowlineException(ExitCode.NotFound,
                "No web resources in 'dist' — refusing to push, since this would remove every web resource in the solution. Build first, or check your source.");
    }

    private class StandaloneParams
    {
        public string? SolutionName { get; set; }
        public string? DllPath { get; set; }
        public string? WebResourcesPath { get; set; }
    }

    internal static bool IsStandaloneMode(Settings settings) =>
        !string.IsNullOrWhiteSpace(settings.PluginFile) || !string.IsNullOrWhiteSpace(settings.WebResources);

    internal static PushScope ResolveScope(Settings settings, bool standaloneMode)
    {
        PushScope scope;
        if (settings.Scopes.Length > 0)
        {
            scope = settings.Scopes.Aggregate(PushScope.None, (current, s) => current | s);
            if (scope.HasFlag(PushScope.AssemblyOnly) && scope.HasFlag(PushScope.Plugins))
                throw new FlowlineException(ExitCode.ValidationFailed, "--scope assemblyonly and --scope plugins are mutually exclusive.");
        }
        else if (standaloneMode)
        {
            scope = PushScope.None;
            if (!string.IsNullOrWhiteSpace(settings.PluginFile)) scope |= PushScope.Plugins;
            if (!string.IsNullOrWhiteSpace(settings.WebResources)) scope |= PushScope.WebResources;
        }
        else
        {
            scope = PushScope.All;
        }

        if (standaloneMode)
        {
            if ((scope.HasFlag(PushScope.Plugins) || scope.HasFlag(PushScope.AssemblyOnly)) && string.IsNullOrWhiteSpace(settings.PluginFile))
                throw new FlowlineException(ExitCode.ValidationFailed, "--scope plugins/assemblyonly requires --pluginFile.");
            if ((scope.HasFlag(PushScope.WebResources) || scope.HasFlag(PushScope.FormEvents)) && string.IsNullOrWhiteSpace(settings.WebResources))
                throw new FlowlineException(ExitCode.ValidationFailed, "--scope webresources/formevents requires --webresources.");
        }

        return scope;
    }

    internal static void ValidateStandaloneMode(Settings settings, string rootFolder)
    {
        if (File.Exists(Path.Combine(rootFolder, ProjectConfig.s_configFileName)))
            throw new FlowlineException(ExitCode.ValidationFailed, "--pluginFile and --webresources cannot be used inside a Flowline project folder. Use project mode or run standalone push from another folder.");
    }

    internal static string ResolveStandaloneSolutionName(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.Solution))
            return settings.Solution.Trim();

        throw new FlowlineException(ExitCode.ValidationFailed, "Solution name is required in standalone mode — pass it as the first argument.");
    }

    internal static string ResolveStandaloneEnvironmentUrl(Settings settings, DataverseConnector dataverseConnector)
    {
        if (!string.IsNullOrWhiteSpace(settings.DevUrl))
            return settings.DevUrl.Trim();

        var profile = dataverseConnector.GetCurrentResourceSpecificPacProfile();
        if (!string.IsNullOrWhiteSpace(profile?.Resource))
            return profile.Resource.Trim();

        throw new FlowlineException(ExitCode.ValidationFailed, "Dev URL is required in standalone mode — use --dev <URL> or select a resource-specific PAC auth profile.");
    }

    // R1/KD1: build output has a .dll for certain (regression-checked above). PluginPackageMode.Dll opts
    // out of the .nupkg search entirely (classic path, unconditionally). Auto and Nupkg both search the
    // whole build output root for a .nupkg — dotnet pack drops it at bin/Release/ directly, a sibling of
    // the net462/publish/ folder the .dll itself lives in, not alongside the .dll — and differ only in
    // what happens when none is found: Auto falls back to the .dll silently, Nupkg fails loudly (U2/R2).
    internal static string ResolvePluginPushPath(string dllPath, string buildOutputRoot, PluginPackageMode mode)
    {
        if (mode == PluginPackageMode.Dll)
            return dllPath;

        var nupkgPaths = Directory.Exists(buildOutputRoot)
            ? Directory.GetFiles(buildOutputRoot, "*.nupkg", SearchOption.AllDirectories)
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .ToArray()
            : [];

        if (nupkgPaths.Length == 0)
        {
            if (mode == PluginPackageMode.Nupkg)
                throw new FlowlineException(ExitCode.ValidationFailed,
                    "PluginPackageMode is \"Nupkg\" but no .nupkg was found under the build output — " +
                    "enable packaging in the Plugins project (GeneratePackageOnBuild), or switch PluginPackageMode to \"Auto\" or \"Dll\".");
            return dllPath;
        }

        // dotnet pack's filename embeds the NuGet version (e.g. "MyPlugins.1.0.0.nupkg") and neither
        // build nor pack cleans a previously-produced .nupkg with a different version out of bin/Release
        // first — a version bump without a clean build leaves both old and new side by side. Picking
        // FirstOrDefault (enumeration order is unspecified) risked silently pushing stale package content.
        if (nupkgPaths.Length > 1)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"Found {nupkgPaths.Length} .nupkg files under the build output — {string.Join(", ", nupkgPaths.Select(Path.GetFileName))}. " +
                "Run a clean build (delete bin/Release or drop --no-build) so only the current version's package remains.");

        return nupkgPaths[0];
    }

    // KD6: the single decision point shared by both project-mode auto-detection and standalone
    // --pluginFile — whichever path produced pluginsPushPath, its extension alone decides the route.
    internal static bool IsPackagePush(string pluginPushPath) =>
        string.Equals(Path.GetExtension(pluginPushPath), ".nupkg", StringComparison.OrdinalIgnoreCase);

    // R2a: standalone mode has no project context to anchor an assembly name to (project mode reads it off
    // the built assembly it discovered) — the .nupkg's own filename typically embeds its NuGet
    // version and does not match the reflected assembly name inside it. Reflect the package once here
    // to resolve the real name rather than guessing from the filename — the reflected list is also
    // returned so the caller can pass it straight into SyncSolutionFromPackageAsync's pre-reflected
    // overload instead of reflecting the same .nupkg a second time.
    internal static (string AssemblyName, List<PluginAssemblyMetadata> Assemblies) ResolveStandalonePackageAssemblyName(string nupkgPath, IAnsiConsole console)
    {
        var assemblies = new PluginAssemblyReader(console).AnalyzePackage(nupkgPath);

        if (assemblies.Count == 1)
            return (assemblies[0].Name, assemblies);

        if (assemblies.Count == 0)
            // R3a fires inside SyncSolutionFromPackageAsync regardless of what name is passed here —
            // any placeholder is fine, it never gets used to resolve a "primary" assembly.
            return (Path.GetFileNameWithoutExtension(nupkgPath), assemblies);

        throw new FlowlineException(ExitCode.ValidationFailed,
            $"--pluginFile package contains {assemblies.Count} plugin-bearing assemblies " +
            $"({string.Join(", ", assemblies.Select(a => a.Name))}) — standalone mode can't determine which " +
            "one is primary without project context. Push from the project instead.");
    }

    internal static string ResolveStandalonePluginFilePath(Settings settings)
    {
        var path = Path.GetFullPath(settings.PluginFile!);
        var ext = Path.GetExtension(path);

        // R2a/KD6: .nupkg is accepted here and routes to the exact same package entry point project mode
        // uses — no separate standalone implementation.
        if (!string.Equals(ext, ".dll", StringComparison.OrdinalIgnoreCase) && !string.Equals(ext, ".nupkg", StringComparison.OrdinalIgnoreCase))
            throw new FlowlineException(ExitCode.ValidationFailed, "--pluginFile must point to a .dll or .nupkg file.");

        if (!File.Exists(path))
            throw new FlowlineException(ExitCode.NotFound, $"Plugin file not found: {path}");

        return path;
    }

    internal static string ResolveStandaloneWebResourcesPath(Settings settings)
    {
        var path = Path.GetFullPath(settings.WebResources!);
        if (!Directory.Exists(path))
            throw new FlowlineException(ExitCode.NotFound, $"Web resources folder not found: {path}");

        return path;
    }

}
