using System.ComponentModel;
using System.Xml.Linq;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Configure;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Infrastructure;
using Flowline.Services;
using Flowline.Utils;
using Flowline.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

/// <summary>Applies a per-environment settings file to a Dataverse environment, or captures one.</summary>
public class ConfigureCommand(
    IAnsiConsole console,
    DataverseConnector dataverseConnector,
    FlowlineRuntimeOptions runtimeOptions,
    ProfileResolutionService profileResolutionService,
    ILoggerFactory loggerFactory,
    SubprocessCapture capture,
    NuGetVersionClient nuGetVersionClient)
    : FlowlineCommand<ConfigureCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<target>")]
        [Description("Target environment: prod, uat, test, dev, or a URL")]
        public string Target { get; set; } = null!;

        [CommandOption("--settings-file <path>")]
        [Description("Apply this settings file instead of the one beside the .cdsproj")]
        public string? SettingsFile { get; set; }

        [CommandOption("--solution-name <name>")]
        [Description("Solution unique name — required outside a Flowline project, rejected inside one")]
        public string? SolutionName { get; set; }

        // No [DefaultValue] here, unlike the FlagValue<bool> options on clone and sync: Spectre assigns the
        // declared default straight into FlagValue<T>.Value, so a `false` default on a FlagValue<string>
        // throws InvalidCastException while binding, before the command ever runs. An unset flag is
        // IsSet == false with a null Value, which is exactly what this needs.
        [CommandOption("--pull [zip-or-folder]")]
        [Description("Capture the environment's configuration into the settings file instead of applying it")]
        public FlagValue<string> Pull { get; set; } = null!;

        [CommandOption("--dry-run")]
        [Description("Report what would change and write nothing")]
        [DefaultValue(false)]
        public bool DryRun { get; set; } = false;
    }

    // KTD10: configure writes only what the file declares, so there is no destructive scope to gate. The
    // vocabulary is deliberately empty of configure-specific specifiers rather than absent — recorded here so
    // nobody later invents a PROD confirmation and breaks every CI job already running the command.
    protected override string[] ValidForceSpecifiers => FlowlineSettings.ConfigOnlyValidSpecifiers;

    protected override bool IsStandalone(Settings settings) =>
        ResolveStandalone(settings.SolutionName, settings.Pull.Value, Directory.GetCurrentDirectory());

    /// <summary>
    /// Stand-alone is a flag naming the solution plus no project to read one from (KTD3).
    /// </summary>
    /// <remarks>
    /// Same shape as <see cref="DeployCommand.ResolveStandalone"/> — a flag plus the absence of a project —
    /// rather than push's flag-only-then-throw form, so the two precedents do not diverge further. Pure so
    /// the rule is testable without a checkout.
    ///
    /// Either flag qualifies: <c>--solution-name</c> states the name outright, and <c>--pull &lt;zip|folder&gt;</c>
    /// names an artifact that carries it. Without this, a stand-alone pull with only the artifact would be
    /// judged project mode and stop at "No Flowline project found" — the artifact it was handed ignored.
    /// </remarks>
    internal static bool ResolveStandalone(string? solutionName, string? pullPath, string startDir) =>
        (!string.IsNullOrWhiteSpace(solutionName) || !string.IsNullOrWhiteSpace(pullPath))
        && FindFlowlineProjectRoot(startDir) is null;

    /// <summary>Rejects flag combinations that cannot mean anything, each naming its own flags.</summary>
    /// <remarks>
    /// One error per problem, never a combined "invalid mode" message: an agent needs to know which flag to
    /// change, and a single catch-all tells it nothing.
    /// </remarks>
    internal static string? ValidateFlags(bool pullRequested, string? settingsFile, bool standalone, string? solutionName, bool projectFound)
    {
        if (pullRequested && !string.IsNullOrWhiteSpace(settingsFile))
            return "--pull and --settings-file can't be used together: --settings-file names a file to apply, and --pull writes one.";

        if (!standalone && !string.IsNullOrWhiteSpace(solutionName) && projectFound)
            return "--solution-name only applies outside a Flowline project. Inside one the solution comes from the project — remove the flag.";

        return null;
    }

    /// <summary>The message a role keyword earns when there is no project to resolve it from.</summary>
    internal static string BuildStandaloneRoleError(string target) =>
        $"'{target}' is a role name, and roles resolve from .flowline — there's no Flowline project here. " +
        "Pass the environment URL instead.";

    /// <summary>Names the environment and file a run resolved, before anything is written.</summary>
    /// <remarks>
    /// The only wrong-file-wrong-environment guard on an unattended run: there is no confirmation (KTD10),
    /// and the all-skipped Inconclusive exit only fires when nothing matched at all.
    /// </remarks>
    internal static string BuildResolutionNote(string environment, SettingsFileLocation location) =>
        $"Applying [bold]{Markup.Escape(Path.GetFileName(location.Path))}[/] " +
        $"({SourceLabel(location.Source)}) to [bold]{Markup.Escape(environment)}[/]";

    static string SourceLabel(SettingsFileSource source) => source switch
    {
        SettingsFileSource.Explicit => "--settings-file",
        SettingsFileSource.RoleConvention => "role convention",
        _ => "shared fallback",
    };

    /// <summary>Dry-run completion wording, in the statement form the other commands use.</summary>
    internal static string BuildDryRunCompleteMessage(string environment) =>
        $"Dry run complete — {Markup.Escape(environment)} is untouched. Run without --dry-run to apply.";

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var standalone = IsStandalone(settings);
        var projectFound = FindFlowlineProjectRoot(Directory.GetCurrentDirectory()) is not null;
        var pullRequested = settings.Pull.IsSet;

        var flagError = ValidateFlags(pullRequested, settings.SettingsFile, standalone, settings.SolutionName, projectFound);
        if (flagError is not null)
            throw new FlowlineException(ExitCode.ValidationFailed, flagError);

        // A role keyword has nothing to resolve against outside a project: Config is a bare ProjectConfig
        // there, so every role would fall through to a config-shaped "URL is required" pointing at a
        // .flowline that was never expected to exist.
        var role = DriftCommand.TryResolveRole(settings.Target);
        if (standalone && role is not null)
            throw new FlowlineException(ExitCode.ConfigInvalid, BuildStandaloneRoleError(settings.Target));

        // The artifact route keys off the flag's own value, not the mode — drift shipped the opposite and
        // silently compared the checkout when `--path` was passed inside a project (DriftCommand.cs:41-46).
        // A named solution wins wherever it is named.
        var artifactPath = settings.Pull.Value;
        var (solutionPath, solutionIsZip) = artifactPath is not null
            ? ResolveSolutionInput(artifactPath)
            : (await SolutionFileLayout.LoadAsync(RootFolder, cancellationToken)).DataverseSolutionFolder is var folder
                ? (Path.Combine(folder, "src"), false)
                : default;

        var (env, profile) = await ResolveEnvironmentAsync(settings.Target, role, settings, cancellationToken);

        var solutionName = await ResolveSolutionNameAsync(settings, standalone, artifactPath, env, cancellationToken);

        var location = await ResolveSettingsFileAsync(settings, role, standalone, artifactPath, cancellationToken);
        if (!pullRequested && !location.Exists)
            throw new FlowlineException(ExitCode.NotFound,
                $"No settings file at '{ConsolePath.FormatRelativePath(location.Path, RootFolder)}'. " +
                $"Run 'flowline configure {settings.Target} --pull' to create one.");

        var mode = settings.DryRun ? RunMode.DryRun : RunMode.Normal;
        var (service, _) = await ConnectToDataverseAsync(dataverseConnector, env.EnvironmentUrl!, cancellationToken, profile);

        var inventory = await Console.Status().FlowlineSpinner().StartAsync(
            $"Reading [bold]{Markup.Escape(solutionName)}[/] components...",
            _ => SolutionComponentInventory.ReadAsync(service, solutionName, cancellationToken));

        if (pullRequested)
            return await PullAsync(service, solutionPath, solutionIsZip, location, inventory, mode, env, cancellationToken);

        Console.Info(BuildResolutionNote(env.DisplayName ?? settings.Target, location));

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(service, SettingsFileReader.Read(location.Path), inventory, mode, cancellationToken);

        Report(outcome, mode, env.DisplayName ?? settings.Target);

        return (int)outcome.ExitCode;
    }

    /// <summary>
    /// Captures the environment's configuration into the settings file (R12).
    /// </summary>
    /// <remarks>
    /// PAC writes the skeleton to a temp file, never the real one: verified against pac 2.11.2, a
    /// <c>create-settings</c> run overwrites its target wholesale, destroying both filled-in values and
    /// every Flowline-owned section. The merge happens here and only the merged document is saved.
    /// </remarks>
    async Task<int> PullAsync(
        IOrganizationServiceAsync2 service,
        string solutionPath,
        bool solutionIsZip,
        SettingsFileLocation location,
        SolutionInventory inventory,
        RunMode mode,
        EnvironmentInfo env,
        CancellationToken ct)
    {
        var skeletonPath = Path.Combine(Directory.CreateTempSubdirectory("flowline-pull-").FullName, "settings.json");

        return await DriftCommand.RunInTempDirAsync(Path.GetDirectoryName(skeletonPath)!, async () =>
        {
            await PacUtils.CreateSettingsAsync(solutionPath, solutionIsZip, skeletonPath, _capture, ct);

            var skeleton = SettingsFileReader.Read(skeletonPath);
            var existing = location.Exists ? SettingsFileReader.Read(location.Path) : null;

            var result = await new ConfigurePullService().BuildAsync(service, skeleton, existing, inventory, ct);

            var display = ConsolePath.FormatRelativePath(location.Path, RootFolder);

            foreach (var added in result.Added)
                Console.Info($"New: {Markup.Escape(added)}");

            foreach (var vanished in result.Vanished)
                Console.Warning($"{Markup.Escape(vanished)} is no longer in the solution — kept in the file, not applied.");

            foreach (var placeholder in result.Placeholders)
                Console.Warning($"{Markup.Escape(placeholder)} is a secret Flowline won't read — " +
                                $"'{ConfigurePullService.SecretPlaceholder}' was written, fill it in by hand.");

            if (mode.IsReportOnly())
            {
                Console.Done(BuildDryRunCompleteMessage(env.DisplayName ?? location.Path));
                return (int)ExitCode.Success;
            }

            SettingsFileReader.Save(result.Document, location.Path);
            Console.Done($"Wrote {display}");

            return (int)ExitCode.Success;
        }, Logger);
    }

    /// <summary>Decides whether a pull's argument names a packed zip or an unpacked folder.</summary>
    internal static (string Path, bool IsZip) ResolveSolutionInput(string path)
    {
        var full = Path.GetFullPath(path);

        if (Directory.Exists(full)) return (full, false);
        if (File.Exists(full)) return (full, true);

        throw new FlowlineException(ExitCode.NotFound,
            $"No solution zip or folder at '{path}'.");
    }

    /// <summary>
    /// Names the solution: from the artifact when one is given, from the project otherwise.
    /// </summary>
    /// <remarks>
    /// A stand-alone pull reads the unique name out of the artifact's own manifest rather than asking for it
    /// (R12) — the artifact already carries it, and a <c>--solution-name</c> that disagreed would read one
    /// solution's components and write them into another's settings file.
    /// </remarks>
    async Task<string> ResolveSolutionNameAsync(
        Settings settings, bool standalone, string? artifactPath, EnvironmentInfo env, CancellationToken ct)
    {
        if (!standalone)
            return (await GetAndCheckSolutionAsync(null, env.EnvironmentUrl!, includeManaged: null, settings, ct))
                .projectSolution.UniqueName;

        if (artifactPath is not null)
        {
            var (path, isZip) = ResolveSolutionInput(artifactPath);
            var uniqueName = isZip
                ? DeployCommand.ReadArtifactSolutionManifest(path).UniqueName
                : ReadFolderSolutionUniqueName(path);

            if (!string.IsNullOrWhiteSpace(uniqueName))
            {
                Console.Info(DeployCommand.BuildStandaloneIdentityNote(Path.GetFileName(path)));
                return uniqueName;
            }
        }

        return settings.SolutionName
            ?? throw new FlowlineException(ExitCode.ConfigInvalid,
                "Couldn't tell which solution this is. Pass --solution-name.");
    }

    /// <summary>Reads a solution's unique name from an unpacked folder's manifest.</summary>
    /// <remarks>
    /// <see cref="DeployCommand.ReadArtifactSolutionManifest"/> is zip-only and throws on a directory, so an
    /// unpacked folder reads <c>Other/Solution.xml</c> and hands it to the same parser that helper uses.
    /// </remarks>
    internal static string? ReadFolderSolutionUniqueName(string folder)
    {
        var manifest = Path.Combine(folder, "Other", "Solution.xml");
        if (!File.Exists(manifest))
            throw new FlowlineException(ExitCode.NotFound,
                $"No solution manifest at '{manifest}' — is '{folder}' an unpacked solution folder?");

        return DeployCommand.ParseSolutionManifest(XDocument.Load(manifest)).UniqueName;
    }

    /// <summary>
    /// Resolves a role through the shared role path and anything else as a URL.
    /// </summary>
    /// <remarks>
    /// The role branch keeps its environment-type guard (R5c) — that check is what catches a stale role URL
    /// in <c>.flowline</c> pointed at the wrong environment, and inheriting it costs nothing. It updates the
    /// config in memory only; nothing here saves, so a dry run leaves the file untouched.
    ///
    /// The URL branch has no type guard on purpose: a URL names the environment outright, so there is no
    /// role to disagree with, and the base standalone helper would refuse Production outright.
    /// </remarks>
    async Task<(EnvironmentInfo Info, PacProfile Profile)> ResolveEnvironmentAsync(
        string target, EnvironmentRole? role, Settings settings, CancellationToken ct)
    {
        if (role is not null)
            return await GetAndCheckEnvironmentInfoAsync(role.Value, null, settings, ct);

        var profile = await ProfileResolutionService.ResolveAsync(target, ct);
        var env = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking [bold]{Markup.Escape(target)}[/]...",
            _ => FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(target, profile, settings, ct));

        if (env is null)
            throw new FlowlineException(ExitCode.ConnectionFailed,
                $"Environment not found — check the URL '{target}' or your PAC login.");

        Console.Ok($"Env [bold]{Markup.Escape(env.DisplayName ?? target)}[/] ({env.EnvironmentUrl}) exists");
        return (env, profile);
    }

    /// <summary>
    /// Decides which folder the convention is anchored to, then locates the file in it.
    /// </summary>
    /// <remarks>
    /// The anchor is the Dataverse solution folder — resolved from the solution file, never composed —
    /// because that is where <c>pac solution create-settings</c> output naturally lands, beside the
    /// <c>.cdsproj</c>. An explicit path or stand-alone mode has no project to resolve, so it anchors on the
    /// working directory instead.
    /// </remarks>
    async Task<SettingsFileLocation> ResolveSettingsFileAsync(
        Settings settings, EnvironmentRole? role, bool standalone, string? artifactPath, CancellationToken ct)
    {
        // A stand-alone pull writes beside the artifact it was given (R12), not beside the working
        // directory — the artifact is the only thing about that run with a location of its own.
        if (standalone && artifactPath is not null)
        {
            var (path, isZip) = ResolveSolutionInput(artifactPath);
            var anchor = isZip ? Path.GetDirectoryName(path)! : path;
            return SettingsFileLocator.Locate(anchor, role?.ToString(), settings.SettingsFile);
        }

        if (!string.IsNullOrWhiteSpace(settings.SettingsFile) || standalone)
            return SettingsFileLocator.Locate(Directory.GetCurrentDirectory(), role?.ToString(), settings.SettingsFile);

        var layout = await SolutionFileLayout.LoadAsync(RootFolder, ct);
        return SettingsFileLocator.Locate(layout.DataverseSolutionFolder, role?.ToString(), settings.SettingsFile);
    }

    /// <summary>
    /// Reports every component on its own line, then the one summary line (R11a).
    /// </summary>
    /// <remarks>
    /// Values never appear (R10a); names and states always do. A run whose only signal is an exit code leaves
    /// an operator, or an agent, nothing to act on.
    /// </remarks>
    void Report(ApplyOutcome outcome, RunMode mode, string environment)
    {
        foreach (var component in outcome.Components)
        {
            var name = Markup.Escape(component.Name);
            switch (component.Outcome)
            {
                case ComponentOutcomeKind.Applied when mode.IsReportOnly():
                    Console.Info($"Would change [bold]{name}[/]");
                    break;
                case ComponentOutcomeKind.Applied:
                    Console.Ok(component.WasSuspended
                        ? $"[bold]{name}[/] activated — it was Suspended, and the platform may suspend it again"
                        : $"[bold]{name}[/] set");
                    break;
                case ComponentOutcomeKind.Unchanged:
                    Console.Verbose($"{name} already matches");
                    break;
                case ComponentOutcomeKind.Skipped:
                    Console.Skip(Markup.Escape(component.Detail ?? $"{component.Name} skipped"));
                    break;
                case ComponentOutcomeKind.Failed:
                    Console.Error(Markup.Escape(component.Detail ?? $"{component.Name} failed"));
                    break;
            }
        }

        foreach (var undeclared in outcome.Undeclared)
            Console.Verbose($"Not declared in the settings file: {Markup.Escape(undeclared)}");

        if (outcome.Undeclared.Count > 0)
            Console.Warning($"{outcome.Undeclared.Count} solution component(s) aren't declared in this file — run with --verbose to list them.");

        Console.Info(outcome.SummaryLine());

        if (mode.IsReportOnly())
            Console.Done(BuildDryRunCompleteMessage(environment));
    }
}
