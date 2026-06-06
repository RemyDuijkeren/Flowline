using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Flowline.Core;
using Flowline.Utils;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class CloneCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions) :
    FlowlineCommand<CloneCommand.Settings>(console, runtimeOptions)
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
        [Description("Include managed artifacts")]
        [DefaultValue(false)]
        public bool IncludeManaged { get; set; } = false;
    }

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Save all provided URLs to config first (no API calls, just config update + prompt on conflict)
        Config!.GetOrUpdateProdUrl(settings.ProdUrl, settings);
        Config!.GetOrUpdateUatUrl(settings.UatUrl, settings);
        Config!.GetOrUpdateTestUrl(settings.TestUrl, settings);
        Config!.GetOrUpdateDevUrl(settings.DevUrl, settings);

        var (sourceEnv, projectSln, solutionInfo) = await FindUnmanagedSourceAsync(settings, cancellationToken);

        Config.Save();
        Console.Verbose($"Project configuration saved to {ProjectConfig.s_configFileName}", settings.Verbose);

        var slnFolder = Path.Combine(RootFolder, AllSolutionsFolderName, projectSln.Name);
        var cdsprojPath = Path.Combine(PackageFolder(slnFolder), $"{PackageName}.cdsproj");
        var slnFilePath = Path.Combine(slnFolder, $"{projectSln.Name}.sln");

        await CloneSolutionFromDataverseAsync(projectSln, slnFolder, cdsprojPath, sourceEnv.EnvironmentUrl!, settings, cancellationToken);
        await CreateSolutionFileAsync(projectSln, slnFolder, slnFilePath, cdsprojPath, settings, cancellationToken);
        await SetupPluginsProjectAsync(slnFolder, settings, cancellationToken);
        await SetupWebResourcesProjectAsync(slnFolder, slnFilePath, settings, cancellationToken);
        SeedWebResourceDistFromSrc(slnFolder, solutionInfo.PublisherPrefix, projectSln.Name, settings);

        // Pack the solution in pac to validate it
        var artifactsFolder = Path.Combine(slnFolder, "artifacts");
        Directory.CreateDirectory(artifactsFolder);
        if (await PacUtils.PackSolutionAsync(projectSln, PackageFolder(slnFolder), artifactsFolder, false, settings.Verbose, cancellationToken) != 0) return 1;
        if (settings.IncludeManaged &&
            await PacUtils.PackSolutionAsync(projectSln, PackageFolder(slnFolder), artifactsFolder, true, settings.Verbose, cancellationToken) != 0)
        {
            return 1;
        }

        // Build the solution in dotnet to validate it (Debug = unmanaged, Release = managed!)
        if (await DotNetUtils.BuildSolutionAsync(slnFolder, DotnetBuild.Debug, settings.Verbose, cancellationToken) != 0) return 1;
        if (settings.IncludeManaged &&
            await DotNetUtils.BuildSolutionAsync(slnFolder, DotnetBuild.Release, settings.Verbose, cancellationToken) != 0)
        {
            return 1;
        }

        Console.Done("Cloned! Use 'push' and 'sync' to keep it in flow.");
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

            var env = await GetAndCheckEnvironmentInfoAsync(role, null, settings, cancellationToken);
            var (sln, info) = await GetAndCheckSolutionAsync(
                settings.Solution, env.EnvironmentUrl!, settings.IncludeManaged, settings, cancellationToken);

            if (info.IsManaged)
            {
                var label = role switch { EnvironmentRole.Prod => "Prod", EnvironmentRole.Uat => "UAT", EnvironmentRole.Test => "Test", _ => "Dev" };
                Console.MarkupLine($"[dim]{label} solution is managed — skipping[/]");
                continue;
            }

            return (env, sln, info);
        }

        throw new FlowlineException("No unmanaged environment found — provide a --dev, --test, --uat, or --prod URL with an unmanaged solution.");
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
        Console.Verbose($"[dim]{publicFolder}[/]", settings.Verbose);
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
            throw new FlowlineException(
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
                      .WithToolExecutionLog(settings.Verbose, ctx)
                      .ExecuteAsync(cancellationToken)
                      .Task);

        if (!result.IsSuccess)
            throw new FlowlineException("Clone failed — check the environment and your PAC login.");

        // PAC creates slnFolder/{SolutionName}/ — rename it to Package/ and rename the .cdsproj
        Directory.Move(Path.Combine(slnFolder, projectSln.Name), PackageFolder(slnFolder));
        File.Move(
            Path.Combine(PackageFolder(slnFolder), $"{projectSln.Name}.cdsproj"),
            cdsprojPath);

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
                              .WithToolExecutionLog(settings.Verbose)
                              .ExecuteAsync(cancellationToken)
                              .Task.FlowlineSpinner();

        if (!result.IsSuccess || !File.Exists(slnFilePath))
            throw new FlowlineException("Couldn't create the solution file.");

        Console.Ok("Solution file created");

        // NOTE: 'dotnet sln add' doesn't support .cdsproj directly.
        // We'll rename it to .csproj, add it, then rename it back and fix the .sln file.
        var csprojPath = Path.ChangeExtension(cdsprojPath, ".csproj");
        if (File.Exists(cdsprojPath))
        {
            Console.Verbose($"Renaming '{cdsprojPath}' to '{csprojPath}'", settings.Verbose);
            File.Move(cdsprojPath, csprojPath);
        }

        await Cli.Wrap("dotnet")
                 .WithArguments(args => args
                                        .Add("sln")
                                        .Add("add")
                                        .Add(csprojPath))
                 .WithWorkingDirectory(slnFolder)
                 .WithToolExecutionLog(settings.Verbose)
                 .ExecuteAsync(cancellationToken)
                 .Task.FlowlineSpinner();

        // Rename back to .cdsproj
        if (File.Exists(csprojPath))
        {
            Console.Verbose($"Renaming '{csprojPath}' back to '{cdsprojPath}'", settings.Verbose);
            File.Move(csprojPath, cdsprojPath);
        }

        // Fix the XML in the .sln file
        if (File.Exists(slnFilePath))
        {
            Console.Verbose("Fixing XML in .sln file...", settings.Verbose);
            var slnContent = await File.ReadAllTextAsync(slnFilePath, cancellationToken);
            slnContent = slnContent.Replace(Path.GetFileName(csprojPath), Path.GetFileName(cdsprojPath));
            await File.WriteAllTextAsync(slnFilePath, slnContent, cancellationToken);
        }

        Console.Ok($"[bold]{PackageName}.cdsproj[/] added to solution file");
        Console.Verbose($"[dim]{slnFilePath}[/]", settings.Verbose);
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
                         .WithToolExecutionLog(settings.Verbose)
                         .ExecuteAsync(cancellationToken);

                // Add Flowline.Attributes NuGet package
                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                                .Add("add")
                                                .Add(pluginsCsproj)
                                                .Add("package")
                                                .Add("Flowline.Attributes"))
                         .WithWorkingDirectory(pluginsFolder)
                         .WithToolExecutionLog(settings.Verbose)
                         .ExecuteAsync(cancellationToken);

                // Add MinVer NuGet package
                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                                .Add("add")
                                                .Add(pluginsCsproj)
                                                .Add("package")
                                                .Add("MinVer"))
                         .WithWorkingDirectory(pluginsFolder)
                         .WithToolExecutionLog(settings.Verbose)
                         .ExecuteAsync(cancellationToken);

                // Add Plugins.csproj to the solution
                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                                .Add("sln")
                                                .Add("add")
                                                .Add(pluginsCsproj))
                         .WithWorkingDirectory(slnFolder)
                         .WithToolExecutionLog(settings.Verbose)
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

                Console.Verbose($"Created {ConsolePath.FormatRelativePath(webresourcesFolder)}", settings.Verbose);

                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                                .Add("sln")
                                                .Add(slnFilePath)
                                                .Add("add")
                                                .Add(webresourcesCsproj))
                         .WithToolExecutionLog(settings.Verbose)
                         .ExecuteAsync(cancellationToken);

                Console.Verbose($"Added {WebResourcesName} project to solution", settings.Verbose);
            });

        Console.Ok("WebResources project ready");
    }
}
