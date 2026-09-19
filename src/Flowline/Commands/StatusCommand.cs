using System.ComponentModel;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Infrastructure;
using Flowline.Services;
using Flowline.Utils;
using Flowline.Validation;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class StatusCommand(IAnsiConsole console, SubprocessCapture capture, DataverseConnector dataverseConnector, ILoggerFactory loggerFactory, NuGetVersionClient nuGetVersionClient) : AsyncCommand<StatusCommand.Settings>
{
    private readonly IAnsiConsole Console = console;
    private readonly SubprocessCapture _capture = capture;
    private ILogger? _logger;
    protected ILogger Logger => _logger ??= loggerFactory.CreateLogger(GetType().Name);

    public sealed class Settings : FlowlineSettings
    {
    }

    internal static void ValidateForce(Settings settings)
    {
        if (settings.Force.Length > 0)
            throw new FlowlineException(ExitCode.ValidationFailed, "'status' has no force-gated behavior — remove --force.");
    }

    // status is a multi-environment overview, so it reports on profile resolution rather than going
    // through ProfileResolutionService.ResolveAsync (which can block/throw/prompt) — up to 4 confirm
    // prompts in one glance command would defeat the point. Pure so it's testable without a live
    // DataverseConnector/pac.exe. Returns null when there's nothing worth surfacing (matched profile).
    internal static string? FormatProfileNote(ProfileResolutionResult resolution, bool isActive) =>
        resolution switch
        {
            // PAC's authprofiles_v2.json gives an unnamed profile an empty-string Name, not a missing/null
            // one — a bare ?? chain never falls through to User for that shape, so this checks for
            // whitespace explicitly instead of relying on null-coalescing alone.
            ProfileFound found when !isActive =>
                $"PAC auth profile mismatch — active identity may not be '{FirstNonBlank(found.Profile.Name, found.Profile.User) ?? "(unnamed)"}'",
            ProfileFound => null,
            ProfileAmbiguous ambiguous =>
                $"{ambiguous.Candidates.Count} local PAC profiles match this environment — run 'pac auth list' to check",
            ProfileNotFound => "No local PAC auth profile matches this environment yet",
            _ => null
        };

    static string? FirstNonBlank(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) ? first
        : !string.IsNullOrWhiteSpace(second) ? second
        : null;

    // DataverseConnector isn't mockable (no interface, its instance overloads read the real
    // authprofiles_v2.json with no override seam) -- taking the two operations as funcs lets tests
    // exercise the loop, including the "never throw" guarantee, without a live PAC auth file.
    internal static Dictionary<string, string?> BuildProfileNotes(
        IEnumerable<(string Label, string? Url)> envs,
        Func<string, ProfileResolutionResult> findBestProfile,
        Func<PacProfile, bool> isProfileActive)
    {
        var notes = new Dictionary<string, string?>();
        foreach (var e in envs)
        {
            if (string.IsNullOrEmpty(e.Url)) continue;
            try
            {
                var resolution = findBestProfile(e.Url);
                var isActive = resolution is ProfileFound found && isProfileActive(found.Profile);
                notes[e.Label] = FormatProfileNote(resolution, isActive);
            }
            catch (Exception)
            {
                // No PAC auth profile file on this machine at all (e.g. 'pac auth create' never run), or
                // any other read failure (permissions, malformed file) -- advisory only, and this check
                // must never abort 'status'; the "Not authenticated" who-check below already surfaces the
                // no-profile case.
            }
        }
        return notes;
    }

    // Never throws. status reports on every environment, so one unreachable environment has to become a
    // row saying so rather than ending the run -- the same reason BuildProfileNotes swallows its errors.
    async Task<StatusGrid.EnvStatus> CheckEnvironmentAsync(
        string label, string? url, ProjectSolution? solution, CancellationToken cancellationToken)
    {
        var versions = new Dictionary<string, string?>();

        if (string.IsNullOrEmpty(url))
            return new StatusGrid.EnvStatus(label, url, null, versions);

        try
        {
            if (dataverseConnector.FindBestProfile(url) is not ProfileFound found)
                return new StatusGrid.EnvStatus(label, url, null, versions,
                    "no PAC auth profile matches this environment");

            var service = await dataverseConnector.ConnectViaPacAsync(found.Profile, url, cancellationToken);

            var connectedAs = await DataverseConnector.GetConnectedUserAsync(service, cancellationToken);

            if (solution is not null)
                versions[solution.UniqueName] =
                    await new SolutionReader().GetInstalledVersionAsync(service, solution.UniqueName, cancellationToken);

            return new StatusGrid.EnvStatus(label, url, new WhoAmIInfo(connectedAs), versions);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Reports what went wrong rather than asserting a cause. A failure here used to be rendered
            // as "Not authenticated", which was a guess and often the wrong one.
            return new StatusGrid.EnvStatus(label, url, null, versions, FirstMeaningfulLine(ex.Message));
        }
    }

    // An exception message can run to several lines of detail; a status row has space for the first.
    internal static string FirstMeaningfulLine(string? text)
    {
        var line = (text ?? string.Empty)
            .Split('\n')
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.Length > 0);

        if (string.IsNullOrEmpty(line)) return "no reason given";

        return line.Length > 120 ? line[..119].TrimEnd() + "\u2026" : line;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ValidateForce(settings);

        if (Console.Profile.Capabilities.Interactive)
            Console.WriteWelcomeScreen();

        // Before the try and the early returns below: "am I on the latest?" is the question status exists
        // to answer, so the notice must not depend on a config being found or the tool probes succeeding.
        // status stays on FlowlineSettings — no --no-cache, so this is always false.
        UpdateNoticeChecker.PrintNotice(Console, await UpdateNoticeChecker.CheckAsync(
            Console, FlowlineValidator.Default, nuGetVersionClient, false, cancellationToken));

        try
        {
            // Probes run fresh (noCache: true): status reports what's installed now, not what a
            // 7-day TTL remembers. The re-probe also rewrites the cache other commands read.
            var dotNet = await FlowlineValidator.Default.EnsureDotNetAsync(settings, true, cancellationToken);
            Console.MarkupLine($"[bold].NET SDK[/] version: [green]{dotNet.Version}[/]");

            var pac = await FlowlineValidator.Default.EnsurePacCliAsync(settings, true, cancellationToken);
            Console.MarkupLine($"[bold]Power Platform CLI[/] version: [green]{pac.Version}[/] ({pac.InstallType})");

            var git = await FlowlineValidator.Default.EnsureGitAsync(settings, true, cancellationToken);
            Console.MarkupLine($"[bold]Git[/] version: [green]{git.Version}[/]");
        }
        catch
        {
            // PAC CLI and Git checks will exit the application if not found
        }

        // Show the current configuration
        var rootFolder = FlowlineCommand<Settings>.FindFlowlineProjectRoot(Directory.GetCurrentDirectory()) ?? Directory.GetCurrentDirectory();
        var config = ProjectConfig.Load(rootFolder);
        Console.MarkupLine("\n[bold]Configuration[/]");

        if (config is null)
        {
            Console.MarkupLine("  [yellow]No .flowline config found[/]");
            return 0;
        }

        var envs = new (string Label, string? Url)[]
        {
            ("Dev",  config.DevUrl),
            ("Test", config.TestUrl),
            ("UAT",  config.UatUrl),
            ("Prod", config.ProdUrl),
        };

        var hasUrls = envs.Any(e => !string.IsNullOrEmpty(e.Url));
        var solution = config.Solution;

        var profileNotes = BuildProfileNotes(envs, dataverseConnector.FindBestProfile, dataverseConnector.IsProfileActive);

        StatusGrid.EnvStatus[] results;

        if (hasUrls)
        {
            // One environment at a time, on Flowline's own connection rather than a pac subprocess per
            // question. Measured against three environments: the old parallel pac fan-out took ~9.8s and
            // reported a random environment as unreachable in roughly half of runs, because concurrent
            // pac processes contend for the one token store they share. Going through this process's own
            // connection removes that contention at the source, and doing it in order removes it for the
            // token-minting case too, which is the one that actually bit. It is still ~3x faster than the
            // parallel version it replaces, so there is no speed argument for going back to concurrency.
            results = await Console.Status().FlowlineSpinner().StartAsync(
                "Checking environments...",
                async ctx =>
                {
                    var collected = new List<StatusGrid.EnvStatus>(envs.Length);
                    foreach (var e in envs)
                    {
                        ctx.Status($"Checking {e.Label.ToLowerInvariant()}...");
                        collected.Add(await CheckEnvironmentAsync(e.Label, e.Url, solution, cancellationToken));
                    }
                    return collected.ToArray();
                });
        }
        else
        {
            results = envs.Select(e => new StatusGrid.EnvStatus(e.Label, e.Url, null, new Dictionary<string, string?>())).ToArray();
        }

        foreach (var (label, url, who, _, checkError) in results)
        {
            if (string.IsNullOrEmpty(url))
            {
                Console.MarkupLine($"  {label}: [gray]Not configured[/]");
                continue;
            }

            Console.MarkupLine($"  {label}: [green]{Markup.Escape(url)}[/]");

            if (who is not null)
                Console.MarkupLine($"    [green]✓[/] {Markup.Escape(who.ConnectedAs)}");
            else
                Console.MarkupLine($"    [red]✗[/] Couldn't check: {Markup.Escape(checkError ?? "pac gave no reason")}");

            if (profileNotes.TryGetValue(label, out var note) && note is not null)
                Console.MarkupLine($"    [yellow]⚠[/] {Markup.Escape(note)}");
        }

        Console.MarkupLine("");

        if (solution is null)
        {
            Console.MarkupLine("[dim]No solutions configured[/]");
            return 0;
        }

        async Task<bool> IsRepoDirtyAsync(string solutionName)
        {
            // Scoped to the whole project root, not just the package source -- status reports on
            // the project as a whole, so uncommitted changes anywhere (docs, tests, config)
            // are relevant here even though deploy's own dirty gate is narrower (the package
            // folder and the packaging project files only, per R15).
            var solutionPath = rootFolder;
            try
            {
                var changes = await GitUtils.GetUncommittedChangesInPathAsync(solutionPath, rootFolder, _capture, cancellationToken);
                return changes.Count > 0;
            }
            catch (Exception)
            {
                // Solution not yet cloned, or git status failed for this path -- the dirty
                // indicator is advisory, so fail closed to "not dirty" rather than aborting status.
                return false;
            }
        }

        var isDirty = await IsRepoDirtyAsync(solution.UniqueName);

        // status is read-only and advisory: a project with no solution file, or one listing two .cdsproj,
        // still has environment versions worth showing, so a broken/missing layout renders a dash below
        // rather than aborting the whole grid — the one sanctioned catch of a FlowlineException outside
        // BuildGridRows' own (R6's "error" is for action commands, not this read-only view).
        string? dataverseSolutionFolder = null;
        try
        {
            var layout = await SolutionFileLayout.LoadAsync(rootFolder, cancellationToken);
            dataverseSolutionFolder = layout.DataverseSolutionFolder;
        }
        catch (FlowlineException)
        {
            // No solution file, or an invalid one (e.g. two .cdsproj) — dataverseSolutionFolder stays null and
            // BuildGridRows renders a dash for the repo column.
        }

        var (headers, rows) = StatusGrid.BuildGridRows([solution], results,
            solutionName => dataverseSolutionFolder is null ? null : DeployCommand.ReadLocalSolutionVersion(dataverseSolutionFolder),
            solutionName => isDirty);

        rows = StatusGrid.DetectVersionDrift(StatusGrid.TrimUnusedRevisionSegment(rows));
        StatusGrid.RenderGrid(Console, headers, rows);
        Console.MarkupLine(StatusGrid.Legend);

        var driftKinds = rows.SelectMany(r => r.Cells).Select(c => c.Drift).ToHashSet();

        if (driftKinds.Contains(StatusGrid.DriftKind.Inverted))
        {
            Console.MarkupLine("");
            Console.Warning("an environment is ahead of an earlier stage — check the grid above.");
        }
        else if (driftKinds.Contains(StatusGrid.DriftKind.Pending))
        {
            Console.Done("Some environments are behind — promote when you're ready.");
        }
        else
        {
            Console.Done("All environments in sync.");
        }

        return 0;
    }
}
