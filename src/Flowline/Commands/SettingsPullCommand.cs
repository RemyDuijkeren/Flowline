using System.ComponentModel;
using Flowline.Core;
using Flowline.Core.Configure;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Infrastructure;
using Flowline.Services;
using Flowline.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

/// <summary>Captures an environment's configuration into a settings file, or every configured one.</summary>
public class SettingsPullCommand(
    IAnsiConsole console,
    DataverseConnector dataverseConnector,
    FlowlineRuntimeOptions runtimeOptions,
    ProfileResolutionService profileResolutionService,
    ILoggerFactory loggerFactory,
    SubprocessCapture capture,
    NuGetVersionClient nuGetVersionClient)
    : SettingsCommandBase<SettingsPullCommand.Settings>(console, dataverseConnector, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
{
    public sealed class Settings : SettingsSettings
    {
        // Optional, unlike every other operation's target (R16). With none, the run captures every role the
        // project configures.
        [CommandArgument(0, "[target]")]
        [Description("Environment to capture: prod, uat, test, dev, or a URL. Omit to capture every configured environment")]
        public string? Target { get; set; }

        [CommandOption("--from <zip-or-folder>")]
        [Description("Read the solution's component list from this zip or unpacked folder instead of the project")]
        public string? From { get; set; }

        [CommandOption("--settings-file <path>")]
        [Description("Write this file instead of the role-named one beside the .cdsproj")]
        public string? SettingsFile { get; set; }
    }

    protected override bool IsStandalone(Settings settings) =>
        SettingsSupport.ResolveStandalone(settings.SolutionName, settings.From, Directory.GetCurrentDirectory());

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var standalone = IsStandalone(settings);
        var projectFound = FindFlowlineProjectRoot(Directory.GetCurrentDirectory()) is not null;

        var flagError = SettingsSupport.ValidateFlags(standalone, settings.SolutionName, projectFound);
        if (flagError is not null)
            throw new FlowlineException(ExitCode.ValidationFailed, flagError);

        var mode = settings.DryRun ? RunMode.DryRun : RunMode.Normal;

        return settings.Target is null
            ? await SweepAsync(settings, mode, projectFound, cancellationToken)
            : await CaptureOneAsync(settings, settings.Target, standalone, mode, sweeping: false, cancellationToken);
    }

    /// <summary>
    /// Captures every role the project configures, reporting each on its own line (R16, KTD13).
    /// </summary>
    /// <remarks>
    /// No role is exempt. The apply side already accepts DEV as a target, and a DEV branched from production
    /// carries connection references bound to connections that do not exist there, so a DEV file has a real
    /// consumer.
    ///
    /// Each environment is captured on its own so one bad environment does not lose the captures that
    /// succeeded, and an environment that cannot be reached is a failure rather than a skip: a configured
    /// role that no longer answers is something the operator has to fix, not something to pass over quietly.
    /// </remarks>
    async Task<int> SweepAsync(Settings settings, RunMode mode, bool projectFound, CancellationToken ct)
    {
        // The roles come from the project config, so outside a project there is nothing to enumerate.
        if (!projectFound)
            throw new FlowlineException(ExitCode.ValidationFailed,
                "No environment named, and no Flowline project to read the configured ones from. " +
                "Pass an environment: 'flowline settings pull <target>'.");

        if (!string.IsNullOrWhiteSpace(settings.SettingsFile))
            throw new FlowlineException(ExitCode.ValidationFailed, SettingsSupport.BuildSweepDestinationError());

        var roles = EnvironmentRoles.All
            .Where(role => !string.IsNullOrWhiteSpace(Config?.GetUrl(role)))
            .ToArray();

        if (roles.Length == 0)
            throw new FlowlineException(ExitCode.ConfigInvalid,
                "No environments are configured in .flowline, so there's nothing to capture. " +
                "Run 'flowline provision' or name an environment.");

        var failed = new List<string>();

        foreach (var role in roles)
        {
            try
            {
                await CaptureOneAsync(settings, role.ConfigKey(), standalone: false, mode, sweeping: true, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Named, not swallowed. The next role still gets its capture.
                failed.Add(role.UpperLabel());
                Console.Error($"{role.UpperLabel()} failed — {Markup.Escape(ex.Message)}");
                Logger.LogWarning(ex, "Sweep capture failed for {Role}", role.UpperLabel());
            }
        }

        if (failed.Count == 0)
        {
            Console.Done($"Captured {roles.Length} environment{(roles.Length == 1 ? "" : "s")}.");
            return (int)ExitCode.Success;
        }

        Console.Warning($"Couldn't reach {string.Join(", ", failed)} — the rest were captured. Fix those and re-run.");
        return (int)ExitCode.PartialSuccess;
    }

    /// <summary>Captures one environment into one settings file.</summary>
    async Task<int> CaptureOneAsync(
        Settings settings, string target, bool standalone, RunMode mode, bool sweeping, CancellationToken ct)
    {
        var role = DriftCommand.TryResolveRole(target);
        if (standalone && role is not null)
            throw new FlowlineException(ExitCode.ConfigInvalid, SettingsSupport.BuildStandaloneRoleError(target));

        var (env, profile) = await ResolveEnvironmentAsync(target, role, settings, ct);
        var solutionName = await ResolveSolutionNameAsync(settings, standalone, settings.From, env, ct);

        // A URL target carries no role, so the file it would write has no name (KTD17). Inference reads the
        // host label and the display name and returns nothing rather than guessing; it never infers
        // production from a name, because production comes only from the environment type Dataverse reports.
        var fileRole = role?.ToString();
        if (role is null && string.IsNullOrWhiteSpace(settings.SettingsFile))
        {
            var inferred = EnvironmentRoleInference.Infer(env.EnvironmentUrl, env.Type, env.DisplayName);
            if (inferred.Role is null)
                throw new FlowlineException(ExitCode.ValidationFailed, SettingsSupport.BuildUninferrableRoleError(target));

            fileRole = inferred.Role.Value.ToString();
            Console.Info($"Role [bold]{fileRole.ToUpperInvariant()}[/] (inferred from {inferred.Source})");
        }

        var location = await ResolveSettingsFileAsync(
            settings.SettingsFile, ParseFileRole(fileRole), standalone, settings.From, forWriting: true, ct);

        var (service, _) = await ConnectToDataverseAsync(DataverseConnector, env.EnvironmentUrl!, ct, profile);
        var inventory = await ReadInventoryAsync(service, solutionName, ct);

        // Resolved here, not up front: only a capture needs a solution on disk to generate the skeleton
        // from. Reading the project layout unconditionally made a stand-alone apply — which needs no
        // checkout at all — fail with "No solution file in ./".
        var (solutionPath, solutionIsZip) = settings.From is not null
            ? SettingsSupport.ResolveSolutionInput(settings.From)
            : (Path.Combine((await SolutionFileLayout.LoadAsync(RootFolder, ct)).DataverseSolutionFolder, "src"), false);

        return await WriteAsync(service, solutionPath, solutionIsZip, location, inventory, mode, sweeping, ct);
    }

    static EnvironmentRole? ParseFileRole(string? role) =>
        role is null ? null : EnvironmentRoles.TryParse(role);

    /// <summary>
    /// Builds the merged document and writes it.
    /// </summary>
    /// <remarks>
    /// PAC writes the skeleton to a temp file, never the real one: verified against pac 2.11.2, a
    /// <c>create-settings</c> run overwrites its target wholesale, destroying both filled-in values and
    /// every Flowline-owned section. The merge happens here and only the merged document is saved.
    /// </remarks>
    async Task<int> WriteAsync(
        IOrganizationServiceAsync2 service,
        string solutionPath,
        bool solutionIsZip,
        SettingsFileLocation location,
        SolutionInventory inventory,
        RunMode mode,
        bool sweeping,
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
                Console.Done(SettingsSupport.BuildPullDryRunMessage(display));
                return (int)ExitCode.Success;
            }

            SettingsFileReader.Save(result.Document, location.Path);

            // A sweep prints its own finish line over the whole run, so each environment reports as a step
            // rather than signing off as if the run were done.
            if (sweeping)
                Console.Ok($"Wrote {display}");
            else
                Console.Done($"Wrote {display}");

            return (int)ExitCode.Success;
        }, Logger);
    }
}
