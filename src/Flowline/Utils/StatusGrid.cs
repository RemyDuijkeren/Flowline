using System.Xml;
using Flowline.Config;
using Flowline.Core;
using Spectre.Console;

namespace Flowline.Utils;

internal static class StatusGrid
{
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
                var version = readRepoVersion(sol.UniqueName);
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

            if (isRepoDirty(sol.UniqueName))
                repo = repo with { IsDirty = true };

            var envCells = configuredEnvs.Select(e =>
            {
                if (e.Who is null)
                    return GridCell.AuthFailed;

                return e.Versions.TryGetValue(sol.UniqueName, out var version) && version is not null
                    ? GridCell.OfVersion(version)
                    : GridCell.Dash;
            }).ToList();
            envCells.Insert(repoIndex, repo);

            return new GridRow(sol.UniqueName, envCells);
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
        var table = new Table().Border(TableBorder.Horizontal);
        table.AddColumn("Solution");
        for (var i = 0; i < headers.Count; i++)
        {
            if (i > 0)
                table.AddColumn("", c => { c.Width = 3; c.Padding = new Padding(0, 0); c.Alignment = Justify.Center; });
            table.AddColumn(headers[i]);
        }

        foreach (var row in rows)
        {
            var cells = new List<string> { Markup.Escape(row.SolutionName) };
            for (var i = 0; i < row.Cells.Count; i++)
            {
                if (i > 0)
                    cells.Add(RenderArrow(row.Cells[i]));
                cells.Add(RenderCell(row.Cells[i]));
            }
            table.AddRow(cells.ToArray());
        }

        console.Write(table);
    }

    internal const string Legend =
        "[dim]—[/] not deployed   [red]✗[/] auth failed   [cyan]↑[/] behind   [yellow]⚠[/] ahead   [magenta]●[/] uncommitted";

    private static string RenderArrow(GridCell destination) => destination.Kind switch
    {
        GridCellKind.Version => destination.Drift switch
        {
            DriftKind.Pending => "[cyan]┈┈▶[/]",
            DriftKind.Inverted => "[yellow]──▶[/]",
            _ => "[green]──▶[/]",
        },
        _ => "[dim]┈┈▶[/]",
    };

    private static string RenderCell(GridCell cell)
    {
        var text = cell.Kind switch
        {
            GridCellKind.Version => cell.Drift switch
            {
                DriftKind.Pending => $"[cyan]{Markup.Escape(cell.Value!)} ↑[/]",
                DriftKind.Inverted => $"[yellow]{Markup.Escape(cell.Value!)} ⚠[/]",
                _ => $"[green]{Markup.Escape(cell.Value!)}[/]",
            },
            GridCellKind.Dash => "[dim]—[/]",
            GridCellKind.AuthFailed => "[red]✗ auth[/]",
            _ => throw new ArgumentOutOfRangeException(nameof(cell)),
        };

        return cell.IsDirty ? $"{text} [magenta]●[/]" : text;
    }
}
