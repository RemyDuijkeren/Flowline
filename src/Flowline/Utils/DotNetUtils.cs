using CliWrap;
using CliWrap.Buffered;
using Flowline.Core;
using Flowline.Diagnostics;
using Spectre.Console;

namespace Flowline.Utils;

/// <summary>The configuration to use when building the solution.</summary>
public enum DotnetBuild { Release, Debug }

public static class DotNetUtils
{

    public static async Task<int> BuildSolutionAsync(string workingDirectory, DotnetBuild configuration, SubprocessCapture capture, CancellationToken cancellationToken = default, bool rebuild = false)
    {
        var relativeWorkingDirectory = ConsolePath.FormatRelativePath(workingDirectory);
        var statusVerb = rebuild ? "Rebuilding" : "Building";

        var buildResult = await AnsiConsole.Status().FlowlineSpinner().StartAsync($"{statusVerb} {relativeWorkingDirectory}...", ctx =>
            capture.Apply(
                Cli.Wrap("dotnet")
                   .WithArguments(args =>
                   {
                       args.Add("build").Add("--configuration").Add(configuration.ToString());
                       if (rebuild) args.Add("-t:Rebuild");
                   })
                   .WithWorkingDirectory(workingDirectory)
                   .WithValidation(CommandResultValidation.None))
               .ExecuteAsync(cancellationToken).Task);

        if (!buildResult.IsSuccess)
        {
            AnsiConsole.MarkupLine("[red]Build failed — check the output above. Use --verbose for details.[/]");
            return 1;
        }
        else
        {
            var elapsed = buildResult.RunTime;
        var duration = elapsed.TotalMinutes >= 1 ? $"{(int)elapsed.TotalMinutes}m {elapsed.Seconds}s" : $"{(int)elapsed.TotalSeconds}s";
        AnsiConsole.MarkupLine($"[green]✓[/] Build {relativeWorkingDirectory} done in {duration}");
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
            AnsiConsole.MarkupLine("[red].NET SDK isn't available. Install it from https://dotnet.microsoft.com/download.[/]");
            Environment.Exit(1);
            return string.Empty;
        }
    }
}
