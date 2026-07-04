using System.ComponentModel;
using Flowline.Config;
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

    internal enum GridCellKind { Version, Dash, AuthFailed }

    internal readonly record struct GridCell(GridCellKind Kind, string? Value = null)
    {
        public static readonly GridCell Dash = new(GridCellKind.Dash);
        public static readonly GridCell AuthFailed = new(GridCellKind.AuthFailed);
        public static GridCell OfVersion(string value) => new(GridCellKind.Version, value);
    }

    internal sealed record GridRow(string SolutionName, GridCell Local, IReadOnlyList<GridCell> EnvCells);

    internal static (IReadOnlyList<string> EnvHeaders, IReadOnlyList<GridRow> Rows) BuildGridRows(
        IReadOnlyList<ProjectSolution> solutions,
        IReadOnlyList<(string Label, string? Url, WhoAmIInfo? Who, Dictionary<string, string?> Versions)> envResults,
        Func<string, string?> readLocalVersion)
    {
        var configuredEnvs = envResults.Where(e => !string.IsNullOrEmpty(e.Url)).ToList();
        var headers = configuredEnvs.Select(e => e.Label).ToList();

        var rows = solutions.Select(sol =>
        {
            GridCell local;
            try
            {
                var version = readLocalVersion(sol.Name);
                local = version is not null ? GridCell.OfVersion(version) : GridCell.Dash;
            }
            catch (FlowlineException)
            {
                local = GridCell.Dash;
            }

            var envCells = configuredEnvs.Select(e =>
            {
                if (e.Who is null)
                    return GridCell.AuthFailed;

                return e.Versions.TryGetValue(sol.Name, out var version) && version is not null
                    ? GridCell.OfVersion(version)
                    : GridCell.Dash;
            }).ToList();

            return new GridRow(sol.Name, local, envCells);
        }).ToList();

        return (headers, rows);
    }

    internal static void RenderGrid(IAnsiConsole console, IReadOnlyList<string> envHeaders, IReadOnlyList<GridRow> rows)
    {
        var table = new Table();
        table.AddColumn("Local");
        table.AddColumn("Solution");
        foreach (var header in envHeaders)
            table.AddColumn(header);

        foreach (var row in rows)
        {
            var cells = new List<string> { RenderCell(row.Local), Markup.Escape(row.SolutionName) };
            cells.AddRange(row.EnvCells.Select(RenderCell));
            table.AddRow(cells.ToArray());
        }

        console.Write(table);
    }

    private static string RenderCell(GridCell cell) => cell.Kind switch
    {
        GridCellKind.Version => $"[green]{Markup.Escape(cell.Value!)}[/]",
        GridCellKind.Dash => "[dim]—[/]",
        GridCellKind.AuthFailed => "[yellow]✗[/]",
        _ => throw new ArgumentOutOfRangeException(nameof(cell)),
    };

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
        var rootFolder = FlowlineCommand<Settings>.FindProjectRoot(Directory.GetCurrentDirectory());
        var config = ProjectConfig.Load(rootFolder);
        Console.MarkupLine("\n[bold]Configuration[/]");

        if (config is null)
        {
            Console.MarkupLine("  [yellow]No .flowline config found[/]");
            return 0;
        }

        var envs = new (string Label, string? Url)[]
        {
            ("Production",  config.ProdUrl),
            ("UAT",         config.UatUrl),
            ("Test",        config.TestUrl),
            ("Development", config.DevUrl),
        };

        var hasUrls = envs.Any(e => !string.IsNullOrEmpty(e.Url));
        var solutions = config.Solutions.ToList();

        (string Label, string? Url, WhoAmIInfo? Who, Dictionary<string, string?> Versions)[] results;

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

                    return (e.Label, e.Url, who, versions);
                })));
        }
        else
        {
            results = envs.Select(e => (e.Label, e.Url, (WhoAmIInfo?)null, new Dictionary<string, string?>())).ToArray();
        }

        foreach (var (label, url, who, versions) in results)
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

            foreach (var (solutionName, version) in versions)
            {
                if (version is not null)
                    Console.MarkupLine($"    {Markup.Escape(solutionName)}  [green]{Markup.Escape(version)}[/]");
                else
                    Console.MarkupLine($"    [yellow]{Markup.Escape(solutionName)}  not deployed[/]");
            }
        }

        return 0;
    }
}
