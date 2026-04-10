using System.ComponentModel;
using CliWrap;
using Flowline.Config;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class CloneCommand : AsyncCommand<CloneCommand.Settings>
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<solution>")]
        [Description("The solution to clone into the repo")]
        [DefaultValue("Cr07982")]
        public string? Solution { get; set; } = "Cr07982";

        [CommandOption("--prod <environment-url>")]
        [Description("The production environment to clone the solution from")]
        public string? ProdUrl { get; set; }

        [CommandOption("--managed")]
        [Description("Also clone managed artifacts in addition to unmanaged")]
        public bool IncludeManaged { get; set; } = false;

        // - `--dev <url>`: save the development environment URL into `.flowconfig`
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        await GitUtils.AssertGitInstalledAsync(cancellationToken);
        await PacUtils.AssertPacCliInstalledAsync(cancellationToken);

        var rootFolder = Directory.GetCurrentDirectory();
        await GitUtils.AssertGitRepoAsync(rootFolder, cancellationToken);

        // Load or create the project configuration
        var config = ProjectConfig.Load();
        if (config != null)
        {
            AnsiConsole.MarkupLine("[yellow]Project configuration already exists.[/]");
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
            AnsiConsole.MarkupLine("[red]Production environment is required. Please provide a Dataverse environment URL using --prod <environment-url>.[/]");
            return 1;
        }

        // Validate Prod URL
        var srcEnvironment = await PacUtils.GetEnvironmentInfoByUrlAsync(prodUrl, cancellationToken);
        if (srcEnvironment == null)
        {
            AnsiConsole.MarkupLine("[red]Invalid Production environment. Please provide a valid Dataverse environment URL using --prod <environment-url>.[/]");
            return 1;
        }

        if (srcEnvironment.Type != "Production")
        {
            AnsiConsole.MarkupLine("[red]Production environment must be of type 'Production'.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine($"  Using Production environment: [bold]{srcEnvironment.DisplayName}[/] ({srcEnvironment.EnvironmentUrl}) - Type: {srcEnvironment.Type})");

        // Solution name is required
        var sln = config.GetOrUpdateSolution(settings.Solution, settings.IncludeManaged, settings);
        if (sln == null)
        {
            AnsiConsole.MarkupLine("[red]Solution name is required. Please provide a solution name using 'clone <solutionName>'.[/]");
            return 1;
        }

        config.Save();
        AnsiConsole.MarkupLine($"Project configuration saved to {ProjectConfig.s_configFileName}.");

        // Clone solution from Dataverse if it doesn't exist locally
        var slnFolder = Path.Combine(rootFolder, "solutions", sln.Name);
        var packageFolder = Path.Combine(slnFolder, "SolutionPackage");
        var cdsprojPath = Path.Combine(packageFolder, "SolutionPackage.cdsproj");
        if (!File.Exists(cdsprojPath))
        {
            AnsiConsole.MarkupLine($"No solution folder for '{sln.Name}' found. Cloning from Dataverse...");

            if (Directory.Exists(slnFolder))
            {
                AnsiConsole.MarkupLine("Removing existing solution folder...");
                Directory.Delete(slnFolder, true);
            }

            var solutionsRoot = Path.Combine(rootFolder, "solutions");
            var result = await Cli.Wrap("pac")
                                  .WithArguments(args => args
                                                         .Add("solution")
                                                         .Add("clone")
                                                         .Add("--name").Add(sln.Name)
                                                         .Add("--async")
                                                         .Add("--environment").Add(config.ProdUrl)
                                                         .Add("--packagetype").Add(sln.IncludeManaged ? "Both" : "Unmanaged")
                                                         .Add("--outputDirectory").Add(solutionsRoot))
                                  .WithStandardOutputPipe(PipeTarget.ToDelegate(s => AnsiConsole.MarkupLineInterpolated($"[dim]PAC: {s}[/]")))
                                  .WithStandardErrorPipe(PipeTarget.ToDelegate(Console.Error.WriteLine))
                                  .ExecuteAsync(cancellationToken);

            if (result.ExitCode != 0)
            {
                AnsiConsole.MarkupLine("[red]Failed to clone the solution. Please check the environment and solution name.[/]");
                return 1;
            }

            // PAC creates solutions/SolutionName/SolutionName.cdsproj
            // We want solutions/SolutionName/SolutionPackage/SolutionPackage.cdsproj
            var tempFolder = Path.Combine(slnFolder, "SolutionPackage");
            Directory.CreateDirectory(tempFolder);

            // Move all files and folders from slnFolder to tempFolder, except tempFolder itself
            foreach (var dir in Directory.GetDirectories(slnFolder))
            {
                if (dir == tempFolder) continue;
                Directory.Move(dir, Path.Combine(tempFolder, Path.GetFileName(dir)));
            }
            foreach (var file in Directory.GetFiles(slnFolder))
            {
                File.Move(file, Path.Combine(tempFolder, Path.GetFileName(file)));
            }

            // Rename .cdsproj
            var oldCdsproj = Path.Combine(tempFolder, $"{sln.Name}.cdsproj");
            if (File.Exists(oldCdsproj))
            {
                File.Move(oldCdsproj, Path.Combine(tempFolder, "SolutionPackage.cdsproj"));
            }

            // Create Solution file if it doesn't exist
            var slnFilePath = Path.Combine(slnFolder, $"{sln.Name}.sln");
            if (!File.Exists(slnFilePath))
            {
                AnsiConsole.MarkupLine($"Creating solution file '{sln.Name}.sln'...");
                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                              .Add("new")
                                              .Add("sln")
                                              .Add("--name")
                                              .Add(sln.Name)
                                              .Add("--output")
                                              .Add(slnFolder))
                         .ExecuteAsync(cancellationToken);

                // Add SolutionPackage.cdsproj to the solution
                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                              .Add("sln")
                                              .Add(slnFilePath)
                                              .Add("add")
                                              .Add(cdsprojPath))
                         .ExecuteAsync(cancellationToken);
            }

            // Create Extensions project if it doesn't exist
            var extensionsFolder = Path.Combine(slnFolder, "Extensions");
            var extensionsCsproj = Path.Combine(extensionsFolder, "Extensions.csproj");
            if (!File.Exists(extensionsCsproj))
            {
                AnsiConsole.MarkupLine("Initializing Extensions project...");
                Directory.CreateDirectory(extensionsFolder);
                await Cli.Wrap("pac")
                         .WithArguments(args => args
                                              .Add("plugin")
                                              .Add("init")
                                              .Add("--outputDirectory")
                                              .Add(extensionsFolder))
                         .ExecuteAsync(cancellationToken);

                // Rename the .csproj created by pac plugin init (it uses the folder name)
                var pacGeneratedCsproj = Directory.GetFiles(extensionsFolder, "*.csproj").FirstOrDefault();
                if (pacGeneratedCsproj != null && Path.GetFileName(pacGeneratedCsproj) != "Extensions.csproj")
                {
                    File.Move(pacGeneratedCsproj, extensionsCsproj);
                }

                // Add Extensions.csproj to the solution
                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                              .Add("sln")
                                              .Add(slnFilePath)
                                              .Add("add")
                                              .Add(extensionsCsproj))
                         .ExecuteAsync(cancellationToken);
            }

            // Create WebResources project if it doesn't exist
            var webresourcesFolder = Path.Combine(slnFolder, "WebResources");
            var webresourcesCsproj = Path.Combine(webresourcesFolder, "WebResources.csproj");
            if (!File.Exists(webresourcesCsproj))
            {
                AnsiConsole.MarkupLine("Initializing WebResources project...");
                Directory.CreateDirectory(webresourcesFolder);
                Directory.CreateDirectory(Path.Combine(webresourcesFolder, "src"));
                Directory.CreateDirectory(Path.Combine(webresourcesFolder, "public"));
                Directory.CreateDirectory(Path.Combine(webresourcesFolder, "dist"));

                // Create a basic class library for WebResources.csproj
                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                              .Add("new")
                                              .Add("classlib")
                                              .Add("--name")
                                              .Add("WebResources")
                                              .Add("--output")
                                              .Add(webresourcesFolder)
                                              .Add("--force"))
                         .ExecuteAsync(cancellationToken);

                // Add WebResources.csproj to the solution
                await Cli.Wrap("dotnet")
                         .WithArguments(args => args
                                              .Add("sln")
                                              .Add(slnFilePath)
                                              .Add("add")
                                              .Add(webresourcesCsproj))
                         .ExecuteAsync(cancellationToken);
            }
        }
        else
        {
            AnsiConsole.MarkupLine($"[yellow]Found 'SolutionPackage.cdsproj'. Solution already cloned locally.[/]");
        }

        AnsiConsole.MarkupLine("[green]Initialization complete! You can now use 'push' and 'sync' to keep your solution up to date.[/]");

        return 0;
    }
}
