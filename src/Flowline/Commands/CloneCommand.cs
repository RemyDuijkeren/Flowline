using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Flowline.Utils;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Extensions;

namespace Flowline.Commands;

public class CloneCommand : FlowlineCommand<CloneCommand.Settings>
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<solution>")]
        [Description("The solution to clone into the repo")]
        public string? Solution { get; set; }

        [CommandOption("--prod <URL>")]
        [Description("The production environment to clone the solution from")]
        public string? ProdUrl { get; set; }

        [CommandOption("--managed")]
        [Description("Also clone managed artifacts in addition to unmanaged")]
        [DefaultValue(false)]
        public bool IncludeManaged { get; set; } = false;

        [CommandOption("--dev <URL>")]
        [Description("Override the configured development environment")]
        public string? DevUrl { get; set; }

        // - `--dev <url>`: save the development environment URL into `.flowconfig`
    }

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Production URL is required
        var prodEnv = await ResolveAndValidateProdUrlAsync(settings.ProdUrl, settings, cancellationToken);
        if (prodEnv == null) return 1;

        // Solution name is required
        var sln = await ResolveAndValidateSolutionAsync(settings.Solution, prodEnv.EnvironmentUrl!, settings.IncludeManaged, settings, cancellationToken);
        if (sln == null) return 1;

        // Save the configuration
        Config?.Save();
        if (settings.Verbose)
            AnsiConsole.MarkupLine($"[dim]Project configuration saved to {ProjectConfig.s_configFileName}.[/]");

        // Cleanup if existing cloned output folder exists, so we download into a clean folder
        var slnFolder = Path.Combine(RootFolder, AllSolutionsFolderName, sln.Name);
        string tempClonedOutputFolder = Path.Combine(slnFolder, sln.Name);
        if (Directory.Exists(tempClonedOutputFolder))
        {
            AnsiConsole.MarkupLine($"[dim]Removing existing '/{AllSolutionsFolderName}/{sln.Name}/{sln.Name}' temp clone folder...[/]");
            Directory.Delete(tempClonedOutputFolder, true);
        }

        // Clone solution from Dataverse if it doesn't exist locally
        string solutionPackageFolder = Path.Combine(slnFolder, SolutionPackageName);
        var cdsprojPath = Path.Combine(slnFolder, SolutionPackageName, $"{SolutionPackageName}.cdsproj");
        if (!Directory.Exists(solutionPackageFolder) || !File.Exists(cdsprojPath))
        {
            AnsiConsole.MarkupLine($"No {SolutionPackageName} folder and {SolutionPackageName}.cdsproj found for '{sln.Name}' found. Cloning from Dataverse...");

            // Clone solution from Dataverse
            var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
            CommandResult result = await AnsiConsole.Status().StartAsync(
                "Connecting...",
                ctx => Cli.Wrap(cmdName)
                          .WithArguments(args => args
                              .AddIfNotNull(prefixArgs)
                              .Add("solution")
                              .Add("clone")
                              .Add("--name").Add(sln.Name)
                              .Add("--environment").Add(Config!.ProdUrl!)
                              .Add("--packagetype").Add(sln.IncludeManaged ? "Both" : "Unmanaged")
                              .Add("--outputDirectory").Add(slnFolder) // will create <sln.Name> folder under this given folder
                              .Add("--async"))
                          .WithValidation(CommandResultValidation.None)
                          .WithToolExecutionLog(settings.Verbose, ctx)
                          .ExecuteAsync(cancellationToken)
                          .Task);

            if (!result.IsSuccess)
            {
                AnsiConsole.MarkupLine("[red]Failed to clone the solution. Please check the environment and solution name.[/]");
                return 1;
            }

            // PAC creates tempClonedOutput folder 'solutions/SolutionName/SolutionName/SolutionName.cdsproj'
            // We want 'solutions/SolutionName/SolutionPackage/SolutionPackage.cdsproj'
            if (Directory.Exists(tempClonedOutputFolder))
            {
                if (settings.Verbose) AnsiConsole.MarkupLine($"[dim]Renaming folder '{tempClonedOutputFolder}' to '{solutionPackageFolder}'...[/]");
                Directory.Move(tempClonedOutputFolder, solutionPackageFolder);
            }

            var clonedCdsproj = Path.Combine(solutionPackageFolder, $"{sln.Name}.cdsproj");
            if (File.Exists(clonedCdsproj))
            {
                if (settings.Verbose) AnsiConsole.MarkupLine($"[dim]Renaming file '{clonedCdsproj}' to '{SolutionPackageName}.cdsproj'...[/]");
                File.Move(clonedCdsproj, Path.Combine(solutionPackageFolder, $"{SolutionPackageName}.cdsproj"));
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Clone in '{SolutionPackageName}' folder already exist. Skip cloning[/]");
        }

        // Create Solution file if it doesn't exist (use sln for now because slnx can't handle .cdsproj yet)
        var slnFilePath = Path.Combine(slnFolder, $"{sln.Name}.sln");
        if (!File.Exists(slnFilePath))
        {
            var result = await Cli.Wrap("dotnet")
                                  .WithArguments(args => args
                                                         .Add("new")
                                                         .Add("sln")
                                                         .Add("--name").Add(sln.Name)
                                                         .Add("--format").Add("sln"))
                                  .WithWorkingDirectory(slnFolder)
                                  .WithToolExecutionLog(settings.Verbose)
                                  .ExecuteAsync(cancellationToken)
                                  .Task.Spinner();

            if (!result.IsSuccess || !File.Exists(slnFilePath))
            {
                AnsiConsole.MarkupLine($"Failed to create solution file '{sln.Name}.sln'.");
                return 1;
            }

            AnsiConsole.MarkupLine($"[green]Created Solution file '{slnFilePath}'[/]");

            // Add SolutionPackage.cdsproj to the solution
            // NOTE: 'dotnet sln add' doesn't support .cdsproj directly.
            // We'll rename it to .csproj, add it, then rename it back and fix the .sln file.
            var csprojPath = Path.ChangeExtension(cdsprojPath, ".csproj");
            if (File.Exists(cdsprojPath))
            {
                if (settings.Verbose) AnsiConsole.MarkupLine($"[dim]Renaming '{cdsprojPath}' to '{csprojPath}'[/]");
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
                     .Task.Spinner();

            // Rename back to .cdsproj
            if (File.Exists(csprojPath))
            {
                if (settings.Verbose) AnsiConsole.MarkupLine($"[dim]Renaming '{csprojPath}' back to '{cdsprojPath}'[/]");
                File.Move(csprojPath, cdsprojPath);
            }

            // Fix the XML in the .sln file
            if (File.Exists(slnFilePath))
            {
                if (settings.Verbose) AnsiConsole.MarkupLine($"[dim]Fixing XML in .sln file...[/]");
                var slnContent = await File.ReadAllTextAsync(slnFilePath, cancellationToken);
                slnContent = slnContent.Replace($"{SolutionPackageName}.csproj", $"{SolutionPackageName}.cdsproj");
                await File.WriteAllTextAsync(slnFilePath, slnContent, cancellationToken);
            }

            AnsiConsole.MarkupLine($"[green]Added '{SolutionPackageName}.cdsproj' to solution file '{slnFilePath}'[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Solution file (sln) already exists. Skip creation[/]");
        }

        // Create Extensions (plugins) project if it doesn't exist
        var extensionsFolder = Path.Combine(slnFolder, ExtensionsName);
        var extensionsCsproj = Path.Combine(extensionsFolder, $"{ExtensionsName}.csproj");
        if (!File.Exists(extensionsCsproj))
        {
            AnsiConsole.MarkupLine("Initializing Extensions project...");
            Directory.CreateDirectory(extensionsFolder);

            var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
            await Cli.Wrap(cmdName)
                     .WithArguments(args => args
                         .AddIfNotNull(prefixArgs)
                         .Add("plugin")
                         .Add("init"))
                     .WithWorkingDirectory(extensionsFolder)
                     .WithToolExecutionLog(settings.Verbose)
                     .ExecuteAsync(cancellationToken)
                     .Task.Spinner();

            // Add Extensions.csproj to the solution
            await Cli.Wrap("dotnet")
                     .WithArguments(args => args
                                          .Add("sln")
                                          //.Add(slnFilePath)
                                          .Add("add")
                                          .Add(extensionsCsproj))
                     .WithWorkingDirectory(slnFolder)
                     .WithToolExecutionLog(settings.Verbose)
                     .ExecuteAsync(cancellationToken)
                     .Task.Spinner();

            AnsiConsole.MarkupLine("[green]Initialized Extensions project[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Extensions project (plugins) already exists. Skip creation[/]");
        }

        // Create WebResources project if it doesn't exist
        var webresourcesFolder = Path.Combine(slnFolder, WebResourcesName);
        var webresourcesCsproj = Path.Combine(webresourcesFolder, $"{WebResourcesName}.csproj");
        if (!File.Exists(webresourcesCsproj))
        {
            AnsiConsole.MarkupLine("Initializing WebResources project...");

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

            AnsiConsole.MarkupLine("[green]Initialized WebResources project[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]WebResources project already exists. Skip creation[/]");
        }

        // Create XML Mapping file in SolutionPackage folder
        var mappingFilePath = Path.Combine(solutionPackageFolder, "Mapping.xml");
        if (!File.Exists(mappingFilePath))
        {
            AnsiConsole.MarkupLine("Creating XML Mapping file...");
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
                cancellationToken).Spinner();

            AnsiConsole.MarkupLine($"[green]Created XML Mapping file at {mappingFilePath}[/]");
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]XML Mapping file already exists. Skip creation[/]");
        }

        // Build the solution in dotnet to validate it
        if (await DotNetUtils.BuildSolutionAsync(slnFolder, settings.Verbose, cancellationToken) != 0)
        {
            return 1;
        }

        AnsiConsole.MarkupLine("[bold green]:rocket: Flowline cloned it! You can now use 'push' and 'sync' to keep your solution up to date.[/]");

        return 0;
    }
}
