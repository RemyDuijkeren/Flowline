using Flowline.Core;
using Flowline.Core.Configure;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Infrastructure;
using Flowline.Services;
using Flowline.Validation;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;

namespace Flowline.Commands;

/// <summary>
/// What every <c>settings</c> operation that reaches an environment has to work out first: which
/// environment, which solution, and which settings file.
/// </summary>
/// <remarks>
/// Extracted from the single <c>configure</c> leaf when it became a branch. Push, capture and the five
/// component kinds each answer these three questions the same way, so the answers live here rather than
/// three times over.
/// </remarks>
public abstract class SettingsCommandBase<TSettings>(
    IAnsiConsole console,
    DataverseConnector dataverseConnector,
    FlowlineRuntimeOptions runtimeOptions,
    ProfileResolutionService profileResolutionService,
    ILoggerFactory loggerFactory,
    SubprocessCapture capture,
    NuGetVersionClient nuGetVersionClient)
    : FlowlineCommand<TSettings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
    where TSettings : SettingsSettings
{
    protected DataverseConnector DataverseConnector { get; } = dataverseConnector;

    // The settings surface writes only what it is told to write, so there is no destructive scope to gate.
    // The vocabulary is deliberately empty of settings-specific specifiers rather than absent — recorded
    // here so nobody later invents a PROD confirmation and breaks every CI job already running these.
    protected override string[] ValidForceSpecifiers => FlowlineSettings.ConfigOnlyValidSpecifiers;

    /// <summary>
    /// Resolves a role through the shared role path and anything else as a URL.
    /// </summary>
    /// <remarks>
    /// The role branch keeps its environment-type guard — that check is what catches a stale role URL in
    /// <c>.flowline</c> pointed at the wrong environment, and inheriting it costs nothing. It updates the
    /// config in memory only; nothing here saves, so a dry run leaves the file untouched.
    ///
    /// The URL branch has no type guard on purpose: a URL names the environment outright, so there is no
    /// role to disagree with, and the base standalone helper would refuse Production outright.
    /// </remarks>
    protected async Task<(EnvironmentInfo Info, PacProfile Profile)> ResolveEnvironmentAsync(
        string target, EnvironmentRole? role, TSettings settings, CancellationToken ct)
    {
        if (role is not null)
            return await GetAndCheckEnvironmentInfoAsync(role.Value, null, settings, ct);

        var profile = await ProfileResolutionService.ResolveAsync(target, ct);
        var env = await Console.Status().FlowlineSpinner().StartAsync(
            $"Checking [bold]{Markup.Escape(target)}[/]...",
            _ => FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(target, profile, settings, settings.NoCache, ct));

        if (env is null)
            throw new FlowlineException(ExitCode.ConnectionFailed,
                $"Environment not found — check the URL '{target}' or your PAC login.");

        Console.Ok($"Env [bold]{Markup.Escape(env.DisplayName ?? target)}[/] ({env.EnvironmentUrl}) exists");
        return (env, profile);
    }

    /// <summary>
    /// Names the solution: from the artifact when one is given, from the project otherwise.
    /// </summary>
    /// <remarks>
    /// A stand-alone capture reads the unique name out of the artifact's own manifest rather than asking for
    /// it — the artifact already carries it, and a <c>--solution-name</c> that disagreed would read one
    /// solution's components and write them into another's settings file.
    /// </remarks>
    protected async Task<string> ResolveSolutionNameAsync(
        TSettings settings, bool standalone, string? artifactPath, EnvironmentInfo env, CancellationToken ct)
    {
        // Keyed off the artifact's own value, not the operation. Resolving the project's name here while the
        // PAC skeleton came from the artifact produced a file whose sections described two different
        // solutions — the same shape of bug drift shipped and fixed (DriftCommand.cs:41-46).
        if (artifactPath is not null)
        {
            var (path, isZip) = SettingsSupport.ResolveSolutionInput(artifactPath);
            var uniqueName = isZip
                ? DeployCommand.ReadArtifactSolutionManifest(path).UniqueName
                : SettingsSupport.ReadFolderSolutionUniqueName(path);

            if (!string.IsNullOrWhiteSpace(uniqueName))
            {
                if (standalone)
                    Console.Info(DeployCommand.BuildStandaloneIdentityNote(Path.GetFileName(path)));

                return uniqueName;
            }
        }

        if (!standalone)
            return (await GetAndCheckSolutionAsync(null, env.EnvironmentUrl!, includeManaged: null, settings, ct))
                .projectSolution.UniqueName;

        return settings.SolutionName
            ?? throw new FlowlineException(ExitCode.ConfigInvalid,
                "Couldn't tell which solution this is. Pass --solution-name.");
    }

    /// <summary>
    /// Decides which folder the convention is anchored to, then locates the file in it.
    /// </summary>
    /// <remarks>
    /// The anchor is the Dataverse solution folder — resolved from the solution file, never composed —
    /// because that is where <c>pac solution create-settings</c> output naturally lands, beside the
    /// <c>.cdsproj</c>. An explicit path or stand-alone mode has no project to resolve, so it anchors on the
    /// working directory instead. A capture that knows the role writes the role-named file even when a shared
    /// one is sitting there, so captured connection ids never land in the file every environment reads.
    /// </remarks>
    protected async Task<SettingsFileLocation> ResolveSettingsFileAsync(
        string? settingsFile, EnvironmentRole? role, bool standalone, string? artifactPath, bool forWriting,
        CancellationToken ct)
    {
        // A capture writes beside the artifact it was given. Anchoring on the project instead would put one
        // solution's captured values in another solution's settings file, since the identity above comes
        // from the artifact whenever there is one.
        if (artifactPath is not null)
        {
            var (path, isZip) = SettingsSupport.ResolveSolutionInput(artifactPath);
            var anchor = isZip ? Path.GetDirectoryName(path)! : path;
            return SettingsFileLocator.Locate(anchor, role?.ToString(), settingsFile, forWriting);
        }

        if (!string.IsNullOrWhiteSpace(settingsFile) || standalone)
            return SettingsFileLocator.Locate(Directory.GetCurrentDirectory(), role?.ToString(), settingsFile, forWriting);

        var layout = await SolutionFileLayout.LoadAsync(RootFolder, ct);
        return SettingsFileLocator.Locate(layout.DataverseSolutionFolder, role?.ToString(), settingsFile, forWriting);
    }

    /// <summary>Reads the solution's components, behind the spinner every operation shows.</summary>
    protected async Task<SolutionInventory> ReadInventoryAsync(
        IOrganizationServiceAsync2 service, string solutionName, CancellationToken ct) =>
        await Console.Status().FlowlineSpinner().StartAsync(
            $"Reading [bold]{Markup.Escape(solutionName)}[/] components...",
            _ => SolutionComponentInventory.ReadAsync(service, solutionName, ct));
}
