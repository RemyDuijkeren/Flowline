using System.Text.RegularExpressions;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Utils;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;

namespace Flowline.Services;

// U5: the shared greenfield create sequence both `flowline init` and clone's (later) create-new
// path run — validate names, connect, resolve/create the publisher, SDK-create the empty unmanaged
// solution, pull + scaffold + build it exactly like a normal clone, then write the DEV role only
// once everything succeeded (R10/R16). Neither InitCommand nor CloneCommand calls the other; both
// call this (KTD1) — the create logic exists once.
public class SolutionCreateFlow(
    IAnsiConsole console,
    ProfileResolutionService profileResolutionService,
    DataverseConnector dataverseConnector,
    SolutionCreateService solutionCreateService,
    CreateSolutionService createSolutionService)
{
    /// <summary>Seam for testing — overrides ConsoleHelper.IsInteractive (global console capability
    /// check can't be driven by an injected TestConsole).</summary>
    internal Func<bool>? IsInteractiveOverride { get; set; }

    /// <summary>Seam for testing — overrides DataverseConnector.ConnectViaPacAsync (a real MSAL token
    /// acquisition with no mocking seam of its own).</summary>
    internal Func<PacProfile, string, CancellationToken, Task<IOrganizationServiceAsync2>>? ConnectOverride { get; set; }

    /// <summary>
    /// Runs the full greenfield create sequence against an already-resolved, already-eligible DEV
    /// environment (<see cref="CreateEnvironmentResolver"/> owns resolution and the DEV-only guard —
    /// this method trusts <paramref name="devEnv"/>). <paramref name="validatePackAndBuildAsync"/> is
    /// the caller's <c>FlowlineCommand.ValidatePackAndBuildAsync</c>, passed in because this class
    /// isn't a command and has no <c>TSettings</c> to close that generic method over.
    /// </summary>
    public async Task<int> RunAsync(
        EnvironmentInfo devEnv,
        string uniqueName,
        string? displayNameInput,
        string? publisherPrefixInput,
        string? publisherNameInput,
        string rootFolder,
        ProjectConfig config,
        Func<ProjectSolution, string, string, CancellationToken, Task<int?>> validatePackAndBuildAsync,
        CancellationToken cancellationToken = default)
    {
        // R14/R19: refuse before anything is written to Dataverse.
        SolutionNameValidator.EnsureSolutionUniqueName(uniqueName);
        var displayName = string.IsNullOrWhiteSpace(displayNameInput) ? Humanize(uniqueName) : displayNameInput;
        SolutionNameValidator.EnsureSolutionDisplayName(displayName);

        // R5/AE8: no flag, no TTY — error naming the flag before even connecting, so this path never
        // needs a Dataverse connection at all (mirrors CreateEnvironmentResolver's AE2 check).
        if (string.IsNullOrWhiteSpace(publisherPrefixInput) && !IsInteractive())
            throw new FlowlineException(ExitCode.ValidationFailed,
                "Publisher prefix is required — pass --publisher-prefix <prefix>, or run this interactively to pick one.");

        var orgService = await ConnectAsync(devEnv, cancellationToken);

        // R5: an explicit flag is used as-is; otherwise the interactive picker (existing publishers +
        // create-new) fills the gap — IsInteractive() was already confirmed true above when the flag
        // is missing, so this never blocks.
        var publisherPrefix = string.IsNullOrWhiteSpace(publisherPrefixInput)
            ? await PickPublisherPrefixAsync(orgService, cancellationToken)
            : publisherPrefixInput;
        SolutionNameValidator.EnsurePublisherPrefix(publisherPrefix);

        // R4/R15/R18: resolves/creates the publisher, refuses a unique-name collision, creates the
        // empty unmanaged solution. Once this returns, records exist in Dataverse — everything past
        // this point that fails is reported for manual cleanup instead of silently discarded (R16).
        var createResult = await console.Status().FlowlineSpinner().StartAsync(
            $"Creating solution [bold]{uniqueName}[/] in Dataverse...",
            _ => solutionCreateService.CreateAsync(orgService, uniqueName, displayName, publisherPrefix, publisherNameInput, cancellationToken));
        console.Ok($"Solution [bold]{uniqueName}[/] created ({(createResult.PublisherCreated ? "new" : "reused")} publisher '{createResult.PublisherPrefix}')");

        var projectSln = new ProjectSolution { UniqueName = uniqueName, IncludeManaged = false };
        var slnFolder = rootFolder;
        var cdsprojPath = Path.Combine(CreateSolutionService.ScaffoldedDataverseSolutionFolder(slnFolder), $"{uniqueName}.cdsproj");
        var slnFilePath = CreateSolutionService.ResolveSolutionFilePath(slnFolder, uniqueName);
        var slnFileName = Path.GetFileName(slnFilePath);

        try
        {
            // Pull + scaffold, identical to a clone of the solution just created (R7).
            await createSolutionService.CloneSolutionFromDataverseAsync(projectSln, slnFolder, cdsprojPath, devEnv.EnvironmentUrl!, cancellationToken);
            await createSolutionService.CreateSolutionFileAsync(slnFolder, slnFilePath, cdsprojPath, cancellationToken);

            var layout = await SolutionFileLayout.LoadAsync(slnFolder, cancellationToken);
            await createSolutionService.SetupPluginsProjectAsync(slnFolder, slnFilePath, uniqueName, layout, cancellationToken);
            var webresourcesFolder = await createSolutionService.SetupWebResourcesProjectAsync(slnFolder, slnFilePath, uniqueName, layout, cancellationToken);
            createSolutionService.SeedWebResourceDistFromSrc(slnFolder, webresourcesFolder, publisherPrefix, uniqueName);
            createSolutionService.ScaffoldRootGitignore(slnFolder);

            if (await validatePackAndBuildAsync(projectSln, CreateSolutionService.ScaffoldedDataverseSolutionFolder(slnFolder), slnFolder, cancellationToken) is { } exitCode)
            {
                ReportManualCleanup(createResult, devEnv);
                return exitCode;
            }

            await createSolutionService.ScaffoldAgentsFileAsync(slnFolder, uniqueName, slnFileName, cancellationToken);
            await createSolutionService.ScaffoldClaudeFileAsync(slnFolder, cancellationToken);
            await new DataverseContextGenerator(console).GenerateAsync(
                Path.Combine(CreateSolutionService.ScaffoldedDataverseSolutionFolder(slnFolder), "src"), uniqueName, rootFolder, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ReportManualCleanup(createResult, devEnv);
            throw;
        }

        // R10: the DEV role is written only once create + scaffold + build all succeeded.
        config.GetOrUpdateDevUrl(devEnv.EnvironmentUrl);
        config.Save(rootFolder);
        console.Ok($"DEV set to [bold]{devEnv.DisplayName}[/] ({devEnv.EnvironmentUrl})");

        return 0;
    }

    async Task<IOrganizationServiceAsync2> ConnectAsync(EnvironmentInfo devEnv, CancellationToken cancellationToken)
    {
        var profile = await profileResolutionService.ResolveAsync(devEnv.EnvironmentUrl!, cancellationToken);
        var connect = ConnectOverride ?? ((p, url, ct) => dataverseConnector.ConnectViaPacAsync(p, url, ct));

        IOrganizationServiceAsync2 orgService = null!;
        await console.Status().FlowlineSpinner().StartAsync("Connecting to Dataverse...", async _ =>
        {
            orgService = await connect(profile, devEnv.EnvironmentUrl!, cancellationToken);
        });

        console.Ok("Connected to Dataverse");
        return orgService;
    }

    // R5/AE4: existing publishers plus a create-new choice, mirroring CreateEnvironmentResolver's
    // tenant-wide environment picker (fetch under a spinner, prompt outside it).
    internal async Task<string> PickPublisherPrefixAsync(IOrganizationServiceAsync2 orgService, CancellationToken cancellationToken)
    {
        var publishers = await console.Status().FlowlineSpinner().StartAsync(
            "Checking existing publishers...",
            _ => solutionCreateService.ListPublishersAsync(orgService, cancellationToken));

        // (Label, Prefix) rather than SelectionPrompt<PublisherSummary?> — SelectionPrompt<T> requires
        // T : notnull, and the create-new choice has no publisher to hang off. A value-tuple choice
        // (a struct, so it's never null itself) carries a nullable Prefix instead.
        var createNew = (Label: "+ Create new publisher", Prefix: (string?)null);
        var choices = publishers
            .Select(p => (Label: $"{p.Prefix} — {p.FriendlyName}", Prefix: (string?)p.Prefix))
            .Append(createNew)
            .ToList();

        var prompt = new SelectionPrompt<(string Label, string? Prefix)>()
            .Title(FlowlineConsoleExtensions.Question("Pick a publisher:"))
            .UseConverter(c => c.Label)
            .AddChoices(choices);

        var selected = console.Prompt(prompt);
        if (selected.Prefix is not null)
            return selected.Prefix;

        return console.Prompt(new TextPrompt<string>(FlowlineConsoleExtensions.Question("New publisher prefix:")));
    }

    // R16: the publisher/solution already exist in Dataverse once this fires — named so the user can
    // go clean them up (or retry) instead of the tool silently discarding what it just wrote.
    void ReportManualCleanup(SolutionCreateResult createResult, EnvironmentInfo devEnv)
    {
        console.Error("Create failed after writing to Dataverse — clean up manually, or retry:");
        console.Info($"Publisher: [bold]{createResult.PublisherPrefix}[/] ({createResult.PublisherId}){(createResult.PublisherCreated ? " — created" : " — reused, not created")}");
        console.Info($"Solution: {createResult.SolutionId}");
        console.Info($"Environment: [bold]{devEnv.DisplayName}[/] ({devEnv.EnvironmentUrl})");
    }

    bool IsInteractive() => IsInteractiveOverride?.Invoke() ?? ConsoleHelper.IsInteractive(settings: null);

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
}
