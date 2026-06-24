using Flowline.Config;
using Flowline.Core;
using Flowline.Services;

namespace Flowline.Generators;

public class XrmContext3Generator(
    FlowlineRuntimeOptions runtimeOptions,
    XrmContextToolProvider xrmContextToolProvider,
    XrmContextRunner xrmContextRunner)
    : IGenerator
{
    public GeneratorType Type => GeneratorType.XrmContext3;

    public async Task RunAsync(GenerationContext context, CancellationToken cancellationToken = default)
    {
        if (context.XrmContextAuth is null)
            throw new FlowlineException(ExitCode.NotAuthenticated,
                "XrmContext3 generator requires auth — check that your PAC profile is configured for this environment.");

        var exePath = await xrmContextToolProvider.GetExePathAsync(cancellationToken);

        await xrmContextRunner.RunAsync(
            exePath: exePath,
            environmentUrl: context.DevUrl,
            auth: context.XrmContextAuth,
            solutionName: context.SolutionName,
            extraTables: context.ExtraTables.Length > 0 ? context.ExtraTables : null,
            modelNamespace: context.ModelNamespace,
            tempOutputPath: context.TempOutputPath,
            serviceContextName: context.ServiceContextName ?? "XrmContext",
            cancellationToken: cancellationToken);
    }
}
