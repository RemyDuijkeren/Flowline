using System.Text.RegularExpressions;
using CliWrap;
using CliWrap.Buffered;
using Spectre.Console;

namespace Flowline.Utils;

/// <summary>The configuration to use when building the solution.</summary>
public enum DotnetBuild { Release, Debug }

public static class DotNetUtils
{

    public static async Task<int> BuildSolutionAsync(string workingDirectory, DotnetBuild configuration, bool verbose = true, CancellationToken cancellationToken = default)
    {
        var relativeWorkingDirectory = ConsolePath.FormatRelativePath(workingDirectory);

        var buildResult = await AnsiConsole.Status().FlowlineSpinner().StartAsync($"Building {relativeWorkingDirectory}...", ctx =>
            Cli.Wrap("dotnet")
               .WithArguments(args => args.Add("build").Add("--configuration").Add(configuration.ToString()))
               .WithWorkingDirectory(workingDirectory)
               .WithValidation(CommandResultValidation.None)
               .WithToolExecutionLog(verbose)
               .ExecuteAsync(cancellationToken).Task);

        if (!buildResult.IsSuccess)
        {
            AnsiConsole.MarkupLine("[red]Build failed — check the output above. Use --verbose for details.[/]");
            return 1;
        }
        else
        {
            AnsiConsole.MarkupLine($"Build {relativeWorkingDirectory} done");
            return 0;
        }
    }

    public static async Task<int> EnsureMapFilePathAsync(string cdsprojPath, bool useMapping, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(cdsprojPath))
        {
            AnsiConsole.MarkupLine($"[red]No .cdsproj found at '{cdsprojPath}'.[/]");
            return 1;
        }

        var content = await File.ReadAllTextAsync(cdsprojPath, cancellationToken);
        var hasMapping = content.Contains("SolutionPackageMapFilePath");

        if (useMapping == hasMapping) return 0;

        if (useMapping)
            content = content.Replace("</Project>",
                "  <PropertyGroup>\n    <SolutionPackageMapFilePath>$(MSBuildProjectDirectory)\\MappingBuild.xml</SolutionPackageMapFilePath>\n  </PropertyGroup>\n</Project>");
        else
            content = Regex.Replace(content,
                @"\s*<PropertyGroup>\s*<SolutionPackageMapFilePath>[^<]*</SolutionPackageMapFilePath>\s*</PropertyGroup>",
                "");

        await File.WriteAllTextAsync(cdsprojPath, content, cancellationToken);
        return 0;
    }

    public static async Task<string> AssertDotNetInstalledAsync(bool verbose = true, CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Cli.Wrap("dotnet")
                                  .WithArguments("--version")
                                  .ExecuteBufferedAsync(cancellationToken);

            var version = result.StandardOutput.Trim();
            AnsiConsole.MarkupLine(".NET's good");
            if (verbose)
            {
                AnsiConsole.MarkupLine($"[dim].NET SDK version: {version}[/]");
            }
            return version;
        }
        catch (Exception)
        {
            AnsiConsole.MarkupLine("[red].NET SDK isn't available. Install it from https://dotnet.microsoft.com/download.[/]");
            Environment.Exit(1);
            return string.Empty;
        }
    }
}
