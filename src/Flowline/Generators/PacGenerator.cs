using CliWrap;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Utils;
using Spectre.Console;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Flowline.Tests")]

namespace Flowline.Generators;

public class PacGenerator(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, SubprocessCapture? capture = null) : IGenerator
{
    public GeneratorType Type => GeneratorType.Pac;

    public async Task RunAsync(GenerationContext context, CancellationToken cancellationToken = default)
    {
        var entityTask = console.Status().FlowlineSpinner().StartAsync(
            "Discovering solution entities...",
            _ => GenerateReader.GetSolutionEntityLogicalNamesAsync(context.Service, context.RemoteSolution.Id, cancellationToken));
        var customApiTask = GenerateReader.GetSolutionCustomApiMessageNamesAsync(context.Service, context.RemoteSolution.Id, cancellationToken);

        var solutionEntities = await entityTask;
        var customApiNames = await customApiTask;

        var entityFilter = solutionEntities
            .Concat(context.ExtraTables)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        console.Ok($"Found [bold]{entityFilter.Count}[/] entities" +
                   (customApiNames.Count > 0 ? $", [bold]{customApiNames.Count}[/] custom APIs" : ""));

        if (context.Verbose)
        {
            foreach (var entity in entityFilter)
                console.Verbose($"  entity: {entity}");
            foreach (var api in customApiNames)
                console.Verbose($"  custom api: {api}");
        }

        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);

        var modelArgs = BuildArgs(solutionEntities, context.ExtraTables, customApiNames, context.ModelNamespace, context.TempOutputPath, context.ServiceContextName);

        var pacCommand = Cli.Wrap(cmdName)
            .WithArguments(args =>
            {
                args.AddIfNotNull(prefixArgs);
                foreach (var arg in modelArgs)
                    args.Add(arg);
            })
            .WithValidation(CommandResultValidation.None);

        var tempPrefix = context.TempOutputPath + Path.DirectorySeparatorChar;
        var outputFolderName = Path.GetFileName(context.TempOutputPath[..^1].TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string ShortenPacLine(string line) => line.Replace(tempPrefix, outputFolderName + "/");

        var result = await console.Status().FlowlineSpinner().StartAsync(
            $"Generating early-bound types into [bold]{context.OutputLabel}[/]...",
            ctx => (capture?.Apply(pacCommand, ctx, ShortenPacLine) ?? pacCommand).ExecuteAsync(cancellationToken).Task);

        if (!result.IsSuccess)
            throw new FlowlineException(ExitCode.BuildFailed, "pac modelbuilder build failed — check the output above.");

        console.Ok("Early-bound types generated");
    }

    internal static string[] BuildArgs(
        IEnumerable<string> solutionEntities,
        IEnumerable<string> extraTables,
        IReadOnlyList<string> customApiNames,
        string modelNamespace,
        string tempOutputPath,
        string? serviceContextName = null)
    {
        var entityFilter = solutionEntities
            .Concat(extraTables)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var args = new List<string>
        {
            "modelbuilder", "build",
            "-o", tempOutputPath,
            "-enf", string.Join(";", entityFilter),
            "-sgca",
            "--suppressINotifyPattern",
            "--emitfieldsclasses",
            "-n", modelNamespace
        };

        args.Add("--serviceContextName");
        args.Add(serviceContextName ?? "XrmContext");

        if (customApiNames.Count > 0)
        {
            args.Add("--generatesdkmessages");
            args.Add("--messagenamesfilter");
            args.Add(string.Join(";", customApiNames));
        }

        return args.ToArray();
    }
}
