using System.ComponentModel;
using CliWrap;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Diagnostics;
using Flowline.Services;
using Flowline.Utils;
using Flowline.Validation;
using Spectre.Console;
using Microsoft.Extensions.Logging;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public enum Role { Dev, Test, Uat }

public enum CopyType { Minimal, Full }

public class ProvisionCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions, ProfileResolutionService profileResolutionService, ILoggerFactory loggerFactory, SubprocessCapture capture) : FlowlineCommand<ProvisionCommand.Settings>(console, runtimeOptions, profileResolutionService, loggerFactory, capture)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "[role]")]
        [Description("Target role: dev, test, or uat")]
        [DefaultValue(Role.Dev)]
        public Role Role { get; set; } = Role.Dev; // dev|test|uat

        [CommandOption("--prod <URL>")]
        [Description("Production environment URL to copy from")]
        public string? ProdUrl { get; set; }

        [CommandOption("--copy <minimal|full>")]
        [Description("Copy with data (full) or no data (minimal) from prod (default: minimal for dev, full for test)")]
        public CopyType? CopyType { get; set; }

        [CommandOption("--suffix <suffix>")]
        [Description("Target URL suffix  (default: <role name>)")]
        public string? Suffix { get; set; }

        [CommandOption("--allow-overwrite")]
        [Description("Overwrite an existing target")]
        [DefaultValue(false)]
        public bool AllowOverwrite { get; set; } = false;
    }

    protected override string[] ValidForceSpecifiers => FlowlineSettings.ConfigOnlyValidSpecifiers;

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Production URL is required
        var (prodEnv, _) = await GetAndCheckEnvironmentInfoAsync(EnvironmentRole.Prod, settings.ProdUrl, settings, cancellationToken);

        // Prepare the target environment name and url
        var suffix = string.IsNullOrWhiteSpace(settings.Suffix)
            ? settings.Role switch { Role.Dev => "Dev", Role.Uat => "UAT", _ => "Test" }
            : settings.Suffix;
        var targetDisplayName = $"{prodEnv.DisplayName} {suffix}";
        EnvironmentUrlParts urlParts = PacUtils.GetPartsFromEnvUrl(prodEnv.EnvironmentUrl!);
        var targetUrl = $"https://{urlParts.Organization}-{suffix.ToLower()}.{urlParts.Host}/";
        Logger.LogInformation("source={ProdUrl} target={TargetUrl} role={Role}", prodEnv.EnvironmentUrl, targetUrl, settings.Role);

        var environmentRole = settings.Role switch
        {
            Role.Dev  => EnvironmentRole.Dev,
            Role.Test => EnvironmentRole.Test,
            Role.Uat  => EnvironmentRole.Uat,
            _ => throw new ArgumentOutOfRangeException(nameof(settings.Role))
        };
        string? url = GetOrUpdateUrl(environmentRole, targetUrl, settings);

        if (url == null)
        {
            Console.Error("Couldn't build a valid target URL — check your .flowline config");
            return (int)ExitCode.ConfigInvalid;
        }

        // Guard: config-stored URL might be from a previous provision in a different region
        var storedUrlParts = PacUtils.GetPartsFromEnvUrl(url);
        if (!string.Equals(storedUrlParts.Region, urlParts.Region, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error($"[bold]{settings.Role}[/] URL in .flowline is in '{storedUrlParts.Region}' but prod is in '{urlParts.Region}' — cross-region copy isn't supported. Use environments in the same region.");
            return (int)ExitCode.ValidationFailed;
        }

        // Validate target environment
        var targetEnv = await FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(targetUrl, settings, cancellationToken);
        if (targetEnv == null)
        {
            var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
            var createResult = await Console.Status().FlowlineSpinner().StartAsync(
                $"Creating [bold]{targetDisplayName}[/]...",
                _ => Cli.Wrap(cmdName)
                        .WithArguments(args => args
                                               .AddIfNotNull(prefixArgs)
                                               .Add("admin")
                                               .Add("create")
                                               .Add("--name").Add($"{targetDisplayName} (cloning)")
                                               .Add("--type").Add("Sandbox")
                                               .Add("--domain").Add($"{urlParts.Organization}-{suffix.ToLower()}")
                                               .Add("--region").Add(urlParts.Region))
                        .WithValidation(CommandResultValidation.None)
                        .WithCapture(_capture)
                        .ExecuteAsync(cancellationToken)
                        .Task);

            if (!createResult.IsSuccess)
                throw new FlowlineException(ExitCode.GeneralError, "Environment creation failed — check the environment and your PAC login. Use --verbose for more details.");

            targetEnv = await FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(targetUrl, settings, cancellationToken);
            if (targetEnv == null)
            {
                Console.Error("Environment created but not found — check the Power Platform admin center");
                return (int)ExitCode.ConnectionFailed;
            }
        }
        else
        {
            Logger.LogInformation("Environment already exists: {TargetUrl}", targetEnv.EnvironmentUrl);
            Console.Skip($"Environment already exists — [link]{targetEnv.EnvironmentUrl}[/]");
        }

        if (targetEnv.Type == "Production")
        {
            Console.Error("Can't overwrite a Production environment");
            return (int)ExitCode.ValidationFailed;
        }

        if (!settings.AllowOverwrite)
        {
            Console.Warning($"[bold]{targetEnv.DisplayName}[/] already exists — use --allow-overwrite to overwrite");
            return 0;
        }
        // reset: empty env with factory settings (https://learn.microsoft.com/en-us/power-platform/admin/reset-environment)?
        // after rest: deploy the solution from prod?

        // Block if target environment has unmanaged solutions
        var (prodSolutions, targetSolutions) = await Console.Status().FlowlineSpinner().StartAsync(
            "Checking solutions...",
            async _ =>
            {
                var prodTask   = PacUtils.GetSolutionsAsync(prodEnv.EnvironmentUrl!,   _capture, cancellationToken);
                var targetTask = PacUtils.GetSolutionsAsync(targetEnv.EnvironmentUrl!, _capture, cancellationToken);
                await Task.WhenAll(prodTask, targetTask);
                return (prodTask.Result, targetTask.Result);
            });

        var problematic = FindProblematicSolutions(targetSolutions, prodSolutions);
        if (problematic.Count > 0)
        {
            Console.Error("Target environment has unmanaged solutions that would be permanently lost:");
            foreach (var (solution, reason) in problematic)
                Console.Info($"- {solution.SolutionUniqueName} ({reason})");
            return (int)ExitCode.ValidationFailed;
        }

        // Test and UAT are always a FullCopy
        string copyType = (settings.Role is Role.Test or Role.Uat || settings.CopyType == CopyType.Full) ? "FullCopy" : "MinimalCopy";
        Logger.LogInformation("Copying {CopyType} from {Source} to {Target}", copyType, prodEnv.EnvironmentUrl, targetEnv.EnvironmentUrl);

        var (cmdNameCopy, prefixArgsCopy, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);

        // Run synchronously (no --async): pac blocks and polls the copy to completion, so IsSuccess
        // means the copy actually finished, not just that it was triggered. Typical copy is ~30 min but
        // can run longer, so raise --max-async-wait-time well above pac's 60 min default. Ctrl-C only
        // stops pac's poll — the copy is a server-side operation and keeps running regardless.
        var copyResult = await Console.Status().FlowlineSpinner().StartAsync(
            $"Copying prod into [bold]{targetDisplayName}[/] (takes a while)...",
            _ => Cli.Wrap(cmdNameCopy)
                    .WithArguments(args => args
                                           .AddIfNotNull(prefixArgsCopy)
                                           .Add("admin")
                                           .Add("copy")
                                           .Add("--name").Add(targetDisplayName)
                                           .Add("--source-env").Add(prodEnv.EnvironmentUrl!)
                                           .Add("--target-env").Add(targetEnv.EnvironmentUrl!)
                                           .Add("--type").Add(copyType)
                                           .Add("--max-async-wait-time").Add("480"))
                    .WithValidation(CommandResultValidation.None)
                    .WithCapture(_capture)
                    .ExecuteAsync(cancellationToken)
                    .Task);

        // Non-success covers both a real copy failure and pac giving up after the wait cap while the
        // copy is still running server-side — hence "didn't finish", not "failed", and point at status.
        if (!copyResult.IsSuccess)
            throw new FlowlineException(ExitCode.GeneralError, "Copy from prod didn't finish — check 'pac admin status' and the Power Platform admin center. Use --verbose for more details.");

        Config!.Save();
        Console.Done($"Provisioned! Prod copied into [bold]{targetDisplayName}[/]. Run 'clone' or 'sync' to get going. ٩(◕‿◕｡)۶");

        return 0;

        // TODO: add a different strategy where we import solution(s) from prod, instead of copying the whole environment.
        // should be much faster. also for reset the environment. => use this path also for Development environments.
    }

    internal static IReadOnlyList<(SolutionInfo Target, string Reason)> FindProblematicSolutions(
        IEnumerable<SolutionInfo> targetSolutions,
        IEnumerable<SolutionInfo> prodSolutions)
    {
        var prod = prodSolutions.ToList();

        var prodByName = prod
            .Where(s => s.SolutionUniqueName != null)
            .ToDictionary(s => s.SolutionUniqueName!, StringComparer.OrdinalIgnoreCase);

        // Match by Id as well as unique name. The default solutions (Default Solution, Common Data
        // Services Default Solution) are unmanaged and present in every environment, but carry an
        // environment-specific unique name — only their solution Id is stable across environments.
        // Without the Id match they never match prod by name and get flagged "absent from prod" on
        // every provision. A shared non-empty Id only ever links the same solution (Dataverse preserves
        // solutionid on import; user solutions get a fresh Id per env, so they still match by name).
        var prodById = prod
            .Where(s => s.Id != Guid.Empty)
            .ToDictionary(s => s.Id);

        return targetSolutions
            .Where(s => !s.IsManaged && s.SolutionUniqueName != null)
            .Select(s =>
            {
                var candidates = new List<SolutionInfo>();
                if (prodByName.TryGetValue(s.SolutionUniqueName!, out var byName))
                    candidates.Add(byName);
                if (s.Id != Guid.Empty && prodById.TryGetValue(s.Id, out var byId))
                    candidates.Add(byId);

                if (candidates.Count == 0)
                    return (Target: s, Reason: "absent from prod");
                if (candidates.Any(c => !c.IsManaged))
                    return (Target: s, Reason: ""); // same solution exists unmanaged in prod → safe
                return (Target: s, Reason: "managed in prod");
            })
            .Where(x => x.Reason != "")
            .ToList();
    }
}
