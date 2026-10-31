using Flowline.Core;
using Flowline.Core.Config;
using Flowline.Core.Configure;
using Flowline.Core.Console;
using Flowline.Core.Dataverse;
using Flowline.Core.Environments;
using Flowline.Core.Models;
using Flowline.Core.Solutions;
using Flowline.Core.Validation;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Settings;
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
    CommandServices services,
    DataverseConnector dataverseConnector)
    : FlowlineCommand<TSettings>(services)
    where TSettings : SettingsSettings
{
    protected DataverseConnector DataverseConnector { get; } = dataverseConnector;

    // The settings surface writes only what it is told to write, so there is no destructive scope to gate.
    // The vocabulary is deliberately empty of settings-specific specifiers rather than absent — recorded
    // here so nobody later invents a PROD confirmation and breaks every CI job already running these.
    protected override string[] ValidForceSpecifiers => FlowlineSettings.ConfigOnlyValidSpecifiers;

    /// <summary>
    /// Works out whether this run has a project behind it, and rejects a solution name the project contradicts.
    /// </summary>
    /// <remarks>
    /// Every operation on this branch asks the same two questions before it touches anything, and answers
    /// them the same way. Kept here rather than at each leaf so a later operation cannot answer them
    /// slightly differently.
    /// </remarks>
    protected (bool Standalone, bool ProjectFound) ResolveProjectMode(TSettings settings)
    {
        var standalone = IsStandalone(settings);
        var projectFound = FindFlowlineProjectRoot(Directory.GetCurrentDirectory()) is not null;

        if (projectFound)
            SettingsSupport.ValidateSolutionName(settings.SolutionName, Config.Solution?.UniqueName);

        return (standalone, projectFound);
    }

    /// <summary>Reads a role out of the target, or fails before anything resolves it.</summary>
    /// <remarks>
    /// The component operations pass their name and the invocation, so a swapped order is answered with
    /// the line to run.
    /// </remarks>
    protected static EnvironmentRole? ResolveRoleOrThrow(
        string target, bool standalone, string? name = null, IReadOnlyList<string>? args = null) =>
        EnvironmentTarget.Check(target, standalone,
            () => SettingsSupport.BuildNotAnEnvironmentError(target, name, args ?? []));

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
            return (await GetAndCheckSolutionAsync(null, env.EnvironmentUrl!, includeManaged: null, cancellationToken: ct))
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
