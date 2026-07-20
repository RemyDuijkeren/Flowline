using CliWrap;
using CliWrap.Buffered;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Spectre.Console;

namespace Flowline.Utils;

/// <summary>The configuration to use when building the solution.</summary>
public enum DotnetBuild { Release, Debug }

public static class DotNetUtils
{
    public static async Task<int> BuildSolutionAsync(string workingDirectory, DotnetBuild configuration, SubprocessCapture capture, CancellationToken cancellationToken = default, bool rebuild = false)
    {
        // Not FormatRelativePath(workingDirectory) — callers sometimes cd into workingDirectory
        // itself before building, which collapses the relative path to a meaningless "./". The
        // folder's own name stays meaningful regardless of the current directory.
        var buildTarget = Path.GetFileName(workingDirectory.TrimEnd('/', '\\'));
        var statusVerb = rebuild ? "Rebuilding" : "Building";

        var buildResult = await AnsiConsole.Status().FlowlineSpinner().StartAsync($"{statusVerb} [bold]{Markup.Escape(buildTarget)}[/] ({configuration})...", ctx =>
            capture.Apply(
                Cli.Wrap("dotnet")
                   .WithArguments(BuildArguments(workingDirectory, configuration, rebuild))
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
            AnsiConsole.MarkupLine($"[green]✓[/] Build [bold]{Markup.Escape(buildTarget)}[/] done in {duration} ({configuration})");
            return 0;
        }
    }

    /// <summary>Builds the <c>dotnet build</c> argument list, naming the solution file when the folder holds one.</summary>
    /// <remarks>
    /// A bare <c>dotnet build</c> fails with MSB1011 when a folder holds both a <c>.sln</c> and a <c>.slnx</c> —
    /// exactly what <c>dotnet sln migrate</c> leaves behind. Naming the file the reader picks removes that
    /// dependency on the folder holding exactly one. Project folders (<c>Plugins/</c>, <c>WebResources/</c>) hold
    /// no solution file, so no target is named and MSBuild resolves the single project as it always has.
    /// </remarks>
    internal static IReadOnlyList<string> BuildArguments(string workingDirectory, DotnetBuild configuration, bool rebuild)
    {
        var args = new List<string> { "build" };

        var solutionFile = new MsBuildSolutionReader().FindSolutionFile(workingDirectory);
        if (solutionFile != null) args.Add(solutionFile);

        args.Add("--configuration");
        args.Add(configuration.ToString());
        if (rebuild) args.Add("-t:Rebuild");

        return args;
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
