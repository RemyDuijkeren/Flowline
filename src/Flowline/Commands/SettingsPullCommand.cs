using System.ComponentModel;
using Flowline.Core;
using Flowline.Core.Configure;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Infrastructure;
using Flowline.Services;
using Flowline.Utils;
using Microsoft.Extensions.Logging;
using Microsoft.PowerPlatform.Dataverse.Client;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

/// <summary>Captures an environment's configuration into a settings file, or every configured one.</summary>
public class SettingsPullCommand(
    IAnsiConsole console,
    DataverseConnector dataverseConnector,
    FlowlineRuntimeOptions runtimeOptions,
    ProfileResolutionService profileResolutionService,
    ILoggerFactory loggerFactory,
    SubprocessCapture capture,
    NuGetVersionClient nuGetVersionClient)
    : SettingsCommandBase<SettingsPullCommand.Settings>(console, dataverseConnector, runtimeOptions, profileResolutionService, loggerFactory, capture, nuGetVersionClient)
{
    public sealed class Settings : SettingsSettings
    {
        // Optional, unlike every other operation's target (R16). With none, the run captures every role the
        // project configures.
        [CommandArgument(0, "[target]")]
        [Description("Environment to capture: prod, uat, test, dev, or a URL. Omit to pick from the configured ones")]
        public string? Target { get; set; }

        [CommandOption("--from <zip-or-folder>")]
        [Description("Read the solution's component list from this zip or unpacked folder instead of the project")]
        public string? From { get; set; }

        [CommandOption("--settings-file <path>")]
        [Description("Write this file instead of the role-named one beside the .cdsproj")]
        public string? SettingsFile { get; set; }
    }

    protected override bool IsStandalone(Settings settings) =>
        SettingsSupport.ResolveStandalone(settings.SolutionName, settings.From, Directory.GetCurrentDirectory());

    protected override async Task<int> ExecuteFlowlineAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var (standalone, projectFound) = ResolveProjectMode(settings);

        var mode = settings.DryRun ? RunMode.DryRun : RunMode.Normal;

        return settings.Target is null
            ? await SweepAsync(settings, mode, projectFound, cancellationToken)
            : await CaptureOneAsync(settings, settings.Target, standalone, mode, sweeping: false, cancellationToken);
    }

    /// <summary>
    /// Captures the roles chosen from the ones the project configures, reporting each on its own line
    /// (R16, KTD13, KTD21).
    /// </summary>
    /// <remarks>
    /// No role is exempt from being offered. The apply side already accepts DEV as a target, and a DEV
    /// branched from production carries connection references bound to connections that do not exist
    /// there, so a DEV file has a real consumer.
    ///
    /// Each environment is captured on its own so one bad environment does not lose the captures that
    /// succeeded, and an environment that cannot be reached is a failure rather than a skip: a configured
    /// role that no longer answers is something the operator has to fix, not something to pass over quietly.
    /// </remarks>
    async Task<int> SweepAsync(Settings settings, RunMode mode, bool projectFound, CancellationToken ct)
    {
        // The roles come from the project config, so outside a project there is nothing to enumerate.
        if (!projectFound)
            throw new FlowlineException(ExitCode.ValidationFailed,
                "No environment named, and no Flowline project to read the configured ones from. " +
                "Pass an environment: 'flowline settings pull <target>'.");

        if (!string.IsNullOrWhiteSpace(settings.SettingsFile))
            throw new FlowlineException(ExitCode.ValidationFailed, SettingsSupport.BuildSweepDestinationError());

        var configured = EnvironmentRoles.All
            .Where(role => !string.IsNullOrWhiteSpace(Config?.GetUrl(role)))
            .ToArray();

        if (configured.Length == 0)
            throw new FlowlineException(ExitCode.ConfigInvalid,
                "No environments are configured in .flowline, so there's nothing to capture. " +
                "Run 'flowline provision' or name an environment.");

        var roles = await ChooseRolesAsync(configured, ct);

        // Choosing none is an answer, not a failure. Someone who opened the list and changed their mind
        // has a way out that is not Ctrl+C.
        if (roles.Count == 0)
        {
            Console.Info("Nothing picked, so nothing was captured.");
            return (int)ExitCode.Success;
        }

        var failed = await CaptureEachAsync(
            roles,
            // A keyword, not the config key: the config key is the .flowline JSON property name, and the
            // role parser does not accept it. Passing it resolved every role to nothing and sent each one
            // down the URL path instead.
            role => CaptureOneAsync(settings, role.Keyword(), standalone: false, mode, sweeping: true, ct),
            (role, ex) =>
            {
                Console.Error($"{role.UpperLabel()} failed — {Markup.Escape(ex.Message)}");
                Logger.LogWarning(ex, "Sweep capture failed for {Role}", role.UpperLabel());
            },
            ct);

        if (failed.Count == 0)
        {
            Console.Done($"Captured {roles.Count} environment{(roles.Count == 1 ? "" : "s")}. {SettingsSupport.Kaomoji}");
            return (int)ExitCode.Success;
        }

        Console.Warning($"Couldn't reach {string.Join(", ", failed.Select(r => r.UpperLabel()))} — " +
                        "the rest were captured. Fix those and re-run.");
        return (int)ExitCode.PartialSuccess;
    }

    /// <summary>
    /// Decides which of the configured roles to capture when the invocation named no environment
    /// (R16, KTD21).
    /// </summary>
    /// <remarks>
    /// An unattended run fails naming the argument, the same as <c>settings push</c>, because the bare
    /// word is the widest and slowest thing this command does: capturing every configured environment
    /// means an auth flow and a Dataverse connection per role. A CLI's no-argument form should not be its
    /// broadest, and a script that meant one environment should hear about it before waiting for four.
    ///
    /// At a terminal it is a multi-select with everything pre-selected, so one Enter still sweeps the lot.
    /// The point is not to make the sweep harder, only to make it visible and narrowable.
    /// </remarks>
    protected virtual async Task<IReadOnlyList<EnvironmentRole>> ChooseRolesAsync(
        IReadOnlyList<EnvironmentRole> configured, CancellationToken ct)
    {
        // The capability check comes before the prompt is built, not around showing it: an unattended
        // caller must never reach a prompt it cannot answer, which hangs the run rather than failing it.
        if (!IsInteractive())
            throw new FlowlineException(ExitCode.ValidationFailed,
                "No environment named. Pass one: 'flowline settings pull <target>'. " +
                $"This project configures {string.Join(", ", configured.Select(r => r.Keyword()))}.");

        var prompt = new MultiSelectionPrompt<EnvironmentRole>()
            .Title(FlowlineConsoleExtensions.Question("Capture which environments?"))
            .InstructionsText("[grey](space toggles, enter confirms)[/]")
            // Picking nothing has to be possible, or the only way out of the list is Ctrl+C.
            .NotRequired()
            .UseConverter(DescribeRole);

        foreach (var role in configured)
            prompt.AddChoice(role).Select();

        return await CancellablePrompt.AskAsync(Console, prompt, ct);
    }

    /// <summary>How a role reads in the list: its keyword, then the environment it resolves to.</summary>
    /// <remarks>
    /// The URL is what tells two similarly named environments apart, and it is the thing worth checking
    /// before writing four files. Escaped because a selection prompt parses its converter output as markup.
    /// </remarks>
    string DescribeRole(EnvironmentRole role)
    {
        var url = Config?.GetUrl(role);

        return Markup.Escape(
            string.IsNullOrWhiteSpace(url) ? role.UpperLabel() : $"{role.UpperLabel()} — {url}");
    }

    /// <summary>
    /// Runs one capture per role, letting a failure stop that role and nothing else.
    /// </summary>
    /// <remarks>
    /// Separated from the capture itself so the isolation can be tested without an environment: hand it a
    /// delegate that throws for one role and the rest must still run. That property is the whole point of
    /// the sweep, and it is not observable from any of the pure helpers around it.
    ///
    /// Cancellation is not a per-role failure. A cancelled run has stopped, and reporting the remaining
    /// roles as unreachable would describe something that was never attempted.
    /// </remarks>
    internal static async Task<IReadOnlyList<EnvironmentRole>> CaptureEachAsync(
        IReadOnlyList<EnvironmentRole> roles,
        Func<EnvironmentRole, Task> capture,
        Action<EnvironmentRole, Exception> onFailure,
        CancellationToken ct)
    {
        var failed = new List<EnvironmentRole>();

        foreach (var role in roles)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await capture(role);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Named, not swallowed. The next role still gets its capture.
                failed.Add(role);
                onFailure(role, ex);
            }
        }

        return failed;
    }

    /// <summary>Captures one environment into one settings file.</summary>
    async Task<int> CaptureOneAsync(
        Settings settings, string target, bool standalone, RunMode mode, bool sweeping, CancellationToken ct)
    {
        // Per target, not per run: a sweep resolves a role for each one it captures.
        var role = ResolveRoleOrThrow(target, standalone);

        var (env, profile) = await ResolveEnvironmentAsync(target, role, settings, ct);
        var solutionName = await ResolveSolutionNameAsync(settings, standalone, settings.From, env, ct);

        // A URL target carries no role, so the file it would write has no name (KTD17). Inference reads the
        // host label and the display name and returns nothing rather than guessing; it never infers
        // production from a name, because production comes only from the environment type Dataverse reports.
        var fileRole = role?.ToString();
        if (role is null && string.IsNullOrWhiteSpace(settings.SettingsFile))
        {
            var inferred = EnvironmentRoleInference.Infer(env.EnvironmentUrl, env.Type, env.DisplayName);
            if (inferred.Role is null)
                throw new FlowlineException(ExitCode.ValidationFailed, SettingsSupport.BuildUninferrableRoleError(target));

            fileRole = inferred.Role.Value.ToString();
            Console.Info($"Role [bold]{fileRole.ToUpperInvariant()}[/] (inferred from {inferred.Source})");
        }

        var location = await ResolveSettingsFileAsync(
            settings.SettingsFile, ParseFileRole(fileRole), standalone, settings.From, forWriting: true, ct);

        var (service, _) = await ConnectToDataverseAsync(DataverseConnector, env.EnvironmentUrl!, ct, profile);
        var inventory = await ReadInventoryAsync(service, solutionName, ct);

        // Resolved here, not up front: only a capture needs a solution on disk to generate the skeleton
        // from. Reading the project layout unconditionally made a stand-alone apply — which needs no
        // checkout at all — fail with "No solution file in ./".
        var (solutionPath, solutionIsZip) = settings.From is not null
            ? SettingsSupport.ResolveSolutionInput(settings.From)
            : (Path.Combine((await SolutionFileLayout.LoadAsync(RootFolder, ct)).DataverseSolutionFolder, "src"), false);

        return await WriteAsync(service, env, solutionPath, solutionIsZip, location, inventory, mode, sweeping, ct);
    }

    /// <summary>
    /// Walks the entries the capture left blank and offers to fill them in (R19, KTD24).
    /// </summary>
    /// <remarks>
    /// `pac solution create-settings` emits a skeleton with every value blank, and a capture leaves one
    /// blank when the environment has nothing to read. Those blanks are the whole reason a settings file
    /// gets hand-edited, and the two things needed to fill them are already here: the environment is
    /// connected, and its connections can be listed and picked.
    ///
    /// Nothing is asked unless something is missing, so an ordinary refresh of an already-filled file is
    /// silent, and a sweep over three filled files stays silent three times.
    ///
    /// One confirmation before the questions start, per environment. Someone who ran a capture did not
    /// necessarily sign up for an interview, and a sweep has to be declinable per file.
    /// </remarks>
    async Task FillTheBlanksAsync(
        SettingsDocument document, EnvironmentInfo environment, string path, string display, CancellationToken ct)
    {
        var gaps = SettingsFileGaps.Find(document);
        if (gaps.Count == 0) return;

        var summary = $"{gaps.Count} entr{(gaps.Count == 1 ? "y" : "ies")} in {Markup.Escape(display)} " +
                      "still need a value";

        // An unattended caller cannot answer, so it gets the count and nothing else. Not a failure: a file
        // with blanks is inert rather than wrong, because the apply path skips an empty declared value.
        if (!IsInteractive())
        {
            Console.Info($"{summary}. Run this interactively to fill them in.");
            return;
        }

        if (!await Console.PromptAsync(new ConfirmationPrompt($"{summary}. Fill them in now?"), ct))
            return;

        var filled = 0;

        foreach (var gap in gaps)
        {
            var answer = gap.Kind == SettingsGapKind.ConnectionReference
                ? await ConnectionPicker.PickAsync(
                    Console, environment, gap.ConnectorId, $"Bind {Markup.Escape(gap.Name)} to:", ct)
                : await Console.PromptAsync(
                    new TextPrompt<string>(FlowlineConsoleExtensions.Question(
                        $"Value for {Markup.Escape(gap.Name)} (blank to skip):")).AllowEmpty(), ct);

            // Blank means skip, not clear. An empty declared value is what the file already holds, so
            // writing one back would be a no-op with the appearance of an answer.
            if (string.IsNullOrEmpty(answer)) continue;

            if (SettingsFileGaps.Fill(document, gap, answer)) filled++;
        }

        if (filled == 0)
        {
            Console.Info("Nothing filled in.");
            return;
        }

        SettingsFileReader.Save(document, path);
        Console.Ok($"Filled in {filled} of {gaps.Count} in {Markup.Escape(display)}");
    }

    static EnvironmentRole? ParseFileRole(string? role) =>
        role is null ? null : EnvironmentRoles.TryParse(role);

    /// <summary>
    /// Builds the merged document and writes it.
    /// </summary>
    /// <remarks>
    /// PAC writes the skeleton to a temp file, never the real one: verified against pac 2.11.2, a
    /// <c>create-settings</c> run overwrites its target wholesale, destroying both filled-in values and
    /// every Flowline-owned section. The merge happens here and only the merged document is saved.
    /// </remarks>
    async Task<int> WriteAsync(
        IOrganizationServiceAsync2 service,
        EnvironmentInfo environment,
        string solutionPath,
        bool solutionIsZip,
        SettingsFileLocation location,
        SolutionInventory inventory,
        RunMode mode,
        bool sweeping,
        CancellationToken ct)
    {
        var skeletonPath = Path.Combine(Directory.CreateTempSubdirectory("flowline-pull-").FullName, "settings.json");

        return await DriftCommand.RunInTempDirAsync(Path.GetDirectoryName(skeletonPath)!, async () =>
        {
            await PacUtils.CreateSettingsAsync(solutionPath, solutionIsZip, skeletonPath, _capture, ct);

            var skeleton = SettingsFileReader.Read(skeletonPath);
            var existing = location.Exists ? SettingsFileReader.Read(location.Path) : null;

            var result = await new ConfigurePullService().BuildAsync(service, skeleton, existing, inventory, ct);

            var display = ConsolePath.FormatRelativePath(location.Path, RootFolder);

            // The same path without markup, for the sinks that escape what they are given. The formatter
            // emits [bold] tags of its own, and escaping those printed them as literal text — which its
            // own documentation warns about and which two messages here were doing.
            var plainDisplay = ConsolePath.FormatRelativePath(location.Path, RootFolder, markup: false);

            foreach (var added in result.Added)
                Console.Info($"New: {Markup.Escape(added)}");

            foreach (var vanished in result.Vanished)
                Console.Warning($"{Markup.Escape(vanished)} is no longer in the solution — kept in the file, not applied.");

            foreach (var placeholder in result.Placeholders)
                Console.Warning($"{Markup.Escape(placeholder)} is a secret Flowline won't read — " +
                                $"'{ConfigurePullService.SecretPlaceholder}' was written, fill it in by hand.");

            if (mode.IsReportOnly())
            {
                Console.Done(SettingsSupport.BuildPullDryRunMessage(plainDisplay));
                return (int)ExitCode.Success;
            }

            SettingsFileReader.Save(result.Document, location.Path);

            // Saved before the fill, not after: the capture is the part that cannot be retyped, and a
            // session abandoned halfway through the questions must not cost it. The fill saves again.
            await FillTheBlanksAsync(result.Document, environment, location.Path, plainDisplay, ct);

            // A sweep prints its own finish line over the whole run, so each environment reports as a step
            // rather than signing off as if the run were done.
            if (sweeping)
                Console.Ok($"Wrote {display}");
            else
                Console.Done($"Wrote {display} {SettingsSupport.Kaomoji}");

            return (int)ExitCode.Success;
        }, Logger);
    }
}
