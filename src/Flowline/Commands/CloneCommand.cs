using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Flowline.Utils;
using Spectre.Console;
using Spectre.Console.Cli;
using Spectre.Console.Extensions;
using Command = CliWrap.Command;

namespace Flowline.Commands;

public class CloneCommand : AsyncCommand<CloneCommand.Settings>
{
    const string AllSolutionsFolderName = "solutions";
    const string SolutionPackageName = "SolutionPackage";

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

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var rootFolder = Directory.GetCurrentDirectory();
        await AnsiConsole.Status().StartAsync("Validating environment...", async ctx =>
        {
            await DotNetUtils.AssertDotNetInstalledAsync(settings.Verbose, cancellationToken);
            await PacUtils.AssertPacCliInstalledAsync(settings.Verbose, cancellationToken);
            await GitUtils.AssertGitInstalledAsync(settings.Verbose, cancellationToken);
            await GitUtils.AssertGitRepoAsync(rootFolder, settings.Verbose, cancellationToken);
        });

        AnsiConsole.MarkupLine("[green]Valid Environment[/]");

        // Load or create the project configuration
        var config = ProjectConfig.Load();
        if (config != null)
        {
            AnsiConsole.MarkupLine("[yellow]Project configuration already exists. Skip creating[/]");
        }
        else
        {
            AnsiConsole.MarkupLine("No project configuration found. Creating...");
            config = new ProjectConfig();
        }

        // Production URL is required
        var prodUrl = config.GetOrUpdateProdUrl(settings.ProdUrl, settings);
        if (prodUrl == null)
        {
            AnsiConsole.MarkupLine("[red]Production environment is required. Please provide a Dataverse environment URL using --prod <URL>.[/]");
            return 1;
        }

        // Validate Prod URL
        EnvironmentInfo? srcEnvironment = await AnsiConsole.Status().StartAsync(
            $"Validating [bold]'{prodUrl}'[/]...",
            ctx => PacUtils.GetEnvironmentInfoByUrlAsync(prodUrl, settings.Verbose, cancellationToken));

        if (srcEnvironment == null)
        {
            AnsiConsole.MarkupLine("[red]Invalid Production environment. Please provide a valid Dataverse environment URL using --prod <URL>.[/]");
            return 1;
        }

        if (srcEnvironment.Type != "Production")
        {
            AnsiConsole.MarkupLine("[red]Production environment must be of type 'Production'.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"[green]Valid Production environment: [bold]{srcEnvironment.DisplayName}[/] ({srcEnvironment.EnvironmentUrl}, Type: {srcEnvironment.Type})[/]");

        // Solution name is required
        var sln = config.GetOrUpdateSolution(settings.Solution, settings.IncludeManaged, settings);
        if (sln == null)
        {
            AnsiConsole.MarkupLine("[red]Unexpected error: Solution could not be resolved.[/]");
            return 1;
        }

        // Validate Solution
        List<SolutionInfo> solutions = await AnsiConsole.Status().StartAsync(
            $"Validating solution [bold]'{sln.Name}'[/]...",
            ctx => PacUtils.GetSolutionsAsync(prodUrl, settings.Verbose, cancellationToken));

        var remoteSolution = solutions.FirstOrDefault(s => s.SolutionUniqueName?.Equals(sln.Name, StringComparison.OrdinalIgnoreCase) == true);
        if (remoteSolution == null)
        {
            AnsiConsole.MarkupLine($"[red]Solution '{sln.Name}' not found in environment '{prodUrl}'.[/]");
            return 1;
        }

        if (remoteSolution.IsManaged && !settings.IncludeManaged)
        {
            AnsiConsole.MarkupLine($"[red]Solution '{sln.Name}' is managed amd --include-managed is not specified. We need dev environment to clone unmanaged artifacts.[/]");
            return 0;
        }

        AnsiConsole.MarkupLine($"[green]Valid Solution: [bold]'{sln.Name}'[/] ({remoteSolution.SolutionUniqueName}, Managed: {remoteSolution.IsManaged})[/]");

        // Save the configuration
        config.Save();
        if (settings.Verbose)
            AnsiConsole.MarkupLine($"[dim]Project configuration saved to {ProjectConfig.s_configFileName}.[/]");

        // Cleanup if existing cloned output folder exists, so we download into a clean folder
        var slnFolder = Path.Combine(rootFolder, AllSolutionsFolderName, sln.Name);
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
            Command pacSolutionCloneCmd = Cli.Wrap(cmdName)
                .WithArguments(args => args
                    .AddIfNotNull(prefixArgs)
                    .Add("solution")
                    .Add("clone")
                    .Add("--name").Add(sln.Name)
                    .Add("--environment").Add(config.ProdUrl!)
                    .Add("--packagetype").Add(sln.IncludeManaged ? "Both" : "Unmanaged")
                    .Add("--outputDirectory").Add(slnFolder) // will create <sln.Name> folder under this given folder
                    .Add("--async"))
                .WithValidation(CommandResultValidation.None)
                .WithStandardErrorPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[red]PAC: {s}[/]")))
                .WithToolExecutionLog();

            string? errorMsg = null;
            CommandResult result = await AnsiConsole.Status().StartAsync(
                "Connecting...",
                ctx => pacSolutionCloneCmd
                       .WithStandardOutputPipe(PipeTarget.ToDelegate(s =>
                       {
                           if (!string.IsNullOrWhiteSpace(s))
                               ctx.Status(s.StartsWith("Processing asynchronous operation...") ? $"Cloning... {s}" : s);

                           if (settings.Verbose)
                           {
                               if (!s.StartsWith("Processing asynchronous operation..."))
                                   AnsiConsole.MarkupLineInterpolated($"[dim]PAC: {s}[/]");
                           }

                           if (s.Contains("Error: ") || s.Contains("The reason given was: ")) errorMsg += s;
                       }))
                       .ExecuteAsync(cancellationToken).Task);

            if (!result.IsSuccess)
            {
                AnsiConsole.MarkupLine("[red]Failed to clone the solution. Please check the environment and solution name.[/]");
                if (errorMsg != null)
                {
                    AnsiConsole.MarkupLine($"[red]{errorMsg}[/]");
                }
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
            AnsiConsole.MarkupLine($"[yellow]Solution file (sln) already exists. Skip creation.[/]");
        }

        // // Create Extensions project if it doesn't exist
        // var extensionsFolder = Path.Combine(slnFolder, "Extensions");
        // var extensionsCsproj = Path.Combine(extensionsFolder, "Extensions.csproj");
        // if (!File.Exists(extensionsCsproj))
        // {
        //     AnsiConsole.MarkupLine("Initializing Extensions project...");
        //     Directory.CreateDirectory(extensionsFolder);
        //     await Cli.Wrap("pac")
        //              .WithArguments(args => args
        //                                   .Add("plugin")
        //                                   .Add("init")
        //                                   .Add("--outputDirectory")
        //                                   .Add(extensionsFolder))
        //              .ExecuteAsync(cancellationToken);
        //
        //     // Rename the .csproj created by pac plugin init (it uses the folder name)
        //     var pacGeneratedCsproj = Directory.GetFiles(extensionsFolder, "*.csproj").FirstOrDefault();
        //     if (pacGeneratedCsproj != null && Path.GetFileName(pacGeneratedCsproj) != "Extensions.csproj")
        //     {
        //         File.Move(pacGeneratedCsproj, extensionsCsproj);
        //     }
        //
        //     // Add Extensions.csproj to the solution
        //     await Cli.Wrap("dotnet")
        //              .WithArguments(args => args
        //                                   .Add("sln")
        //                                   .Add(slnFilePath)
        //                                   .Add("add")
        //                                   .Add(extensionsCsproj))
        //              .ExecuteAsync(cancellationToken);
        // }
        //
        // // Create WebResources project if it doesn't exist
        // var webresourcesFolder = Path.Combine(slnFolder, "WebResources");
        // var webresourcesCsproj = Path.Combine(webresourcesFolder, "WebResources.csproj");
        // if (!File.Exists(webresourcesCsproj))
        // {
        //     AnsiConsole.MarkupLine("Initializing WebResources project...");
        //     Directory.CreateDirectory(webresourcesFolder);
        //     Directory.CreateDirectory(Path.Combine(webresourcesFolder, "src"));
        //     Directory.CreateDirectory(Path.Combine(webresourcesFolder, "public"));
        //     Directory.CreateDirectory(Path.Combine(webresourcesFolder, "dist"));
        //
        //     // Create a basic class library for WebResources.csproj
        //     await Cli.Wrap("dotnet")
        //              .WithArguments(args => args
        //                                   .Add("new")
        //                                   .Add("classlib")
        //                                   .Add("--name")
        //                                   .Add("WebResources")
        //                                   .Add("--output")
        //                                   .Add(webresourcesFolder)
        //                                   .Add("--force"))
        //              .ExecuteAsync(cancellationToken);
        //
        //     // Add WebResources.csproj to the solution
        //     await Cli.Wrap("dotnet")
        //              .WithArguments(args => args
        //                                   .Add("sln")
        //                                   .Add(slnFilePath)
        //                                   .Add("add")
        //                                   .Add(webresourcesCsproj))
        //              .ExecuteAsync(cancellationToken);
        // }

        AnsiConsole.MarkupLine("[green]Initialization complete! You can now use 'push' and 'sync' to keep your solution up to date.[/]");

        return 0;
    }
}
