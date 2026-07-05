using System.ComponentModel;
using System.Xml;
using Flowline.Config;
using Flowline.Core;
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

    internal enum GridCellKind { Dash, Version, AuthFailed }

    internal enum DriftKind { None, Pending, Inverted }

    internal readonly record struct GridCell(GridCellKind Kind, string? Value = null, DriftKind Drift = DriftKind.None, bool IsDirty = false)
    {
        public static readonly GridCell Dash = new(GridCellKind.Dash);
        public static readonly GridCell AuthFailed = new(GridCellKind.AuthFailed);
        public static GridCell OfVersion(string value) => new(GridCellKind.Version, value);
    }

    internal sealed record GridRow(string SolutionName, IReadOnlyList<GridCell> Cells);

    internal readonly record struct EnvStatus(string Label, string? Url, WhoAmIInfo? Who, Dictionary<string, string?> Versions);

    internal static (IReadOnlyList<string> Headers, IReadOnlyList<GridRow> Rows) BuildGridRows(
        IReadOnlyList<ProjectSolution> solutions,
        IReadOnlyList<EnvStatus> envResults,
        Func<string, string?> readRepoVersion,
        Func<string, bool> isRepoDirty)
    {
        var configuredEnvs = envResults.Where(e => !string.IsNullOrEmpty(e.Url)).ToList();

        // Repo is fed by syncing Dev (Dev's version leads, Repo follows), so it belongs right after
        // Dev in the true dependency order -- or leads the chain when Dev isn't configured.
        var repoIndex = configuredEnvs.Count > 0 && configuredEnvs[0].Label == "Dev" ? 1 : 0;

        var headers = configuredEnvs.Select(e => e.Label).ToList();
        headers.Insert(repoIndex, "Repo");

        var rows = solutions.Select(sol =>
        {
            GridCell repo;
            try
            {
                var version = readRepoVersion(sol.Name);
                repo = version is not null ? GridCell.OfVersion(version) : GridCell.Dash;
            }
            catch (FlowlineException)
            {
                repo = GridCell.Dash;
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                repo = GridCell.Dash;
            }

            if (isRepoDirty(sol.Name))
                repo = repo with { IsDirty = true };

            var envCells = configuredEnvs.Select(e =>
            {
                if (e.Who is null)
                    return GridCell.AuthFailed;

                return e.Versions.TryGetValue(sol.Name, out var version) && version is not null
                    ? GridCell.OfVersion(version)
                    : GridCell.Dash;
            }).ToList();
            envCells.Insert(repoIndex, repo);

            return new GridRow(sol.Name, envCells);
        }).ToList();

        return (headers, rows);
    }

    internal static IReadOnlyList<GridRow> TrimUnusedRevisionSegment(IReadOnlyList<GridRow> rows)
    {
        var versionCells = rows.SelectMany(r => r.Cells).Where(c => c.Kind == GridCellKind.Version).ToList();

        var revisionUnused = versionCells.Count > 0 && versionCells.All(c =>
        {
            var parts = c.Value!.Split('.');
            return parts.Length == 4 && parts[3] == "0";
        });

        if (!revisionUnused)
            return rows;

        GridCell Trim(GridCell cell) => cell.Kind == GridCellKind.Version
            ? cell with { Value = string.Join('.', cell.Value!.Split('.').Take(3)) }
            : cell;

        return rows.Select(r => r with { Cells = r.Cells.Select(Trim).ToList() }).ToList();
    }

    internal static IReadOnlyList<GridRow> DetectVersionDrift(IReadOnlyList<GridRow> rows) => rows.Select(row =>
    {
        var cells = row.Cells.ToList();

        GridCell? previousVersionCell = null;
        for (var i = 0; i < cells.Count; i++)
        {
            if (cells[i].Kind != GridCellKind.Version)
                continue;

            if (previousVersionCell is { } prev &&
                Version.TryParse(prev.Value, out var prevVersion) &&
                Version.TryParse(cells[i].Value, out var currVersion) &&
                currVersion != prevVersion)
            {
                cells[i] = cells[i] with { Drift = currVersion < prevVersion ? DriftKind.Pending : DriftKind.Inverted };
            }

            previousVersionCell = cells[i];
        }

        return row with { Cells = cells };
    }).ToList();

    internal static void RenderGrid(IAnsiConsole console, IReadOnlyList<string> headers, IReadOnlyList<GridRow> rows)
    {
        var table = new Table();
        table.AddColumn("Solution");
        foreach (var header in headers)
            table.AddColumn(header);

        foreach (var row in rows)
        {
            var cells = new List<string> { Markup.Escape(row.SolutionName) };
            cells.AddRange(row.Cells.Select(RenderCell));
            table.AddRow(cells.ToArray());
        }

        console.Write(table);
    }

    private const string Legend =
        "[dim]—[/] not deployed   [yellow]✗[/] auth failed   [cyan]↑[/] behind   [red]⚠[/] ahead   [magenta]●[/] uncommitted";

    private static string RenderCell(GridCell cell)
    {
        var text = cell.Kind switch
        {
            GridCellKind.Version => cell.Drift switch
            {
                DriftKind.Pending => $"[cyan]{Markup.Escape(cell.Value!)} ↑[/]",
                DriftKind.Inverted => $"[red]{Markup.Escape(cell.Value!)} ⚠[/]",
                _ => $"[green]{Markup.Escape(cell.Value!)}[/]",
            },
            GridCellKind.Dash => "[dim]—[/]",
            GridCellKind.AuthFailed => "[yellow]✗ auth[/]",
            _ => throw new ArgumentOutOfRangeException(nameof(cell)),
        };

        return cell.IsDirty ? $"{text} [magenta]●[/]" : text;
    }

    protected override async Task<int> ExecuteAsync(CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
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

        EnvStatus[] results;

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

                    return new EnvStatus(e.Label, e.Url, who, versions);
                })));
        }
        else
        {
            results = envs.Select(e => new EnvStatus(e.Label, e.Url, null, new Dictionary<string, string?>())).ToArray();
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

        var (headers, rows) = BuildGridRows(solutions, results,
            solutionName => DeployCommand.ReadLocalSolutionVersion(
                FlowlineCommand<Settings>.PackageFolder(Path.Combine(rootFolder, "solutions", solutionName))),
            solutionName => dirtyByName.GetValueOrDefault(solutionName));

        rows = DetectVersionDrift(TrimUnusedRevisionSegment(rows));
        RenderGrid(Console, headers, rows);
        Console.MarkupLine(Legend);

        var driftKinds = rows.SelectMany(r => r.Cells).Select(c => c.Drift).ToHashSet();

        if (driftKinds.Contains(DriftKind.Inverted))
        {
            Console.MarkupLine("");
            Console.Warning("an environment is ahead of an earlier stage — check the grid above.");
        }
        else if (driftKinds.Contains(DriftKind.Pending))
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
