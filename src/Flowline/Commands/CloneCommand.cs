using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Flowline.Core;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Utils;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class CloneCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture) :
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

        [CommandOption("--managed")]
        [Description("Include managed artifacts (--managed false resets to default)")]
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
        Logger.LogInformation("source={EnvironmentUrl} solution={SolutionName}", sourceEnv.EnvironmentUrl, projectSln.Name);

        Config.Save();
        Console.Verbose($"Project configuration saved to {ProjectConfig.s_configFileName}");

        var slnFolder = Path.Combine(RootFolder, AllSolutionsFolderName, projectSln.Name);
        var cdsprojPath = Path.Combine(PackageFolder(slnFolder), $"{PackageName}.cdsproj");
        var slnFilePath = Path.Combine(slnFolder, $"{projectSln.Name}.sln");

        await CloneSolutionFromDataverseAsync(projectSln, slnFolder, cdsprojPath, sourceEnv.EnvironmentUrl!, settings, cancellationToken);
        await CreateSolutionFileAsync(projectSln, slnFolder, slnFilePath, cdsprojPath, settings, cancellationToken);
        await SetupPluginsProjectAsync(slnFolder, settings, cancellationToken);
        await SetupWebResourcesProjectAsync(slnFolder, slnFilePath, settings, cancellationToken);
        SeedWebResourceDistFromSrc(slnFolder, solutionInfo.PublisherPrefix, projectSln.Name, settings);

        ScaffoldRootGitignore();

        // Pack the solution in pac to validate it
        Logger.LogInformation("Validating pack: {SolutionName}", projectSln.Name);
        var artifactsFolder = Path.Combine(slnFolder, "artifacts");
        Directory.CreateDirectory(artifactsFolder);
        if (await PacUtils.PackSolutionAsync(projectSln, PackageFolder(slnFolder), artifactsFolder, false, _capture, cancellationToken) != 0) return (int)ExitCode.BuildFailed;
        if (projectSln.IncludeManaged &&
            await PacUtils.PackSolutionAsync(projectSln, PackageFolder(slnFolder), artifactsFolder, true, _capture, cancellationToken) != 0)
        {
            return (int)ExitCode.BuildFailed;
        }

        // Build the solution in dotnet to validate it (Debug = unmanaged, Release = managed!)
        Logger.LogInformation("Validating build: {SlnFolder}", slnFolder);
        if (await DotNetUtils.BuildSolutionAsync(slnFolder, DotnetBuild.Debug, _capture, cancellationToken) != 0) return (int)ExitCode.BuildFailed;
        if (projectSln.IncludeManaged &&
            await DotNetUtils.BuildSolutionAsync(slnFolder, DotnetBuild.Release, _capture, cancellationToken) != 0)
        {
            return (int)ExitCode.BuildFailed;
        }

        await ScaffoldAgentsFileAsync(projectSln.Name, cancellationToken);
        await new DataverseContextGenerator(Console).GenerateAsync(
            Path.Combine(PackageFolder(slnFolder), "src"), projectSln.Name, RootFolder, cancellationToken);

        Console.Done("Cloned! Use 'push' and 'sync' to keep it in flow. ヽ(•‿•)ノ");
        return 0;
    }

    private static readonly string[] s_gitignorePatterns =
    [
        "bin/",
        "obj/",
        "dist/",
        "[Aa]rtifacts/",
        "node_modules/",
        "appsettings.local.json",
        "appsettings.*.local.json",
        ".vs/",
        ".vscode/",
        ".idea/",
        "*.binlog",
        "*.user",
        "*.suo",
        ".env*",
        "!.env.example",
    ];

    private void ScaffoldRootGitignore()
    {
        var gitignorePath = Path.Combine(RootFolder, ".gitignore");
        var existingLines = File.Exists(gitignorePath) ? File.ReadAllLines(gitignorePath) : [];
        var missing = s_gitignorePatterns.Except(existingLines).ToList();
        if (missing.Count > 0)
            File.AppendAllLines(gitignorePath, missing);
    }

    private static void DeleteScaffoldedGitignore(string folder)
    {
        var path = Path.Combine(folder, ".gitignore");
        if (File.Exists(path))
            File.Delete(path);
    }

    private async Task ScaffoldAgentsFileAsync(string solutionName, CancellationToken cancellationToken)
    {
        var agentsPath = Path.Combine(RootFolder, "AGENTS.md");
        if (File.Exists(agentsPath))
        {
            Console.Skip("AGENTS.md already exists — skipping.");
            return;
        }

        var content = $$"""
            # Flowline — Agent Instructions

            Flowline is the ALM CLI for this Power Platform solution repo.
            Use Flowline commands instead of PAC CLI directly.

            ## Daily dev loop

            ```
            dotnet build                    # build plugin assembly
            flowline push --dry-run         # preview what would be registered (optional safety check)
            flowline push                   # register DLL + web resources in DEV
            flowline sync                   # pull solution state from DEV, bump version, unpack to XML
            git add . && git commit -m "…"  # commit the unpacked XML diff
            flowline deploy test            # promote to TEST
            flowline deploy prod            # promote to PROD
            ```

            ## Generate early-bound types (run after entities or custom APIs change)

            ```
            flowline generate               # regenerate Plugins/Models/ from solution entities
            ```

            ## Rules

            - Never run `pac solution` commands directly — Flowline wraps them correctly.
            - Always run `flowline push` before `flowline sync` when plugin code changed.
            - `flowline sync` requires no uncommitted changes in `Package/src/` (exit code 12 if dirty).
            - `flowline deploy` requires no uncommitted changes under the target solution's folder (exit code 12 if dirty).
            - DEV is the source of truth. Sync captures its state; never hand-edit unpacked XML.
            - `clone`, `push`, and `sync` require an unmanaged solution in DEV — they fail on managed environments.
            - To deploy a managed package, use `--managed` on `deploy`. Requires the solution to have been cloned or synced with `--managed` first.
            - In repos with multiple solutions, pass the solution name as the first argument: `flowline push {{solutionName}}`, `flowline sync {{solutionName}}`, etc.

            ## Project structure

            ```
            .flowline                            ← environment URLs + solution config
            .gitignore                           ← single root gitignore for all solutions (bin/obj/node_modules/artifacts/dist)
            solutions/{{solutionName}}/
              Package/Package.cdsproj            ← solution package project (PAC-managed, do not edit)
              Package/src/                       ← unpacked solution XML (git-diffable)
              Plugins/Plugins.csproj             ← plugin source, decorated with [Step] attributes
              Plugins/Models/                    ← early-bound C# types (from flowline generate)
              WebResources/WebResources.csproj   ← web resource assets
              WebResources/dist/                 ← build output synced to Dataverse (gitignored, regenerated by npm run build)
              artifacts/                         ← packed solution zips (gitignored, regenerated by pack)
            ```

            ## Exit codes

            | Code | Meaning | Fix |
            |------|---------|-----|
            | 0 | Success | |
            | 1 | General error | Check error output |
            | 3 | Not found | Verify solution name matches .flowline config |
            | 4 | Not authenticated | Run: `pac auth create --environment <url>` |
            | 10 | Connection failed | Check environment URL in .flowline |
            | 11 | Config invalid | Check .flowline exists and is valid |
            | 12 | Dirty working directory | Commit or stash changes first |
            | 13 | Build failed | Fix `dotnet build` errors in Plugins/ |
            | 14 | Version conflict | Add the --force <specifier> the error names to overwrite |
            | 15 | Validation failed | Check error output for drift, an invalid --force value, or missing dependencies |
            | 16 | Timeout | PAC CLI 60-min limit hit — retry or check environment health |
            | 17 | Force required | Add the --force <specifier> the message names |
            | 130 | Cancelled | Ctrl+C pressed |

            ## Environments

            Defined in `.flowline`. Use `flowline status` to verify connectivity before running commands.

            ## Dataverse schema context
            - [{{solutionName}}](solutions/{{solutionName}}/DATAVERSE_CONTEXT.md)

            @solutions/{{solutionName}}/DATAVERSE_CONTEXT.md
            """;

        await File.WriteAllTextAsync(agentsPath, content, cancellationToken);
        Console.Ok("AGENTS.md created.");
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

            var env = await GetAndCheckEnvironmentInfoAsync(role, null, settings, cancellationToken);
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

    private void SeedWebResourceDistFromSrc(string slnFolder, string? publisherPrefix, string solutionName, Settings settings)
    {
        var srcWebResources = Path.Combine(PackageFolder(slnFolder), "src", "WebResources");
        var publicFolder = Path.Combine(slnFolder, "WebResources", "public");

        if (!Directory.Exists(srcWebResources))
        {
            Console.Skip("No WebResources in src — skipping public seed");
            return;
        }

        Directory.CreateDirectory(publicFolder);
        if (Directory.EnumerateFiles(publicFolder, "*.*", SearchOption.AllDirectories).Any())
        {
            Console.Skip("WebResources/public already populated — skipping");
            return;
        }

        // PAC unpacks web resources under src/WebResources/<publisher_prefix>_<solution>/
        // That subfolder maps to public/ root — strip one level. Everything else copies as-is.
        var publisherFolderName = publisherPrefix != null ? $"{publisherPrefix}_{solutionName}" : null;
        var publisherRoot = publisherFolderName != null
            ? Path.Combine(srcWebResources, publisherFolderName)
            : null;
        if (publisherRoot != null && !Directory.Exists(publisherRoot)) publisherRoot = null;

        foreach (var srcFile in Directory.EnumerateFiles(srcWebResources, "*.*", SearchOption.AllDirectories))
        {
            if (srcFile.EndsWith(".data.xml", StringComparison.OrdinalIgnoreCase)) continue;

            var sourceRoot = publisherRoot != null && srcFile.StartsWith(publisherRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? publisherRoot
                : srcWebResources;

            var relPath = Path.GetRelativePath(sourceRoot, srcFile);
            var destFile = Path.Combine(publicFolder, relPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
            File.Copy(srcFile, destFile, overwrite: false);
        }

        Console.Ok("WebResources/public seeded from src");
        Console.Verbose(publicFolder);
    }

    private async Task CloneSolutionFromDataverseAsync(ProjectSolution projectSln, string slnFolder, string cdsprojPath, string environmentUrl,
        Settings settings, CancellationToken cancellationToken)
    {
        if (File.Exists(cdsprojPath))
        {
            Console.Skip("Solution already cloned — skipping");
            return;
        }

        if (Directory.Exists(PackageFolder(slnFolder)))
            throw new FlowlineException(ExitCode.ConfigInvalid,
                $"{PackageName}/ exists but {PackageName}.cdsproj is missing. Delete solutions/{projectSln.Name}/{PackageName} and re-clone.");

        Directory.CreateDirectory(Path.Combine(RootFolder, AllSolutionsFolderName));
        Directory.CreateDirectory(slnFolder);

        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
        CommandResult result = await Console.Status().FlowlineSpinner().StartAsync(
            $"Cloning solution [bold]{projectSln.Name}[/] from Dataverse...",
            ctx => Cli.Wrap(cmdName)
                      .WithArguments(args =>
                          args.AddIfNotNull(prefixArgs)
                              .Add("solution")
                              .Add("clone")
                              .Add("--name").Add(projectSln.Name)
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

        // PAC creates slnFolder/{SolutionName}/ — rename it to Package/ and rename the .cdsproj
        Directory.Move(Path.Combine(slnFolder, projectSln.Name), PackageFolder(slnFolder));
        File.Move(
            Path.Combine(PackageFolder(slnFolder), $"{projectSln.Name}.cdsproj"),
            cdsprojPath);
        DeleteScaffoldedGitignore(PackageFolder(slnFolder)); // superseded by the project-root .gitignore

        Console.Ok($"Solution [bold]{projectSln.Name}[/] cloned in {FormatDuration(result.RunTime)}");
    }

    private async Task CreateSolutionFileAsync(ProjectSolution projectSln, string slnFolder, string slnFilePath, string cdsprojPath, Settings settings,
        CancellationToken cancellationToken)
    {
        // Create Solution file if it doesn't exist (use sln for now because slnx can't handle .cdsproj yet)
        if (File.Exists(slnFilePath))
        {
            Console.Skip("Solution file already there — skipping");
            return;
        }

        var result = await Cli.Wrap("dotnet")
                              .WithArguments(args => args
                                                     .Add("new")
                                                     .Add("sln")
                                                     .Add("--name").Add(projectSln.Name)
                                                     .Add("--format").Add("sln"))
                              .WithWorkingDirectory(slnFolder)
                              .WithCapture(_capture)
                              .ExecuteAsync(cancellationToken)
                              .Task.FlowlineSpinner();

        if (!result.IsSuccess || !File.Exists(slnFilePath))
            throw new FlowlineException(ExitCode.BuildFailed, "Couldn't create the solution file.");

        Console.Ok("Solution file created");

        // NOTE: 'dotnet sln add' doesn't support .cdsproj directly.
        // We'll rename it to .csproj, add it, then rename it back and fix the .sln file.
        var csprojPath = Path.ChangeExtension(cdsprojPath, ".csproj");
        if (File.Exists(cdsprojPath))
        {
            Console.Verbose($"Renaming '{cdsprojPath}' to '{csprojPath}'");
            File.Move(cdsprojPath, csprojPath);
        }

        await Cli.Wrap("dotnet")
                 .WithArguments(args => args
                                        .Add("sln")
                                        .Add("add")
                                        .Add(csprojPath))
                 .WithWorkingDirectory(slnFolder)
                 .WithCapture(_capture)
                 .ExecuteAsync(cancellationToken)
                 .Task.FlowlineSpinner();

        // Rename back to .cdsproj
        if (File.Exists(csprojPath))
        {
            Console.Verbose($"Renaming '{csprojPath}' back to '{cdsprojPath}'");
            File.Move(csprojPath, cdsprojPath);
        }

        // Fix the XML in the .sln file
        if (File.Exists(slnFilePath))
        {
            Console.Verbose("Fixing XML in .sln file...");
            var slnContent = await File.ReadAllTextAsync(slnFilePath, cancellationToken);
            slnContent = slnContent.Replace(Path.GetFileName(csprojPath), Path.GetFileName(cdsprojPath));
            await File.WriteAllTextAsync(slnFilePath, slnContent, cancellationToken);
        }

        Console.Ok($"[bold]{PackageName}.cdsproj[/] added to solution file");
        Console.Verbose(slnFilePath);
    }

    private async Task SetupPluginsProjectAsync(string slnFolder, Settings settings, CancellationToken cancellationToken)
    {
        var pluginsFolder = Path.Combine(slnFolder, PluginsName);
        var pluginsCsproj = Path.Combine(pluginsFolder, $"{PluginsName}.csproj");
        if (File.Exists(pluginsCsproj))
        {
            Console.Skip("Plugins project already there — skipping");
            return;
        }

        await Console.Status().FlowlineSpinner().StartAsync(
            "Setting up Plugins project...", async ctx =>
            {
                // Create Plugins project
                Directory.CreateDirectory(pluginsFolder);

                var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
                await Cli.Wrap(cmdName)
                         .WithArguments(args => args
                                                .AddIfNotNull(prefixArgs)
                                                .Add("plugin")
                                                .Add("init")) // --skip-signing
                         .WithWorkingDirectory(pluginsFolder)
                         .WithCapture(_capture)
                         .ExecuteAsync(cancellationToken);
                DeleteScaffoldedGitignore(pluginsFolder); // superseded by the project-root .gitignore

                // Add Flowline.Attributes NuGet package
                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                                .Add("add")
                                                .Add(pluginsCsproj)
                                                .Add("package")
                                                .Add("Flowline.Attributes"))
                         .WithWorkingDirectory(pluginsFolder)
                         .WithCapture(_capture)
                         .ExecuteAsync(cancellationToken);

                // Add MinVer NuGet package
                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                                .Add("add")
                                                .Add(pluginsCsproj)
                                                .Add("package")
                                                .Add("MinVer"))
                         .WithWorkingDirectory(pluginsFolder)
                         .WithCapture(_capture)
                         .ExecuteAsync(cancellationToken);

                // Add Plugins.csproj to the solution
                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                                .Add("sln")
                                                .Add("add")
                                                .Add(pluginsCsproj))
                         .WithWorkingDirectory(slnFolder)
                         .WithCapture(_capture)
                         .ExecuteAsync(cancellationToken);
            });

        Console.Ok("Plugins project ready");
    }

    private async Task SetupWebResourcesProjectAsync(string slnFolder, string slnFilePath, Settings settings, CancellationToken cancellationToken)
    {
        // Create WebResources project if it doesn't exist
        var webresourcesFolder = Path.Combine(slnFolder, WebResourcesName);
        var webresourcesCsproj = Path.Combine(webresourcesFolder, $"{WebResourcesName}.csproj");
        if (File.Exists(webresourcesCsproj))
        {
            Console.Skip("WebResources project already there — skipping");
            return;
        }

        await Console.Status().FlowlineSpinner().StartAsync(
            "Setting up WebResources project...", async ctx =>
            {
                Directory.CreateDirectory(webresourcesFolder);

                await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.WebResources.csproj", webresourcesCsproj, cancellationToken);
                await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.package.json", Path.Combine(webresourcesFolder, "package.json"), cancellationToken);
                await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.rollup.config.mjs", Path.Combine(webresourcesFolder, "rollup.config.mjs"), cancellationToken);
                await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.tsconfig.json", Path.Combine(webresourcesFolder, "tsconfig.json"), cancellationToken);
                await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.eslint.config.mjs", Path.Combine(webresourcesFolder, "eslint.config.mjs"), cancellationToken);
                await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.README.md", Path.Combine(webresourcesFolder, "README.md"), cancellationToken);

                Directory.CreateDirectory(Path.Combine(webresourcesFolder, "src", "modules"));
                await TemplateWriter.WriteAsync("Flowline.Templates.WebResources.src.example.ts", Path.Combine(webresourcesFolder, "src", "example.ts"), cancellationToken);
                Directory.CreateDirectory(Path.Combine(webresourcesFolder, "public"));
                Directory.CreateDirectory(Path.Combine(webresourcesFolder, "dist"));

                Console.Verbose($"Created {ConsolePath.FormatRelativePath(webresourcesFolder)}");

                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                                .Add("sln")
                                                .Add(slnFilePath)
                                                .Add("add")
                                                .Add(webresourcesCsproj))
                         .WithCapture(_capture)
                         .ExecuteAsync(cancellationToken);

                Console.Verbose($"Added {WebResourcesName} project to solution");
            });

        Console.Ok("WebResources project ready");
    }
}
