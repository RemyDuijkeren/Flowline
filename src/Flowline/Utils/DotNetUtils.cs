using CliWrap;
using CliWrap.Buffered;
using Spectre.Console;

namespace Flowline.Utils;

public static class DotNetUtils
{
    public static async Task<int> BuildSolutionAsync(string workingDirectory, bool verbose = true, CancellationToken cancellationToken = default)
    {
        // Build the solution in dotnet to validate it
        var buildResult = await AnsiConsole.Status().FlowlineSpinner().StartAsync("Building solution...", ctx =>
            Cli.Wrap("dotnet")
               .WithArguments(args => args.Add("build"))
               .WithWorkingDirectory(workingDirectory)
               .WithToolExecutionLog(verbose)
               .ExecuteAsync(cancellationToken).Task);

        if (!buildResult.IsSuccess)
        {
            AnsiConsole.MarkupLine($"[red]Failed to build solution. Please check the logs for more details.[/]");
            return 1;
        }
        else
        {
            AnsiConsole.MarkupLine("[green]Solution built successfully[/]");
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
            AnsiConsole.MarkupLine(".NET is ready to build");
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

    public static async Task<bool> IsDotNetInstalledAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await Cli.Wrap("dotnet")
                                  .WithArguments("--version")
                                  .ExecuteBufferedAsync(cancellationToken);

            return result.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
