using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using CliWrap;
using CliWrap.Buffered;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Services;
using Flowline.Utils;
using Spectre.Console;

[assembly: InternalsVisibleTo("Flowline.Tests")]

namespace Flowline.Generators;

public class XrmContextGenerator(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, SubprocessCapture? capture = null)
    : IGenerator
{
    static (string Command, string[]? PrefixArgs, string[]? SuffixArgs)? _cachedXrmContextCommand;

    internal static Func<string, string[], CancellationToken, Task<(bool Success, string Output)>>? CheckCommandExistsFunc { get; set; }

    internal static void ResetCache() => _cachedXrmContextCommand = null;

    public GeneratorType Type => GeneratorType.XrmContext;

    public async Task RunAsync(GenerationContext context, CancellationToken cancellationToken = default)
    {
        var (cmdName, prefixArgs, suffixArgs) = await GetBestXrmContextCommandAsync(cancellationToken);

        var tempAppsettingsDir = Path.Combine(Path.GetTempPath(), $"flowline-xrmcontext-{Guid.NewGuid()}");

        try
        {
            Directory.CreateDirectory(tempAppsettingsDir);

            var json = BuildAppSettingsJson(context.DevUrl, context.TempOutputPath, context.ModelNamespace, context.SolutionName, context.ExtraTables, context.ServiceContextName ?? "XrmContext");
            await File.WriteAllTextAsync(Path.Combine(tempAppsettingsDir, "appsettings.json"), json, cancellationToken);

            var envVars = BuildEnvVars(context.ResolvedProfile, context.ResolvedSecret);

            var command = Cli.Wrap(cmdName)
                .WithArguments(args =>
                {
                    args.AddIfNotNull(prefixArgs);
                    args.AddIfNotNull(suffixArgs);
                })
                .WithWorkingDirectory(tempAppsettingsDir)
                .WithEnvironmentVariables(envVars)
                .WithValidation(CommandResultValidation.None);

            var result = await ExecuteCommandAsync(command, context, cancellationToken);

            if (!result.IsSuccess)
                throw new FlowlineException(ExitCode.BuildFailed, "xrmcontext failed — check the output above.");
        }
        finally
        {
            if (Directory.Exists(tempAppsettingsDir))
                Directory.Delete(tempAppsettingsDir, recursive: true);
        }
    }

    internal virtual Task<CommandResult> ExecuteCommandAsync(Command command, GenerationContext context, CancellationToken cancellationToken)
    {
        return console.Status().FlowlineSpinner().StartAsync(
            $"Generating early-bound types into [bold]{context.OutputLabel}[/]...",
            ctx => (capture?.Apply(command, ctx) ?? command).ExecuteAsync(cancellationToken).Task);
    }

    internal static string BuildAppSettingsJson(string devUrl, string outputDirectory, string modelNamespace, string solutionName, string[] extraTables, string serviceContextName = "XrmContext")
    {
        var xrmContext = new JsonObject
        {
            ["OutputDirectory"] = outputDirectory,
            ["NamespaceSetting"] = modelNamespace,
            ["ServiceContextName"] = serviceContextName,
            ["Solutions"] = new JsonArray(solutionName),
            ["GenerateCustomApis"] = true,
            ["DeprecatedPrefix"] = "ZZ_",
        };

        if (extraTables is { Length: > 0 })
        {
            var entitiesArray = new JsonArray();
            foreach (var table in extraTables)
                entitiesArray.Add(table);
            xrmContext["Entities"] = entitiesArray;
        }

        var root = new JsonObject
        {
            ["DATAVERSE_URL"] = devUrl,
            ["XrmContext"] = xrmContext,
        };

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    internal static IReadOnlyDictionary<string, string?> BuildEnvVars(PacProfile? profile, string? resolvedSecret = null)
    {
        var envVars = new Dictionary<string, string?>();

        if (profile?.IsServicePrincipal == true)
        {
            if (profile.ApplicationId is not null)
                envVars["AZURE_CLIENT_ID"] = profile.ApplicationId;

            if (profile.TenantId is not null)
                envVars["AZURE_TENANT_ID"] = profile.TenantId;

            // resolvedSecret (from --secret flag or SecretResolver) takes precedence for subprocess.
            // If not provided, fall back to AZURE_CLIENT_SECRET from parent env.
            var secret = resolvedSecret ?? Environment.GetEnvironmentVariable("AZURE_CLIENT_SECRET");
            if (secret is not null)
                envVars["AZURE_CLIENT_SECRET"] = secret;
        }

        return envVars;
    }

    internal static async Task<(string Command, string[]? PrefixArgs, string[]? SuffixArgs)> GetBestXrmContextCommandAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedXrmContextCommand != null) return _cachedXrmContextCommand.Value;

        // 1. Check for 'dnx' — dnx XrmContext [args] --prerelease
        var dnxCheck = await CheckCommandExistsAsync("dnx", ["--help"], cancellationToken);
        if (dnxCheck.Success)
        {
            _cachedXrmContextCommand = ("dnx", ["XrmContext"], ["--prerelease"]);
            return _cachedXrmContextCommand.Value;
        }

        // 2. Check for 'dotnet tool run xrmcontext'
        var dotnetCheck = await CheckCommandExistsAsync("dotnet", ["tool", "run", "xrmcontext", "--help"], cancellationToken);
        if (dotnetCheck.Success)
        {
            _cachedXrmContextCommand = ("dotnet", ["tool", "run", "xrmcontext"], null);
            return _cachedXrmContextCommand.Value;
        }

        throw new FlowlineException(ExitCode.BuildFailed, "XrmContext not found. Install it with: dotnet tool install -g XrmContext");
    }

    static async Task<(bool Success, string Output)> CheckCommandExistsAsync(string command, string[] args, CancellationToken cancellationToken)
    {
        if (CheckCommandExistsFunc != null)
            return await CheckCommandExistsFunc(command, args, cancellationToken);

        try
        {
            var result = await Cli.Wrap(command)
                .WithArguments(args)
                .ExecuteBufferedAsync(cancellationToken);
            return (result.ExitCode == 0, result.StandardOutput + result.StandardError);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return (false, string.Empty);
        }
    }
}
