using System.ComponentModel;
using Flowline.Core;
using Flowline.Core.Configure;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Infrastructure;
using Flowline.Services;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

/// <summary>Applies a per-environment settings file to a Dataverse environment.</summary>
public class SettingsPushCommand(
    IAnsiConsole console,
    DataverseConnector dataverseConnector,
    FlowlineRuntimeOptions runtimeOptions,
    ProfileResolutionService profileResolutionService,
    ILoggerFactory loggerFactory,
    SubprocessCapture capture,
    NuGetVersionClient nuGetVersionClient)
    : SettingsCommandBase<SettingsPushCommand.Settings>(console, dataverseConnector, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
{
    public sealed class Settings : SettingsSettings
    {
        [CommandArgument(0, "<target>")]
        [Description("Target environment: prod, uat, test, dev, or a URL")]
        public string Target { get; set; } = null!;

        [CommandOption("--settings-file <path>")]
        [Description("Apply this settings file instead of the one beside the .cdsproj")]
        public string? SettingsFile { get; set; }
    }

    protected override bool IsStandalone(Settings settings) =>
        SettingsSupport.ResolveStandalone(settings.SolutionName, null, Directory.GetCurrentDirectory());

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var (standalone, _) = ResolveProjectMode(settings);
        var role = ResolveRoleOrThrow(settings.Target, standalone);

        var (env, profile) = await ResolveEnvironmentAsync(settings.Target, role, settings, cancellationToken);
        var solutionName = await ResolveSolutionNameAsync(settings, standalone, null, env, cancellationToken);

        var location = await ResolveSettingsFileAsync(
            settings.SettingsFile, role, standalone, null, forWriting: false, cancellationToken);

        if (!location.Exists)
            throw new FlowlineException(ExitCode.NotFound,
                $"No settings file at '{ConsolePath.FormatRelativePath(location.Path, RootFolder)}'. " +
                $"Run 'flowline settings pull {settings.Target}' to create one.");

        var mode = settings.DryRun ? RunMode.DryRun : RunMode.Normal;
        var (service, _) = await ConnectToDataverseAsync(DataverseConnector, env.EnvironmentUrl!, cancellationToken, profile);

        var inventory = await ReadInventoryAsync(service, solutionName, cancellationToken);

        Console.Info(SettingsSupport.BuildResolutionNote(location));

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(service, SettingsFileReader.Read(location.Path), inventory, mode, cancellationToken);

        Report(outcome, mode, env.DisplayName ?? settings.Target);

        return (int)outcome.ExitCode;
    }

    /// <summary>
    /// Reports every component on its own line, then the one summary line.
    /// </summary>
    /// <remarks>
    /// Values never appear; names and states always do. A run whose only signal is an exit code leaves an
    /// operator, or an agent, nothing to act on.
    /// </remarks>
    void Report(ApplyOutcome outcome, RunMode mode, string environment)
    {
        foreach (var component in outcome.Components)
        {
            var name = Markup.Escape(component.Name);
            switch (component.Outcome)
            {
                case ComponentOutcomeKind.Applied when mode.IsReportOnly():
                    Console.Info(SettingsSupport.BuildWouldChangeLine(component.Name));
                    break;
                case ComponentOutcomeKind.Applied:
                    Console.Ok(SettingsSupport.BuildUpdatedLine(component.Name, component.WasSuspended));
                    break;
                case ComponentOutcomeKind.Unchanged:
                    Console.Verbose($"{name} already matches");
                    break;
                case ComponentOutcomeKind.Skipped:
                    Console.Skip(Markup.Escape(component.Detail ?? $"{component.Name} skipped"));
                    break;
                case ComponentOutcomeKind.Failed:
                    Console.Error(Markup.Escape(component.Detail ?? $"{component.Name} failed"));
                    break;
            }
        }

        foreach (var undeclared in outcome.Undeclared)
            Console.Verbose($"Not declared in the settings file: {Markup.Escape(undeclared)}");

        if (outcome.Undeclared.Count > 0)
            Console.Warning(SettingsSupport.BuildUndeclaredWarning(outcome.Undeclared.Count));

        Console.Info(outcome.SummaryLine());

        // An interrupted run must not sign off as if it finished the file. The components above are what it
        // reached, and re-running is safe, so that is the whole message.
        if (outcome.Cancelled)
        {
            Console.Warning(SettingsSupport.BuildCancelledMessage(environment));
            return;
        }

        // One finish line, always last. A real apply used to end on the summary and never reach one.
        Console.Done(mode.IsReportOnly()
            ? SettingsSupport.BuildDryRunCompleteMessage(environment)
            : SettingsSupport.BuildAppliedMessage(environment));
    }
}
