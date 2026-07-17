using System.ComponentModel;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Diagnostics;
using Flowline.Utils;
using Flowline.Validation;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class StatusCommand(IAnsiConsole console, SubprocessCapture capture, DataverseConnector dataverseConnector, ILoggerFactory loggerFactory) : AsyncCommand<StatusCommand.Settings>
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
            ProfileFound found when !isActive =>
                $"PAC auth profile mismatch — active identity may not be '{found.Profile.Name ?? found.Profile.User ?? "(unnamed)"}'",
            ProfileFound => null,
            ProfileAmbiguous ambiguous =>
                $"{ambiguous.Candidates.Count} local PAC profiles match this environment — run 'pac auth list' to check",
            ProfileNotFound => "No local PAC auth profile matches this environment yet",
            _ => null
        };

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

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        ValidateForce(settings);

        if (ConsoleHelper.IsInteractive(settings))
            ConsoleHelper.WelcomeScreen(Console);

        try
        {
            var dotNet = await FlowlineValidator.Default.EnsureDotNetAsync(settings, cancellationToken);
            Console.MarkupLine($"[bold].NET SDK[/] version: [green]{dotNet.Version}[/]");

            var pac = await FlowlineValidator.Default.EnsurePacCliAsync(settings, cancellationToken);
            Console.MarkupLine($"[bold]Power Platform CLI[/] version: [green]{pac.Version}[/] ({pac.InstallType})");

            var git = await FlowlineValidator.Default.EnsureGitAsync(settings, cancellationToken);
            Console.MarkupLine($"[bold]Git[/] version: [green]{git.Version}[/]");
        }
        catch
        {
            // PAC CLI and Git checks will exit the application if not found
        }

        // Show the current configuration
        var rootFolder = FlowlineCommand<Settings>.FindProjectRoot(Directory.GetCurrentDirectory()) ?? Directory.GetCurrentDirectory();
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
        var solutions = config.Solution is not null
            ? new List<ProjectSolution> { config.Solution }
            : new List<ProjectSolution>();

        // Sequential, not folded into the Task.WhenAll below — this is a separate, cheap, local-file
        // check (no pac.exe subprocess), and keeping it out of the concurrent block keeps that block
        // exactly as it was before this check existed.
        var profileNotes = BuildProfileNotes(envs, dataverseConnector.FindBestProfile, dataverseConnector.IsProfileActive);

        StatusGrid.EnvStatus[] results;

        if (hasUrls)
        {
            results = await Console.Status().FlowlineSpinner().StartAsync(
                "Checking environments...",
                _ => Task.WhenAll(envs.Select(async e =>
                {
                    var who = !string.IsNullOrEmpty(e.Url)
                        ? await PacUtils.GetEnvWhoAsync(e.Url!, cancellationToken)
                        : null;

                    var versions = new Dictionary<string, string?>();
                    if (who is not null && solutions.Count > 0)
                    {
                        var versionTasks = solutions.Select(async sol =>
                        {
                            string? version = null;
                            try
                            {
                                version = await PacUtils.GetSolutionVersionAsync(sol.UniqueName, e.Url!, _capture, cancellationToken);
                            }
                            catch (FlowlineException)
                            {
                                // solution not deployed or version unreadable
                            }
                            return (sol.UniqueName, version);
                        });
                        foreach (var (name, ver) in await Task.WhenAll(versionTasks))
                            versions[name] = ver;
                    }

                    return new StatusGrid.EnvStatus(e.Label, e.Url, who, versions);
                })));
        }
        else
        {
            results = envs.Select(e => new StatusGrid.EnvStatus(e.Label, e.Url, null, new Dictionary<string, string?>())).ToArray();
        }

        foreach (var (label, url, who, _) in results)
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
                Console.MarkupLine($"    [yellow]✗ Not authenticated[/]");

            if (profileNotes.TryGetValue(label, out var note) && note is not null)
                Console.MarkupLine($"    [yellow]⚠[/] {Markup.Escape(note)}");
        }

        Console.MarkupLine("");

        if (solutions.Count == 0)
        {
            Console.MarkupLine("[dim]No solutions configured[/]");
            return 0;
        }

        async Task<bool> IsRepoDirtyAsync(string solutionName)
        {
            // Scoped to the whole solution folder, not just Package/src -- deploy's own
            // dirty gate (GitUtils.AssertRepoCleanAsync) blocks on any uncommitted change
            // anywhere in the repo, and things like deploymentSettings.json or build
            // artifacts under the solution folder are just as relevant as the unpacked XML.
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

        var dirtyByName = (await Task.WhenAll(solutions.Select(async sol => (sol.UniqueName, IsDirty: await IsRepoDirtyAsync(sol.UniqueName)))))
            .ToDictionary(x => x.UniqueName, x => x.IsDirty);

        var (headers, rows) = StatusGrid.BuildGridRows(solutions, results,
            solutionName => DeployCommand.ReadLocalSolutionVersion(
                FlowlineCommand<Settings>.PackageFolder(rootFolder)),
            solutionName => dirtyByName.GetValueOrDefault(solutionName));

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
