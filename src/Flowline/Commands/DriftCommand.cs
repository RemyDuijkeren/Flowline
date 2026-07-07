using System.ComponentModel;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Services;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class DriftCommand(IAnsiConsole console, DataverseConnector dataverseConnector, OrphanCleanupService orphanCleanupService, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture) : FlowlineCommand<DriftCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "<target>")]
        [Description("Target environment: prod, uat, test, or dev")]
        public string Target { get; set; } = null!;

        [CommandArgument(1, "[solution]")]
        [Description("Solution to check (optional in project mode)")]
        public string? Solution { get; set; }
    }

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var role = ResolveRole(settings.Target);

        var env = await GetAndCheckEnvironmentInfoAsync(role, null, settings, cancellationToken);
        var (service, _) = await ConnectToDataverseAsync(dataverseConnector, env.EnvironmentUrl!, cancellationToken);
        var (projectSln, _) = await GetAndCheckSolutionAsync(settings.Solution, env.EnvironmentUrl!, includeManaged: null, settings, cancellationToken);

        var slnFolder = Path.Combine(RootFolder, "solutions", projectSln.Name);
        var packageFolder = PackageFolder(slnFolder);
        var packageSrcRoot = Path.Combine(packageFolder, "src");
        var (localComponents, entityLogicalNames, namedComponents) = ComponentClassifier.ParseLocalSource(packageFolder);

        var postDeployContext = new PostDeployContext(service, projectSln.Name, localComponents, RunMode.NoDelete, packageSrcRoot, env.EnvironmentUrl!, entityLogicalNames, packageSrcRoot, namedComponents);

        var entries = await orphanCleanupService.CompareAsync(postDeployContext, cancellationToken);

        return SelectExitCode(entries.Count);
    }

    internal static EnvironmentRole ResolveRole(string target) => target.ToLowerInvariant() switch
    {
        "prod" => EnvironmentRole.Prod,
        "uat"  => EnvironmentRole.Uat,
        "test" => EnvironmentRole.Test,
        "dev"  => EnvironmentRole.Dev,
        _      => throw new FlowlineException(ExitCode.ConfigInvalid, $"Unknown target '{target}' — use prod, uat, test, or dev.")
    };

    internal static int SelectExitCode(int driftEntryCount) =>
        driftEntryCount == 0 ? (int)ExitCode.Success : (int)ExitCode.ValidationFailed;
}
