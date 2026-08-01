using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Utils;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class CloneCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture, CreateSolutionService createSolutionService) :
    FlowlineCommand<CloneCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<solution>")]
        [Description("Solution to clone into this repo")]
        public string? Solution { get; set; }

        [CommandOption("--prod <URL>")]
        [Description("Production environment URL to clone solution from")]
        public string? ProdUrl { get; set; }

        [CommandOption("--uat <URL>")]
        [Description("UAT environment URL to clone solution from")]
        public string? UatUrl { get; set; }

        [CommandOption("--test <URL>")]
        [Description("Test environment URL to clone solution from")]
        public string? TestUrl { get; set; }

        [CommandOption("--dev <URL>")]
        [Description("Development environment URL to clone solution from")]
        public string? DevUrl { get; set; }

        [CommandOption("--managed [false]")]
        [Description("Include managed artifacts (--managed false resets to default)")]
        [DefaultValue(true)]
        public FlagValue<bool> IncludeManaged { get; set; } = null!;
    }

    protected override bool RequiresProject => false;
    protected override string[] ValidForceSpecifiers => FlowlineSettings.ConfigOnlyValidSpecifiers;

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Save all provided URLs to config first (no API calls, just config update + prompt on conflict)
        Config!.GetOrUpdateProdUrl(settings.ProdUrl, settings);
        Config!.GetOrUpdateUatUrl(settings.UatUrl, settings);
        Config!.GetOrUpdateTestUrl(settings.TestUrl, settings);
        Config!.GetOrUpdateDevUrl(settings.DevUrl, settings);

        var (sourceEnv, projectSln, solutionInfo) = await FindUnmanagedSourceAsync(settings, cancellationToken);
        Logger.LogInformation("source={EnvironmentUrl} solution={SolutionName}", sourceEnv.EnvironmentUrl, projectSln.UniqueName);

        // Before anything is written: the solution name becomes a C# namespace in the scaffolded plugin
        // project, and a keyword there produces source that doesn't compile.
        if (CreateSolutionService.DescribeCSharpKeywordCollision(projectSln.UniqueName) is { } keywordCollision)
            throw new FlowlineException(ExitCode.ValidationFailed, keywordCollision);

        Config.Save();
        Console.Verbose($"Project configuration saved to {ProjectConfig.s_configFileName}");

        var slnFolder = RootFolder;
        var solutionName = projectSln.UniqueName;

        var cdsprojPath = Path.Combine(CreateSolutionService.ScaffoldedDataverseSolutionFolder(slnFolder), $"{solutionName}.cdsproj");
        var slnFilePath = CreateSolutionService.ResolveSolutionFilePath(slnFolder, solutionName);
        var slnFileName = Path.GetFileName(slnFilePath);

        await CloneSolutionFromDataverseAsync(projectSln, slnFolder, cdsprojPath, sourceEnv.EnvironmentUrl!, settings, cancellationToken);
        await createSolutionService.CreateSolutionFileAsync(slnFolder, slnFilePath, cdsprojPath, cancellationToken);

        // The .cdsproj entry CreateSolutionFileAsync just wrote makes the solution file loadable, so the
        // scaffold-skip checks below can ask "is a plugin/WebResources project already registered under
        // any name or location" instead of only "does the default folder hold one" — a project whose
        // Plugins/WebResources project was legitimately moved/renamed (project-structure flexibility)
        // resolves here the same way push/sync/deploy already discover it. Loaded once and reused by both
        // setup calls, matching SolutionFileLayout's one-read contract.
        var layout = await SolutionFileLayout.LoadAsync(slnFolder, cancellationToken);
        await createSolutionService.SetupPluginsProjectAsync(slnFolder, slnFilePath, solutionName, layout, cancellationToken);
        var webresourcesFolder = await createSolutionService.SetupWebResourcesProjectAsync(slnFolder, slnFilePath, solutionName, layout, cancellationToken);
        createSolutionService.SeedWebResourceDistFromSrc(slnFolder, webresourcesFolder, solutionInfo.PublisherPrefix, projectSln.UniqueName);

        createSolutionService.ScaffoldRootGitignore(slnFolder);

        if (await ValidatePackAndBuildAsync(projectSln, CreateSolutionService.ScaffoldedDataverseSolutionFolder(slnFolder), slnFolder,
                buildRelease: true, skipBuild: false, cancellationToken) is { } exitCode)
        {
            return exitCode;
        }

        await createSolutionService.ScaffoldAgentsFileAsync(slnFolder, projectSln.UniqueName, slnFileName, cancellationToken);
        await createSolutionService.ScaffoldClaudeFileAsync(slnFolder, cancellationToken);
        await new DataverseContextGenerator(Console).GenerateAsync(
            Path.Combine(CreateSolutionService.ScaffoldedDataverseSolutionFolder(slnFolder), "src"), projectSln.UniqueName, RootFolder, cancellationToken);

        Console.Done("Cloned! Use 'push' and 'sync' to keep it in flow. ヽ(•‿•)ノ");
        return 0;
    }

    private async Task<(EnvironmentInfo sourceEnv, ProjectSolution projectSolution, SolutionInfo solutionInfo)> FindUnmanagedSourceAsync(Settings settings,
        CancellationToken cancellationToken)
    {
        foreach (var role in new[] { EnvironmentRole.Prod, EnvironmentRole.Uat, EnvironmentRole.Test, EnvironmentRole.Dev })
        {
            var configUrl = role switch
            {
                EnvironmentRole.Prod => Config!.ProdUrl,
                EnvironmentRole.Uat  => Config!.UatUrl,
                EnvironmentRole.Test => Config!.TestUrl,
                EnvironmentRole.Dev  => Config!.DevUrl,
                _ => null
            };
            if (string.IsNullOrEmpty(configUrl)) continue;

            var (env, _) = await GetAndCheckEnvironmentInfoAsync(role, null, settings, cancellationToken);
            var (sln, info) = await GetAndCheckSolutionAsync(
                settings.Solution, env.EnvironmentUrl!, settings.IncludeManaged.IsSet ? settings.IncludeManaged.Value : (bool?)null, settings, cancellationToken);

            if (info.IsManaged)
            {
                var label = role switch { EnvironmentRole.Prod => "Prod", EnvironmentRole.Uat => "UAT", EnvironmentRole.Test => "Test", _ => "Dev" };
                Console.MarkupLine($"[dim]{label} solution is managed — skipping[/]");
                continue;
            }

            return (env, sln, info);
        }

        throw new FlowlineException(ExitCode.NotFound, "No unmanaged environment found — provide a --dev, --test, --uat, or --prod URL with an unmanaged solution.");
    }

    private async Task CloneSolutionFromDataverseAsync(ProjectSolution projectSln, string slnFolder, string cdsprojPath, string environmentUrl,
        Settings settings, CancellationToken cancellationToken)
    {
        if (File.Exists(cdsprojPath))
        {
            // Unmanaged content is always present once cloned (Both is a superset), so only a
            // switch to managed can leave the local source stale — and only when it doesn't
            // already have the managed layer (e.g. a previous clone/sync already fetched Both).
            if (projectSln.IncludeManaged && !CreateSolutionService.HasManagedContent(CreateSolutionService.ScaffoldedDataverseSolutionFolder(slnFolder)))
                await PacUtils.SyncSolutionFromDataverseAsync(projectSln.UniqueName, CreateSolutionService.ScaffoldedDataverseSolutionFolder(slnFolder), environmentUrl, projectSln.IncludeManaged, _capture, cancellationToken);
            else
                Console.Skip("Solution already cloned — skipping");

            return;
        }

        if (Directory.Exists(CreateSolutionService.ScaffoldedDataverseSolutionFolder(slnFolder)))
            throw new FlowlineException(ExitCode.ConfigInvalid,
                CreateSolutionService.DescribeDataverseSolutionFolderWithoutCdsproj(CreateSolutionService.ScaffoldedDataverseSolutionFolder(slnFolder), Path.GetFileName(cdsprojPath)));

        Directory.CreateDirectory(slnFolder);

        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
        CommandResult result = await Console.Status().FlowlineSpinner().StartAsync(
            $"Cloning solution [bold]{projectSln.UniqueName}[/] from Dataverse...",
            ctx => Cli.Wrap(cmdName)
                      .WithArguments(args =>
                          args.AddIfNotNull(prefixArgs)
                              .Add("solution")
                              .Add("clone")
                              .Add("--name").Add(projectSln.UniqueName)
                              .Add("--environment").Add(environmentUrl)
                              .Add("--packagetype").Add(projectSln.IncludeManaged ? "Both" : "Unmanaged")
                              .Add("--outputDirectory").Add(slnFolder)
                              .Add("--async"))
                      .WithValidation(CommandResultValidation.None)
                      .WithCapture(_capture, ctx)
                      .ExecuteAsync(cancellationToken)
                      .Task);

        if (!result.IsSuccess)
            throw new FlowlineException(ExitCode.GeneralError, "Clone failed — check the environment and your PAC login.");

        // pac writes slnFolder/{SolutionName}/{SolutionName}.cdsproj plus src/. Flowline places that folder
        // under the role-based name and leaves the project file exactly as pac wrote it — the folder answers
        // "what kind of thing lives here", the file answers "which solution", and only the latter escapes
        // the repo.
        Directory.Move(Path.Combine(slnFolder, projectSln.UniqueName), CreateSolutionService.ScaffoldedDataverseSolutionFolder(slnFolder));
        CreateSolutionService.DeleteScaffoldedGitignore(CreateSolutionService.ScaffoldedDataverseSolutionFolder(slnFolder)); // superseded by the project-root .gitignore

        Console.Ok($"Solution [bold]{projectSln.UniqueName}[/] cloned in {FormatDuration(result.RunTime)}");
    }
}
