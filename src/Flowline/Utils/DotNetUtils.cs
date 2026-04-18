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
        // Build the solution in dotnet to validate it
        var buildResult = await AnsiConsole.Status().FlowlineSpinner().StartAsync("Building...", ctx =>
            Cli.Wrap("dotnet")
               .WithArguments(args => args.Add("build")
                                          .Add("--configuration").Add(configuration.ToString()))
               .WithWorkingDirectory(workingDirectory)
               .WithValidation(CommandResultValidation.None)
               .WithToolExecutionLog(verbose)
               .ExecuteAsync(cancellationToken).Task);

        if (!buildResult.IsSuccess)
        {
            AnsiConsole.MarkupLine("[red]Build failed — check the output above (- use --verbose)[/]");
            return 1;
        }
        else
        {
            AnsiConsole.MarkupLine("[green]Build done[/]");
            return 0;
        }
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
            AnsiConsole.MarkupLine("[red].NET SDK (dotnet) is not installed or not in PATH. Please install it from https://dotnet.microsoft.com/download.[/]");
            Environment.Exit(1);
            return string.Empty;
        }
    }
}
