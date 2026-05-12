using System.ComponentModel;
using System.Diagnostics;
using CliWrap;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Utils;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class CloneCommand(IAnsiConsole console, DataverseConnector dataverseConnector, WebResourceService webResourceService, FlowlineRuntimeOptions runtimeOptions) : FlowlineCommand<CloneCommand.Settings>(console, runtimeOptions)
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

        [CommandOption("--full")]
        [Description("Download all artifacts from Dataverse, including binaries (skips mapping)")]
        [DefaultValue(false)]
        public bool Full { get; set; } = false;

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

        var slnFolder = Path.Combine(RootFolder, AllSolutionsFolderName, projectSln.Name);
        var cdsprojPath = Path.Combine(slnFolder, $"{projectSln.Name}.cdsproj");
        var slnFilePath = Path.Combine(slnFolder, $"{projectSln.Name}.sln");

        if (await CloneSolutionFromDataverseAsync(projectSln, slnFolder, cdsprojPath, settings, cancellationToken) != 0) return 1;
        if (await WriteMappingFilesAsync(slnFolder, cancellationToken) != 0) return 1;
        if (await InjectMapFilePathAsync(cdsprojPath, cancellationToken) != 0) return 1;
        if (await CreateSolutionFileAsync(projectSln, slnFolder, slnFilePath, cdsprojPath, settings, cancellationToken) != 0) return 1;
        if (await SetupPluginsProjectAsync(slnFolder, settings, cancellationToken) != 0) return 1;
        if (await SetupWebResourcesProjectAsync(slnFolder, slnFilePath, settings, cancellationToken) != 0) return 1;
        if (await CloneWebResourcesFromDataverseAsync(projectSln, prodEnv.EnvironmentUrl!, slnFolder, settings, cancellationToken) != 0) return 1;

        // Build the solution in dotnet to validate it (Debug = unmanaged, Release = managed!)
        if (await DotNetUtils.BuildSolutionAsync(slnFolder, DotnetBuild.Debug, settings.Verbose, cancellationToken) != 0) return 1;
        if (settings.IncludeManaged &&
            await DotNetUtils.BuildSolutionAsync(slnFolder, DotnetBuild.Release, settings.Verbose, cancellationToken) != 0)
        {
            return 1;
        }

        Console.Success("[bold]:rocket: Cloned! Use 'push' and 'sync' to keep it in flow.[/]");
        return 0;
    }

    private async Task<int> CloneSolutionFromDataverseAsync(ProjectSolution projectSln, string slnFolder, string cdsprojPath, Settings settings, CancellationToken cancellationToken)
    {
        if (File.Exists(cdsprojPath))
        {
            Console.Skip($"Solution already cloned — skipping");
            return 0;
        }

        if (Directory.Exists(slnFolder) && !File.Exists(cdsprojPath))
        {
            Console.Error($"Solution folder {slnFolder} already exists but no .cdsproj file found. Cannot clone. Delete the folder and try again.");
            return 1;
        }

        // PAC creates outputDirectory/SolutionName/ directly — no rename needed.
        // Write a temp pac mapping outside slnFolder so PAC doesn't see a non-empty output subfolder.
        var allSolutionsFolder = Path.Combine(RootFolder, AllSolutionsFolderName);
        Directory.CreateDirectory(allSolutionsFolder);
        string? tempPacMap = null;
        if (!settings.Full)
        {
            tempPacMap = Path.Combine(allSolutionsFolder, $"{projectSln.Name}.MappingPac-temp.xml");
            await File.WriteAllTextAsync(tempPacMap, PacMappingContent, cancellationToken);
        }

        CommandResult result;
        var sw = Stopwatch.StartNew();
        try
        {
            var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
            result = await Console.Status().FlowlineSpinner().StartAsync(
                $"Cloning solution [bold]{projectSln.Name}[/] from Dataverse...",
                ctx => Cli.Wrap(cmdName)
                          .WithArguments(args =>
                          {
                              args.AddIfNotNull(prefixArgs)
                                  .Add("solution")
                                  .Add("clone")
                                  .Add("--name").Add(projectSln.Name)
                                  .Add("--environment").Add(Config!.ProdUrl!)
                                  .Add("--packagetype").Add(projectSln.IncludeManaged ? "Both" : "Unmanaged")
                                  .Add("--outputDirectory").Add(allSolutionsFolder)
                                  .Add("--async");
                              if (tempPacMap != null)
                                  args.Add("--map").Add(tempPacMap);
                          })
                          .WithValidation(CommandResultValidation.None)
                          .WithToolExecutionLog(settings.Verbose, ctx)
                          .ExecuteAsync(cancellationToken)
                          .Task);
        }
        finally
        {
            sw.Stop();
            if (tempPacMap != null) File.Delete(tempPacMap);
        }

        if (!result.IsSuccess)
        {
            Console.Error("Clone failed — check the environment and your PAC login.");
            return 1;
        }

        Console.Success($"Solution [bold]{projectSln.Name}[/] cloned in {FormatDuration(sw.Elapsed)}");
        return 0;
    }

    private async Task<int> CreateSolutionFileAsync(ProjectSolution projectSln, string slnFolder, string slnFilePath, string cdsprojPath, Settings settings, CancellationToken cancellationToken)
    {
        // Create Solution file if it doesn't exist (use sln for now because slnx can't handle .cdsproj yet)
        if (File.Exists(slnFilePath))
        {
            Console.Skip("Solution file already there — skipping");
            return 0;
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
        {
            Console.Error("Couldn't create the solution file.");
            return 1;
        }

        Console.Success("Solution file created");

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
            slnContent = slnContent.Replace($"{projectSln.Name}.csproj", $"{projectSln.Name}.cdsproj");
            await File.WriteAllTextAsync(slnFilePath, slnContent, cancellationToken);
        }

        Console.Success($"[bold]{projectSln.Name}.cdsproj[/] added to solution file");
        Console.Verbose($"[dim]{slnFilePath}[/]", settings.Verbose);
        return 0;
    }

    private async Task<int> SetupPluginsProjectAsync(string slnFolder, Settings settings, CancellationToken cancellationToken)
    {
        // Create Plugins project if it doesn't exist
        var pluginsFolder = Path.Combine(slnFolder, PluginsName);
        var pluginsCsproj = Path.Combine(pluginsFolder, $"{PluginsName}.csproj");
        if (File.Exists(pluginsCsproj))
        {
            Console.Skip("Plugins project already there — skipping");
            return 0;
        }

        Console.Info("Setting up Plugins project...");
        Directory.CreateDirectory(pluginsFolder);

        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
        await Cli.Wrap(cmdName)
                 .WithArguments(args => args
                     .AddIfNotNull(prefixArgs)
                     .Add("plugin")
                     .Add("init")) // --skip-signing
                 .WithWorkingDirectory(pluginsFolder)
                 .WithToolExecutionLog(settings.Verbose)
                 .ExecuteAsync(cancellationToken)
                 .Task.FlowlineSpinner();

        // Add Plugins.csproj to the solution
        await Cli.Wrap("dotnet")
                 .WithArguments(args => args
                                      .Add("sln")
                                      .Add("add")
                                      .Add(pluginsCsproj))
                 .WithWorkingDirectory(slnFolder)
                 .WithToolExecutionLog(settings.Verbose)
                 .ExecuteAsync(cancellationToken)
                 .Task.FlowlineSpinner();

        Console.Success("Plugins project ready");
        return 0;
    }

    private async Task<int> SetupWebResourcesProjectAsync(string slnFolder, string slnFilePath, Settings settings, CancellationToken cancellationToken)
    {
        // Create WebResources project if it doesn't exist
        var webresourcesFolder = Path.Combine(slnFolder, WebResourcesName);
        var webresourcesCsproj = Path.Combine(webresourcesFolder, $"{WebResourcesName}.csproj");
        if (File.Exists(webresourcesCsproj))
        {
            Console.Skip("WebResources project already there — skipping");
            return 0;
        }

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

        Console.Success("WebResources project ready");
        return 0;
    }

    private async Task<int> CloneWebResourcesFromDataverseAsync(
        ProjectSolution projectSln, string environmentUrl, string slnFolder, Settings settings, CancellationToken cancellationToken)
    {
        var distFolder = Path.Combine(slnFolder, WebResourcesName, "dist");

        if (Directory.Exists(distFolder) && Directory.EnumerateFiles(distFolder, "*.*", SearchOption.AllDirectories).Any())
        {
            Console.Skip("WebResources/dist already populated — skipping");
            return 0;
        }

        Directory.CreateDirectory(distFolder);

        var conn = await ConnectToDataverseAsync(environmentUrl, cancellationToken);
        if (conn == null) return 1;

        await webResourceService.DownloadWebResourcesAsync(conn, distFolder, projectSln.Name, cancellationToken);
        return 0;
    }

    private async Task<IOrganizationServiceAsync2?> ConnectToDataverseAsync(string environmentUrl, CancellationToken cancellationToken)
    {
        IOrganizationServiceAsync2? conn = null;

        await Console.Status().FlowlineSpinner().StartAsync("Connecting to Dataverse...", async ctx =>
        {
            var profile = dataverseConnector.FindBestProfile(environmentUrl);
            if (profile == null)
            {
                Console.Error("No PAC profile found — run 'pac auth create' first.");
                return;
            }

            try
            {
                conn = await dataverseConnector.ConnectViaPacAsync(profile, environmentUrl, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                Console.Error(ex);
            }
        });

        if (conn != null)
            Console.Success("Connected");

        return conn;
    }

    private async Task<int> InjectMapFilePathAsync(string cdsprojPath, CancellationToken cancellationToken)
    {
        if (!File.Exists(cdsprojPath))
        {
            Console.Error($"No .cdsproj found at '{cdsprojPath}' — cannot inject map file path.");
            return 1;
        }

        var content = await File.ReadAllTextAsync(cdsprojPath, cancellationToken);
        if (content.Contains("SolutionPackageMapFilePath")) return 0;

        content = content.Replace("</Project>",
            $"  <PropertyGroup>\n    <SolutionPackageMapFilePath>$(MSBuildProjectDirectory)\\{MappingBuildFileName}</SolutionPackageMapFilePath>\n  </PropertyGroup>\n</Project>");
        await File.WriteAllTextAsync(cdsprojPath, content, cancellationToken);
        return 0;
    }

    private const string PacMappingContent =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <Mapping>
            <!-- pac solution clone / sync: paths relative to src\ (1 level up to solution root) -->
            <FileToFile map="PluginAssemblies\**\Plugins.dll" to="..\Plugins\bin\Release\net462\Plugins.dll" />
            <FileToPath map="WebResources\**\*.*"  to="..\WebResources\dist\**" />
        </Mapping>
        """;

    private const string BuildMappingContent =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <Mapping>
            <!-- dotnet build (SolutionPackagerTask via MSBuild): paths relative to obj\Debug\Metadata\ (3 levels up) -->
            <FileToFile map="PluginAssemblies\**\Plugins.dll" to="..\..\..\Plugins\bin\Release\net462\Plugins.dll" />
            <FileToPath map="WebResources\**\*.*"  to="..\..\..\WebResources\dist\**" />
        </Mapping>
        """;

    private async Task<int> WriteMappingFilesAsync(string slnFolder, CancellationToken cancellationToken)
    {
        var pacMapFile   = Path.Combine(slnFolder, MappingPacFileName);
        var buildMapFile = Path.Combine(slnFolder, MappingBuildFileName);

        if (File.Exists(pacMapFile) && File.Exists(buildMapFile))
        {
            Console.Skip("Mapping files already there — skipping");
            return 0;
        }

        Directory.CreateDirectory(slnFolder);

        await File.WriteAllTextAsync(pacMapFile, PacMappingContent, cancellationToken);

        await File.WriteAllTextAsync(buildMapFile, BuildMappingContent, cancellationToken);

        Console.Success("Mapping files written");
        return 0;
    }
}
