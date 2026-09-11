using CliWrap;
using CliWrap.Buffered;
using Flowline.Core;

namespace Flowline.Utils;

/// <summary>One connection in an environment, as <c>pac connection list</c> reports it.</summary>
/// <param name="Id">
/// The connection's name, as <c>shared-commondataser-863397d0-10d3-43b1-8824-5fd5d954356a</c>. This is the
/// value a connection reference binds to and the settings file carries, despite the column heading.
/// </param>
/// <param name="Name">The label its owner gave it, which is often their own address.</param>
/// <param name="ConnectorId">
/// Which connector it is for, matching <c>connectionreference.connectorid</c>.
/// </param>
/// <param name="Status">Its health, as PAC words it. <c>Connected</c> is the working case.</param>
public sealed record PacConnection(string Id, string Name, string ConnectorId, string Status);

/// <summary>
/// Lists an environment's connections, so binding a connection reference can be a pick rather than a typed
/// GUID.
/// </summary>
/// <remarks>
/// Connections do not live in Dataverse, so this cannot be a query alongside the rest of the inventory:
/// they belong to the Power Platform, and `pac` is the only first-party route to them that Flowline
/// already has a dependency on.
/// </remarks>
public static class PacConnections
{
    /// <summary>Lists the connections the signed-in identity can see in one environment.</summary>
    /// <remarks>
    /// Returns what it found, or nothing at all when `pac` failed. A caller is mid-prompt by the time this
    /// runs, so a failure has to narrow the choices rather than end the run — the caller says so and falls
    /// back to a typed id.
    /// </remarks>
    public static async Task<IReadOnlyList<PacConnection>> ListAsync(
        string environmentUrl, CancellationToken ct)
    {
        var (cmdName, prefixArgs, _) = await PacUtils.GetBestPacCommandAsync(ct);

        var result = await Cli.Wrap(cmdName)
            .WithArguments(args => args
                .AddIfNotNull(prefixArgs)
                .Add("connection")
                .Add("list")
                .Add("--environment").Add(environmentUrl))
            .WithValidation(CommandResultValidation.None)
            .ExecuteBufferedAsync(ct);

        return result.ExitCode == 0 ? Parse(result.StandardOutput) : [];
    }

    /// <summary>
    /// Narrows a listing to the connections a given connection reference could actually bind to.
    /// </summary>
    /// <remarks>
    /// Binding across connectors is not a thing, so every other connection in the environment is noise.
    /// A reference with no connector recorded gets the whole list rather than an empty one: showing
    /// everything is recoverable, showing nothing looks like the environment has no connections.
    /// </remarks>
    public static IReadOnlyList<PacConnection> ForConnector(
        IEnumerable<PacConnection> connections, string? connectorId) =>
        connections
            .Where(c => string.IsNullOrEmpty(connectorId)
                        || string.Equals(c.ConnectorId, connectorId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    /// <summary>
    /// Reads the table `pac connection list` prints.
    /// </summary>
    /// <remarks>
    /// By column offset, not by splitting on whitespace: a connection called "HTTP Request AD" is ordinary
    /// and would split into three. There is no JSON to ask for — `--json` is rejected on this verb — so the
    /// table is the only contract available.
    ///
    /// Offsets are read from the run's own header line rather than fixed, because PAC pads each column to
    /// its widest cell: one long connection name moves every column to its right. The header is found by
    /// the heading it carries, not by line number, because `pac` prints a "Connected as ..." line above it.
    /// </remarks>
    internal static IReadOnlyList<PacConnection> Parse(string output)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        var headerIndex = Array.FindIndex(lines, l =>
            l.Contains("Id", StringComparison.Ordinal) && l.Contains("API Id", StringComparison.Ordinal));
        if (headerIndex < 0) return [];

        var header = lines[headerIndex];
        var idAt = header.IndexOf("Id", StringComparison.Ordinal);
        var nameAt = header.IndexOf("Name", StringComparison.Ordinal);
        var connectorAt = header.IndexOf("API Id", StringComparison.Ordinal);
        var statusAt = header.IndexOf("Status", StringComparison.Ordinal);

        if (idAt < 0 || nameAt <= idAt || connectorAt <= nameAt || statusAt <= connectorAt) return [];

        var connections = new List<PacConnection>();

        foreach (var line in lines.Skip(headerIndex + 1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            var connection = new PacConnection(
                Slice(line, idAt, nameAt),
                Slice(line, nameAt, connectorAt),
                Slice(line, connectorAt, statusAt),
                Slice(line, statusAt, line.Length));

            // A row with no id is a footer or a wrapped line, not a connection.
            if (connection.Id.Length > 0) connections.Add(connection);
        }

        return connections;
    }

    static string Slice(string line, int from, int to)
    {
        if (from >= line.Length) return string.Empty;

        return line[from..Math.Min(to, line.Length)].Trim();
    }
}
