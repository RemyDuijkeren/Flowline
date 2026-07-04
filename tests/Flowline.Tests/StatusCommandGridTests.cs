using System.Xml;
using FluentAssertions;
using Flowline.Commands;
using Flowline.Config;
using Flowline.Utils;
using Spectre.Console.Testing;

namespace Flowline.Tests;

public class StatusCommandGridTests
{
    private static ProjectSolution Solution(string name) => new() { Name = name };

    private static StatusCommand.EnvStatus EnvResult(
        string label, string? url, WhoAmIInfo? who, Dictionary<string, string?>? versions = null) =>
        new(label, url, who, versions ?? new Dictionary<string, string?>());

    // ── BuildGridRows: happy path ─────────────────────────────────────────────

    [Fact]
    public void BuildGridRows_VersionInEveryColumn_WhenDeployedAndCloned()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusCommand.EnvStatus>
        {
            EnvResult("Dev", "https://dev.crm.dynamics.com/", new WhoAmIInfo("user@contoso.com"),
                new Dictionary<string, string?> { ["MySolution"] = "1.4.0" }),
        };

        var (headers, rows) = StatusCommand.BuildGridRows(solutions, envResults, _ => "1.5.0");

        headers.Should().Equal("Dev");
        rows.Should().ContainSingle();
        rows[0].SolutionName.Should().Be("MySolution");
        rows[0].Local.Kind.Should().Be(StatusCommand.GridCellKind.Version);
        rows[0].Local.Value.Should().Be("1.5.0");
        rows[0].EnvCells.Should().ContainSingle();
        rows[0].EnvCells[0].Kind.Should().Be(StatusCommand.GridCellKind.Version);
        rows[0].EnvCells[0].Value.Should().Be("1.4.0");
    }

    // ── Local column ───────────────────────────────────────────────────────────

    [Fact]
    public void BuildGridRows_LocalIsDash_WhenLocalVersionReaderThrowsFlowlineException()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusCommand.EnvStatus>();

        var (_, rows) = StatusCommand.BuildGridRows(solutions, envResults,
            _ => throw new FlowlineException(ExitCode.NotFound, "not cloned"));

        rows[0].Local.Kind.Should().Be(StatusCommand.GridCellKind.Dash);
    }

    [Fact]
    public void BuildGridRows_LocalIsDash_WhenLocalVersionReaderThrowsMalformedXml()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusCommand.EnvStatus>();

        var (_, rows) = StatusCommand.BuildGridRows(solutions, envResults,
            _ => throw new XmlException("malformed Solution.xml"));

        rows[0].Local.Kind.Should().Be(StatusCommand.GridCellKind.Dash);
    }

    // ── Env columns: not deployed ───────────────────────────────────────────────

    [Fact]
    public void BuildGridRows_EnvCellIsDash_WhenAuthenticatedButSolutionNotInVersions()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusCommand.EnvStatus>
        {
            EnvResult("Dev", "https://dev.crm.dynamics.com/", new WhoAmIInfo("user@contoso.com")),
        };

        var (_, rows) = StatusCommand.BuildGridRows(solutions, envResults, _ => null);

        rows[0].Local.Kind.Should().Be(StatusCommand.GridCellKind.Dash);
        rows[0].EnvCells[0].Kind.Should().Be(StatusCommand.GridCellKind.Dash);
    }

    // ── Env columns: auth failure ───────────────────────────────────────────────

    [Fact]
    public void BuildGridRows_EveryRowGetsAuthFailedMarker_WhenEnvConfiguredButUnauthenticated()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution"), Solution("OtherSolution") };
        var envResults = new List<StatusCommand.EnvStatus>
        {
            EnvResult("UAT", "https://uat.crm.dynamics.com/", null,
                new Dictionary<string, string?> { ["MySolution"] = "1.3.0" }),
        };

        var (_, rows) = StatusCommand.BuildGridRows(solutions, envResults, _ => null);

        rows.Should().AllSatisfy(r => r.EnvCells[0].Kind.Should().Be(StatusCommand.GridCellKind.AuthFailed));
    }

    // ── Env columns: unconfigured omitted ───────────────────────────────────────

    [Fact]
    public void BuildGridRows_UnconfiguredEnvIsAbsentFromHeaders()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusCommand.EnvStatus>
        {
            EnvResult("Dev", "https://dev.crm.dynamics.com/", new WhoAmIInfo("user@contoso.com")),
            EnvResult("Test", null, null),
        };

        var (headers, rows) = StatusCommand.BuildGridRows(solutions, envResults, _ => null);

        headers.Should().Equal("Dev");
        rows[0].EnvCells.Should().ContainSingle();
    }

    // ── Zero solutions ───────────────────────────────────────────────────────────

    [Fact]
    public void BuildGridRows_ReturnsEmptyRows_WhenNoSolutionsConfigured()
    {
        var envResults = new List<StatusCommand.EnvStatus>
        {
            EnvResult("Dev", "https://dev.crm.dynamics.com/", new WhoAmIInfo("user@contoso.com")),
        };

        var (headers, rows) = StatusCommand.BuildGridRows([], envResults, _ => null);

        headers.Should().Equal("Dev");
        rows.Should().BeEmpty();
    }

    // ── RenderGrid ───────────────────────────────────────────────────────────────

    [Fact]
    public void RenderGrid_HeadersInLocalSolutionEnvOrder_WithMixedCells()
    {
        var console = new TestConsole();
        var rows = new List<StatusCommand.GridRow>
        {
            new("MySolution", StatusCommand.GridCell.OfVersion("1.5.0"),
                [StatusCommand.GridCell.OfVersion("1.4.0"), StatusCommand.GridCell.Dash, StatusCommand.GridCell.AuthFailed]),
        };

        StatusCommand.RenderGrid(console, ["Dev", "Test", "UAT"], rows);

        var output = console.Output;
        output.Should().Contain("Local");
        output.Should().Contain("Solution");
        output.IndexOf("Local", StringComparison.Ordinal).Should().BeLessThan(output.IndexOf("Solution", StringComparison.Ordinal));
        output.IndexOf("Solution", StringComparison.Ordinal).Should().BeLessThan(output.IndexOf("Dev", StringComparison.Ordinal));
        output.IndexOf("Dev", StringComparison.Ordinal).Should().BeLessThan(output.IndexOf("Test", StringComparison.Ordinal));
        output.IndexOf("Test", StringComparison.Ordinal).Should().BeLessThan(output.IndexOf("UAT", StringComparison.Ordinal));
        output.Should().Contain("MySolution");
        output.Should().Contain("1.5.0");
        output.Should().Contain("1.4.0");
        output.Should().Contain("—");
        output.Should().Contain("✗");
    }

    [Fact]
    public void RenderGrid_ExcludedColumnDoesNotAppear()
    {
        var console = new TestConsole();
        var rows = new List<StatusCommand.GridRow>
        {
            new("MySolution", StatusCommand.GridCell.Dash, [StatusCommand.GridCell.OfVersion("1.4.0")]),
        };

        StatusCommand.RenderGrid(console, ["Dev"], rows);

        console.Output.Should().NotContain("Prod");
        console.Output.Should().NotContain("UAT");
    }
}
