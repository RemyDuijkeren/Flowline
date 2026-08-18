using System.ComponentModel;
using System.Text.RegularExpressions;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Utils;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Flowline.Commands;

// The greenfield create command (KD1): validate names, connect, resolve/create the publisher,
// SDK-create the empty unmanaged solution, scaffold it exactly like a clone, then write the DEV role
// only once everything succeeded (R10/R16). RequiresFlowlineProject=false: like clone, init is how a Flowline
// project comes to exist, so there is no project yet to require.
public class InitCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService,
    ILoggerFactory loggerFactory, SubprocessCapture capture, CreateEnvironmentResolver createEnvironmentResolver,
    DataverseConnector dataverseConnector, SolutionCreateService solutionCreateService, ProjectScaffolder projectScaffolder, NuGetVersionClient nuGetVersionClient) :
    FlowlineCommand<InitCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
{
    /// <summary>Seam for testing — overrides DataverseConnector.ConnectViaPacAsync (a real MSAL token
    /// acquisition with no mocking seam of its own).</summary>
    internal Func<PacProfile, string, CancellationToken, Task<IOrganizationServiceAsync2>>? ConnectOverride { get; set; }

    /// <summary>Seam for testing — overrides the pack + build step, which shells out to real
    /// SolutionPackager and dotnet processes.</summary>
    internal Func<ProjectSolution, string, string, CancellationToken, Task<int?>>? ValidatePackAndBuildOverride { get; set; }

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

    protected override bool RequiresFlowlineProject => false;
    protected override string[] ValidForceSpecifiers => FlowlineSettings.ConfigOnlyValidSpecifiers;

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var name = await ResolveNameAsync(settings.Name, cancellationToken);

        // R14/R19: refuse a bad name before spending an interactive environment picker on it.
        SolutionNameValidator.EnsureSolutionUniqueName(name);

        var devEnv = await createEnvironmentResolver.ResolveCreateTargetAsync(settings.DevUrl, settings, cancellationToken);
        if (devEnv is null)
            return 0; // user chose "+ Create new environment" — resolver already emitted the provision advice

        var exitCode = await CreateSolutionAsync(devEnv, name, settings, RootFolder, Config!, cancellationToken);
        if (exitCode != 0)
            return exitCode;

        Console.Done("Created! Use 'push' and 'sync' to keep it in flow.");
        return 0;
    }

    /// <summary>
    /// The create sequence, against an already-resolved, already-eligible DEV environment
    /// (<see cref="CreateEnvironmentResolver"/> owns resolution and the DEV-only guard — this method
    /// trusts <paramref name="devEnv"/>). Takes <paramref name="rootFolder"/> and
    /// <paramref name="config"/> explicitly rather than reading <c>RootFolder</c>/<c>Config</c>, so it's
    /// callable — and testable — without running the base command pipeline that sets them.
    /// </summary>
    internal async Task<int> CreateSolutionAsync(
        EnvironmentInfo devEnv,
        string uniqueName,
        Settings settings,
        string rootFolder,
        ProjectConfig config,
        CancellationToken cancellationToken = default)
    {
        // R14/R19: refuse before anything is written to Dataverse.
        SolutionNameValidator.EnsureSolutionUniqueName(uniqueName);
        var displayName = string.IsNullOrWhiteSpace(settings.DisplayName) ? Humanize(uniqueName) : settings.DisplayName;
        SolutionNameValidator.EnsureSolutionDisplayName(displayName);

        // R5/AE8: no flag, no TTY — error naming the flag before even connecting, so this path never
        // needs a Dataverse connection at all (mirrors CreateEnvironmentResolver's AE2 check).
        if (string.IsNullOrWhiteSpace(settings.PublisherPrefix) && !IsInteractive())
            throw new FlowlineException(ExitCode.ValidationFailed,
                "Publisher prefix is required — pass --publisher-prefix <prefix>, or run this interactively to pick one.");

        var orgService = await ConnectAsync(devEnv.EnvironmentUrl!, cancellationToken);

        // R5: an explicit flag is used as-is; otherwise the interactive picker (existing publishers +
        // create-new) fills the gap — IsInteractive() was already confirmed true above when the flag
        // is missing, so this never blocks.
        var publisherPrefix = string.IsNullOrWhiteSpace(settings.PublisherPrefix)
            ? await PickPublisherPrefixAsync(orgService, cancellationToken)
            : settings.PublisherPrefix;
        SolutionNameValidator.EnsurePublisherPrefix(publisherPrefix);

        // R4/R15/R18: resolves/creates the publisher, refuses a unique-name collision, creates the
        // empty unmanaged solution. Once this returns, records exist in Dataverse — everything past
        // this point that fails is reported for manual cleanup instead of silently discarded (R16).
        var createResult = await Console.Status().FlowlineSpinner().StartAsync(
            $"Creating solution [bold]{uniqueName}[/] in Dataverse...",
            _ => solutionCreateService.CreateAsync(orgService, uniqueName, displayName, publisherPrefix, settings.PublisherName, cancellationToken));
        Console.Ok($"Solution [bold]{uniqueName}[/] created ({(createResult.PublisherCreated ? "new" : "reused")} publisher '{createResult.PublisherPrefix}')");

        var projectSln = new ProjectSolution { UniqueName = uniqueName, IncludeManaged = false };

        try
        {
            // Scaffold, identical to a clone of the solution just created (R7).
            var slnFileName = await projectScaffolder.ScaffoldProjectAsync(projectSln, rootFolder, devEnv.EnvironmentUrl!, publisherPrefix, cancellationToken);

            var validatePackAndBuild = ValidatePackAndBuildOverride ?? ((sln, dataverseSolutionFolder, slnFolder, ct) =>
                ValidatePackAndBuildAsync(sln, dataverseSolutionFolder, slnFolder, buildRelease: true, skipBuild: false, ct));

            if (await validatePackAndBuild(projectSln, ProjectScaffolder.ScaffoldedDataverseSolutionFolder(rootFolder), rootFolder, cancellationToken) is { } exitCode)
            {
                ReportManualCleanup(createResult, devEnv);
                return exitCode;
            }

            await projectScaffolder.ScaffoldDocsAsync(rootFolder, uniqueName, slnFileName, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportManualCleanup(createResult, devEnv);
            throw;
        }

        // R10: the DEV role is written only once create + scaffold + build all succeeded.
        // Record the created solution too, so a later push/sync can resolve it from .flowline.
        // settings is threaded in so the config-overwrite gate these two share can actually be
        // approved: without it, running init over an existing .flowline that names a different DEV URL
        // told you to pass --force config, and passing it changed nothing (HasForce is read off settings).
        config.GetOrUpdateSolution(uniqueName, includeManaged: false, settings);
        config.GetOrUpdateDevUrl(devEnv.EnvironmentUrl, settings);
        config.Save(rootFolder);
        Console.Ok($"DEV set to [bold]{devEnv.DisplayName}[/] ({devEnv.EnvironmentUrl})");

        return 0;
    }

    // The override branch skips the base helper's spinner, which is the point: it exists so tests never
    // reach a real MSAL token acquisition. The profile is still resolved through
    // ProfileResolutionService either way, so its own test overrides stay meaningful.
    async Task<IOrganizationServiceAsync2> ConnectAsync(string environmentUrl, CancellationToken cancellationToken) =>
        ConnectOverride is { } connect
            ? await connect(await ProfileResolutionService.ResolveAsync(environmentUrl, cancellationToken), environmentUrl, cancellationToken)
            : (await ConnectToDataverseAsync(dataverseConnector, environmentUrl, cancellationToken)).Connection;

    // R5/AE4: existing publishers plus a create-new choice, mirroring CreateEnvironmentResolver's
    // tenant-wide environment picker (fetch under a spinner, prompt outside it).
    internal async Task<string> PickPublisherPrefixAsync(IOrganizationServiceAsync2 orgService, CancellationToken cancellationToken)
    {
        var publishers = await Console.Status().FlowlineSpinner().StartAsync(
            "Checking existing publishers...",
            _ => solutionCreateService.ListPublishersAsync(orgService, cancellationToken));

        // (Label, Prefix) rather than SelectionPrompt<PublisherSummary?> — SelectionPrompt<T> requires
        // T : notnull, and the create-new choice has no publisher to hang off. A value-tuple choice
        // (a struct, so it's never null itself) carries a nullable Prefix instead.
        var createNew = (Label: "[italic]+ Create new publisher[/]", Prefix: (string?)null);
        var choices = publishers
            .Select(p => (Label: $"{p.Prefix} — {p.FriendlyName}", Prefix: (string?)p.Prefix))
            .Append(createNew)
            .ToList();

        var prompt = new SelectionPrompt<(string Label, string? Prefix)>()
            .Title(FlowlineConsoleExtensions.Question("Pick a publisher:"))
            .UseConverter(c => c.Label)
            .AddChoices(choices);

        var selected = await Console.PromptAsync(prompt, cancellationToken);
        if (selected.Prefix is not null)
            return selected.Prefix;

        return await Console.PromptAsync(new TextPrompt<string>(FlowlineConsoleExtensions.Question("New publisher prefix:")), cancellationToken);
    }

    // R16: the publisher/solution already exist in Dataverse once this fires — named so the user can
    // go clean them up (or retry) instead of the tool silently discarding what it just wrote.
    void ReportManualCleanup(SolutionCreateResult createResult, EnvironmentInfo devEnv)
    {
        Console.Error("Create failed after writing to Dataverse — clean up manually, or retry:");
        Console.Info($"Publisher: [bold]{createResult.PublisherPrefix}[/] ({createResult.PublisherId}){(createResult.PublisherCreated ? " — created" : " — reused, not created")}");
        Console.Info($"Solution: {createResult.SolutionId}");
        Console.Info($"Environment: [bold]{devEnv.DisplayName}[/] ({devEnv.EnvironmentUrl})");
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

    // KTD3(a): split on underscores and camelCase/acronym boundaries into spaced words, keeping
    // consecutive-capital acronym runs together. Two separate insertions, not one regex:
    //   1. lower/digit -> upper   (MySolution -> My|Solution)
    //   2. upper -> upper+lower   (APIGateway -> API|Gateway, not API|G|ateway)
    // Order matters — (2) must see the already-underscore-replaced string but doesn't depend on (1)'s
    // insertions, since (1) never creates a new upper-upper+lower boundary.
    internal static string Humanize(string uniqueName)
    {
        var spaced = uniqueName.Replace('_', ' ');
        spaced = Regex.Replace(spaced, "(?<=[a-z0-9])(?=[A-Z])", " ");
        spaced = Regex.Replace(spaced, "(?<=[A-Z])(?=[A-Z][a-z])", " ");
        return Regex.Replace(spaced, @"\s+", " ").Trim();
    }

    bool IsInteractive() => Console.Profile.Capabilities.Interactive;
}
