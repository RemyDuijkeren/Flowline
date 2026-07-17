using System.ComponentModel;
using Flowline.Config;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Diagnostics;
using Flowline.Utils;
using Flowline.Validation;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Flowline.Commands;

public class StatusCommand(IAnsiConsole console, SubprocessCapture capture, ILoggerFactory loggerFactory) : AsyncCommand<StatusCommand.Settings>
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
        var solutions = config.Solutions.ToList();

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
                                version = await PacUtils.GetSolutionVersionAsync(sol.Name, e.Url!, _capture, cancellationToken);
                            }
                            catch (FlowlineException)
                            {
                                // solution not deployed or version unreadable
                            }
                            return (sol.Name, version);
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
            var solutionPath = Path.Combine(rootFolder, "solutions", solutionName);
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

        var dirtyByName = (await Task.WhenAll(solutions.Select(async sol => (sol.Name, IsDirty: await IsRepoDirtyAsync(sol.Name)))))
            .ToDictionary(x => x.Name, x => x.IsDirty);

        var (headers, rows) = StatusGrid.BuildGridRows(solutions, results,
            solutionName => DeployCommand.ReadLocalSolutionVersion(
                FlowlineCommand<Settings>.PackageFolder(Path.Combine(rootFolder, "solutions", solutionName))),
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
