using CliWrap;
using Flowline.Core;
using Flowline.Utils;
using Spectre.Console;

namespace Flowline.Services;

public class XrmContextRunner(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions)
{
    public async Task RunAsync(
        string exePath,
        string solutionName,
        string[]? extraTables,
        string modelNamespace,
        string connectionString,
        string tempOutputPath,
        CancellationToken cancellationToken = default)
    {
        var args = BuildArgs(solutionName, extraTables, modelNamespace, connectionString, tempOutputPath);

        var cmd = Cli.Wrap(exePath)
            .WithArguments(args);

        await console.Status().FlowlineSpinner().StartAsync(
            $"Running XrmContext for [bold]{solutionName}[/]...",
            ctx => cmd.WithToolExecutionLog(runtimeOptions.IsVerbose, ctx).ExecuteAsync(cancellationToken).Task);
    }

    internal static string[] BuildArgs(
        string solutionName,
        string[]? extraTables,
        string modelNamespace,
        string connectionString,
        string tempOutputPath)
    {
        var args = new List<string>
        {
            $"/solutions:{solutionName}",
            $"/namespace:{modelNamespace}",
            $"/connectionString:{connectionString}",
            $"/out:{tempOutputPath}",
        };

        if (extraTables is { Length: > 0 })
            args.Add($"/entities:{string.Join(",", extraTables)}");

        return args.ToArray();
    }
}
