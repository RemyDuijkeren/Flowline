using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Flowline.Core;
using Flowline.Utils;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class CloneCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions) : FlowlineCommand<CloneCommand.Settings>(console, runtimeOptions)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<solution>")]
        [Description("Solution to clone into this repo")]
        public string? Solution { get; set; }

        [CommandOption("--prod <URL>")]
        [Description("Production environment URL to clone solution from")]
        public string? ProdUrl { get; set; }

        [CommandOption("--managed")]
        [Description("Include managed artifacts")]
        [DefaultValue(false)]
        public bool IncludeManaged { get; set; } = false;

        [CommandOption("--dev <URL>")]
        [Description("Development environment URL")]
        public string? DevUrl { get; set; }

        // - `--dev <url>`: save the development environment URL into `.flowconfig`
    }

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Production URL is required
        var prodEnv = await GetAndCheckEnvironmentInfoAsync(EnvironmentRole.Prod, settings.ProdUrl, settings, cancellationToken);
        if (prodEnv == null) return 1;

        // Solution name is required
        (ProjectSolution? projectSln, SolutionInfo? slnInfo) = await GetAndCheckSolutionAsync(settings.Solution, prodEnv.EnvironmentUrl!, settings.IncludeManaged, settings, cancellationToken);
        if (projectSln == null || slnInfo == null) return 1;
        if (slnInfo.IsManaged)
        {
            Console.Error("Managed solutions are not supported yet");
            return 1;
        }

        // Save the configuration
        Config?.Save();
        Console.Verbose($"Project configuration saved to {ProjectConfig.s_configFileName}", settings.Verbose);

        // Cleanup if existing cloned output folder exists, so we download into a clean folder
        var slnFolder = Path.Combine(RootFolder, AllSolutionsFolderName, projectSln.Name);
        string tempClonedOutputFolder = Path.Combine(slnFolder, projectSln.Name);
        if (Directory.Exists(tempClonedOutputFolder))
        {
            Console.Info($"Removing stale temp clone folder");
            Console.Verbose($"[dim]Path: /{AllSolutionsFolderName}/{projectSln.Name}/{projectSln.Name}[/]", settings.Verbose);
            Directory.Delete(tempClonedOutputFolder, true);
        }

        // Clone solution from Dataverse if it doesn't exist locally
        string solutionPackageFolder = Path.Combine(slnFolder, SolutionPackageName);
        var cdsprojPath = Path.Combine(slnFolder, SolutionPackageName, $"{SolutionPackageName}.cdsproj");
        if (!Directory.Exists(solutionPackageFolder) || !File.Exists(cdsprojPath))
        {
            // Clone solution from Dataverse
            var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
            CommandResult result = await Console.Status().FlowlineSpinner().StartAsync(
                $"Cloning [bold]{projectSln.Name}[/] from Dataverse...",
                ctx => Cli.Wrap(cmdName)
                          .WithArguments(args => args
                              .AddIfNotNull(prefixArgs)
                              .Add("solution")
                              .Add("clone")
                              .Add("--name").Add(projectSln.Name)
                              .Add("--environment").Add(Config!.ProdUrl!)
                              .Add("--packagetype").Add(projectSln.IncludeManaged ? "Both" : "Unmanaged")
                              .Add("--outputDirectory").Add(slnFolder) // will create <sln.Name> folder under this given folder
                              .Add("--async"))
                          .WithValidation(CommandResultValidation.None)
                          .WithToolExecutionLog(settings.Verbose, ctx)
                          .ExecuteAsync(cancellationToken)
                          .Task);

            if (!result.IsSuccess)
            {
                Console.Error("Clone failed — check the environment and your PAC login.");
                return 1;
            }

            // PAC creates tempClonedOutput folder 'solutions/SolutionName/SolutionName/SolutionName.cdsproj'
            // We want 'solutions/SolutionName/SolutionPackage/SolutionPackage.cdsproj'
            if (Directory.Exists(tempClonedOutputFolder))
            {
                Console.Verbose($"Renaming {tempClonedOutputFolder} -> {solutionPackageFolder}", settings.Verbose);
                Directory.Move(tempClonedOutputFolder, solutionPackageFolder);
            }

            var clonedCdsproj = Path.Combine(solutionPackageFolder, $"{projectSln.Name}.cdsproj");
            if (File.Exists(clonedCdsproj))
            {
                Console.Verbose($"Renaming {clonedCdsproj} -> {SolutionPackageName}.cdsproj", settings.Verbose);
                File.Move(clonedCdsproj, Path.Combine(solutionPackageFolder, $"{SolutionPackageName}.cdsproj"));
            }

            Console.Success($"Solution cloned");
        }
        else
        {
            Console.Skip($"Solution already cloned — skipping");
        }

        // Create Solution file if it doesn't exist (use sln for now because slnx can't handle .cdsproj yet)
        var slnFilePath = Path.Combine(slnFolder, $"{projectSln.Name}.sln");
        if (!File.Exists(slnFilePath))
        {
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
            {
                Console.Error("Couldn't create the solution file.");
                return 1;
            }

            Console.Success("Solution file created");

            // Add SolutionPackage.cdsproj to the solution
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
                slnContent = slnContent.Replace($"{SolutionPackageName}.csproj", $"{SolutionPackageName}.cdsproj");
                await File.WriteAllTextAsync(slnFilePath, slnContent, cancellationToken);
            }

            Console.Success($"[bold]{SolutionPackageName}.cdsproj[/] added to solution file");
            Console.Verbose($"[dim]{slnFilePath}[/]", settings.Verbose);
        }
        else
        {
            Console.Skip("Solution file already there — skipping");
        }

        // Create Extensions (plugins) project if it doesn't exist
        var extensionsFolder = Path.Combine(slnFolder, ExtensionsName);
        var extensionsCsproj = Path.Combine(extensionsFolder, $"{ExtensionsName}.csproj");
        if (!File.Exists(extensionsCsproj))
        {
            Console.Info("Setting up Extensions project...");
            Directory.CreateDirectory(extensionsFolder);

            var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
            await Cli.Wrap(cmdName)
                     .WithArguments(args => args
                         .AddIfNotNull(prefixArgs)
                         .Add("plugin")
                         .Add("init")) // --skip-signing
                     .WithWorkingDirectory(extensionsFolder)
                     .WithToolExecutionLog(settings.Verbose)
                     .ExecuteAsync(cancellationToken)
                     .Task.FlowlineSpinner();

            // Add Extensions.csproj to the solution
            await Cli.Wrap("dotnet")
                     .WithArguments(args => args
                                          .Add("sln")
                                          .Add("add")
                                          .Add(extensionsCsproj))
                     .WithWorkingDirectory(slnFolder)
                     .WithToolExecutionLog(settings.Verbose)
                     .ExecuteAsync(cancellationToken)
                     .Task.FlowlineSpinner();

            Console.Success("Extensions project ready");
        }
        else
        {
            Console.Skip("Extensions project already there — skipping");
        }

        // Create WebResources project if it doesn't exist
        var webresourcesFolder = Path.Combine(slnFolder, WebResourcesName);
        var webresourcesCsproj = Path.Combine(webresourcesFolder, $"{WebResourcesName}.csproj");
        if (!File.Exists(webresourcesCsproj))
        {
            Console.Info("Setting up WebResources project...");

            // Create a basic class library for WebResources.csproj
            await Cli.Wrap("dotnet")
                     .WithArguments(args => args
                                          .Add("new")
                                          .Add("classlib")
                                          .Add("--name").Add(WebResourcesName))
                     .WithWorkingDirectory(slnFolder)
                     .WithToolExecutionLog(settings.Verbose)
                     .ExecuteAsync(cancellationToken);

            // Add WebResources.csproj to the solution
            await Cli.Wrap("dotnet")
                     .WithArguments(args => args
                                          .Add("sln")
                                          .Add(slnFilePath)
                                          .Add("add")
                                          .Add(webresourcesCsproj))
                     .WithToolExecutionLog(settings.Verbose)
                     .ExecuteAsync(cancellationToken);

            // Create default WebResources folder structure
            File.Delete(Path.Combine(webresourcesFolder, "Class1.cs"));
            Directory.CreateDirectory(Path.Combine(webresourcesFolder, "src"));
            Directory.CreateDirectory(Path.Combine(webresourcesFolder, "public"));
            Directory.CreateDirectory(Path.Combine(webresourcesFolder, "dist"));
            Directory.CreateDirectory(Path.Combine(webresourcesFolder, "dist", "images"));
            Directory.CreateDirectory(Path.Combine(webresourcesFolder, "dist", "scripts"));
            Directory.CreateDirectory(Path.Combine(webresourcesFolder, "dist", "pages"));

            Console.Success("WebResources project ready");
        }
        else
        {
            Console.Skip("WebResources project already there — skipping");
        }

        // Create XML Mapping file in SolutionPackage folder
        var mappingFilePath = Path.Combine(solutionPackageFolder, "Mapping.xml");
        if (!File.Exists(mappingFilePath))
        {
            await File.WriteAllTextAsync(
                mappingFilePath,
                $"""
                  <?xml version="1.0" encoding="utf-8"?>
                 <Mapping>
                     <!-- https://docs.microsoft.com/en-us/dynamics365/customer-engagement/developer/compress-extract-solution-file-solutionpackager -->
                     <FileToFile map="PluginAssemblies\**\Extensions.dll" to="..\..\Extensions\bin\Release\net462\Extensions.dll" />
                     <FileToPath map="WebResources\**\*.*" to="..\..\WebResources\dist\**" />
                 </Mapping>
                 """,
                cancellationToken).FlowlineSpinner();

            Console.Success("Mapping file written");
        }
        else
        {
            Console.Skip("Mapping file already there — skipping");
        }

        // Build the solution in dotnet to validate it (Debug = unmanaged, Release = managed!)
        if (await DotNetUtils.BuildSolutionAsync(slnFolder, DotnetBuild.Debug, settings.Verbose, cancellationToken) != 0)
        {
            return 1;
        }
        if (settings.IncludeManaged &&
            await DotNetUtils.BuildSolutionAsync(slnFolder, DotnetBuild.Release, settings.Verbose, cancellationToken) != 0)
        {
            return 1;
        }

        Console.Success("[bold]:rocket: Cloned! Use 'push' and 'sync' to keep it in flow.[/]");

        return 0;
    }
}
