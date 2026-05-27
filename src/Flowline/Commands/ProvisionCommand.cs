using System.ComponentModel;
using CliWrap;
using Flowline.Core;
using Flowline.Utils;
using Flowline.Validation;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public enum Role { Dev, Test }

public enum CopyType { Minimal, Full }

public class ProvisionCommand(IAnsiConsole console, FlowlineRuntimeOptions runtimeOptions) : FlowlineCommand<ProvisionCommand.Settings>(console, runtimeOptions)
{
    public sealed class Settings : FlowlineSettings
    {
        [CommandArgument(0, "[role]")]
        [Description("Target role: dev or test")]
        [DefaultValue(Role.Dev)]
        public Role Role { get; set; } = Role.Dev; // dev|test

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

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        // Production URL is required
        var prodEnv = await GetAndCheckEnvironmentInfoAsync(EnvironmentRole.Prod, settings.ProdUrl, settings, cancellationToken);

        // Prepare the target environment name and url
        var suffix = string.IsNullOrWhiteSpace(settings.Suffix)
            ? (settings.Role == Role.Dev ? "Dev" : "Test")
            : settings.Suffix;
        var targetDisplayName = $"{prodEnv.DisplayName} {suffix}";
        EnvironmentUrlParts urlParts = PacUtils.GetPartsFromEnvUrl(prodEnv.EnvironmentUrl!);
        var targetUrl = $"https://{urlParts.Organization}-{suffix.ToLower()}.{urlParts.Host}/";

        // TODO: verify if the target environment url is given, is in the same region. Is this needed?
        // if <org> already ends with your suffix, don't duplicate.
        // If your prod org is named contoso-prod, add a config "swap map" so -prod → -dev/-stg instead of appending.

        string? url = settings.Role switch
        {
            Role.Dev  => Config!.GetOrUpdateDevUrl(targetUrl, settings),
            Role.Test => Config!.GetOrUpdateTestUrl(targetUrl, settings),
            _ => null
        };

        if (url == null)
        {
            Console.Error("Couldn't build a valid target URL — check your .flowline config");
            return 1;
        }

        // Validate target environment
        var targetEnv = await FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(targetUrl, settings, cancellationToken);
        if (targetEnv == null)
        {
            var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);
            await Console.Status().FlowlineSpinner().StartAsync(
                $"Creating [bold]{targetDisplayName}[/]...",
                _ => Cli.Wrap(cmdName)
                     .WithArguments(args => args
                                            .AddIfNotNull(prefixArgs)
                                            .Add("admin")
                                            .Add("create")
                                            .Add("--name").Add($"{targetDisplayName} (cloning)")
                                            .Add("--domain").Add($"{urlParts.Organization}-{suffix.ToLower()}")
                                            .Add("--region").Add(urlParts.Region)
                                            .Add("--async"))
                     .WithToolExecutionLog(settings.Verbose)
                     .ExecuteAsync(cancellationToken)
                     .Task);

            targetEnv = await FlowlineValidator.Default.GetEnvironmentInfoByUrlAsync(targetUrl, settings, cancellationToken);
            if (targetEnv == null)
            {
                Console.Error("Environment created but not found — check the Power Platform admin center");
                return 1;
            }
        }
        else
        {
            Console.Skip($"Environment already exists — [link]{targetEnv.EnvironmentUrl}[/]");
        }

        if (targetEnv.Type == "Production")
        {
            Console.Error("Can't overwrite a Production environment");
            return 1;
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
                var prodTask   = PacUtils.GetSolutionsAsync(prodEnv.EnvironmentUrl!,   settings.Verbose, cancellationToken);
                var targetTask = PacUtils.GetSolutionsAsync(targetEnv.EnvironmentUrl!, settings.Verbose, cancellationToken);
                await Task.WhenAll(prodTask, targetTask);
                return (prodTask.Result, targetTask.Result);
            });

        var problematic = FindProblematicSolutions(targetSolutions, prodSolutions);
        if (problematic.Count > 0)
        {
            Console.Error("Target environment has unmanaged solutions that would be permanently lost:");
            foreach (var (solution, reason) in problematic)
                Console.Info($"- {solution.SolutionUniqueName} ({reason})");
            return 1;
        }

        // Test is always a FullCopy
        string copyType = (settings.Role == Role.Test || settings.CopyType == CopyType.Full) ? "FullCopy" : "MinimalCopy";

        var (cmdNameCopy, prefixArgsCopy, _) = await PacUtils.GetBestPacCommandAsync(cancellationToken);

        await Console.Status().FlowlineSpinner().StartAsync(
            $"Copying prod to [bold]{targetDisplayName}[/]...",
            _ => Cli.Wrap(cmdNameCopy)
                 .WithArguments(args => args
                                        .AddIfNotNull(prefixArgsCopy)
                                        .Add("admin")
                                        .Add("copy")
                                        .Add("--name").Add(targetDisplayName)
                                        .Add("--source-env").Add(prodEnv.EnvironmentUrl!)
                                        .Add("--target-env").Add(targetEnv.EnvironmentUrl!)
                                        .Add("--type").Add(copyType)
                                        .Add("--async"))
                 .WithToolExecutionLog(settings.Verbose)
                 .ExecuteAsync(cancellationToken)
                 .Task);

        Config!.Save();
        Console.Done($"Provisioned! See [link]{targetEnv.EnvironmentUrl}[/]. You can now run 'clone' or 'sync'");

        return 0;

        // TODO: add a different strategy where we import solution(s) from prod, instead of copying the whole environment.
        // should be much faster. also for reset the environment. => use this path also for Development environments.
    }

    internal static IReadOnlyList<(SolutionInfo Target, string Reason)> FindProblematicSolutions(
        IEnumerable<SolutionInfo> targetSolutions,
        IEnumerable<SolutionInfo> prodSolutions)
    {
        var prodByName = prodSolutions
            .Where(s => s.SolutionUniqueName != null)
            .ToDictionary(s => s.SolutionUniqueName!, StringComparer.OrdinalIgnoreCase);

        return targetSolutions
            .Where(s => !s.IsManaged && s.SolutionUniqueName != null)
            .Select(s =>
            {
                if (!prodByName.TryGetValue(s.SolutionUniqueName!, out var prodMatch))
                    return (Target: s, Reason: "absent from prod");
                if (prodMatch.IsManaged)
                    return (Target: s, Reason: "managed in prod");
                return (Target: s, Reason: "");
            })
            .Where(x => x.Reason != "")
            .ToList();
    }
}
