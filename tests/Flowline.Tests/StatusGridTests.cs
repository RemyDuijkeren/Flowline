using System.Xml;
using FluentAssertions;
using Flowline.Config;
using Flowline.Core;
using Flowline.Utils;
using Spectre.Console.Testing;

namespace Flowline.Tests;

public class StatusGridTests
{
    private static ProjectSolution Solution(string name) => new() { Name = name };

    private static StatusGrid.EnvStatus EnvResult(
        string label, string? url, WhoAmIInfo? who, Dictionary<string, string?>? versions = null) =>
        new(label, url, who, versions ?? new Dictionary<string, string?>());

    // ── BuildGridRows: happy path ─────────────────────────────────────────────

    [Fact]
    public void BuildGridRows_VersionInEveryColumn_WhenDeployedAndCloned()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusGrid.EnvStatus>
        {
            EnvResult("Dev", "https://dev.crm.dynamics.com/", new WhoAmIInfo("user@contoso.com"),
                new Dictionary<string, string?> { ["MySolution"] = "1.4.0" }),
        };

        var (headers, rows) = StatusGrid.BuildGridRows(solutions, envResults, _ => "1.5.0", _ => false);

        headers.Should().Equal("Dev", "Repo");
        rows.Should().ContainSingle();
        rows[0].SolutionName.Should().Be("MySolution");
        rows[0].Cells[0].Kind.Should().Be(StatusGrid.GridCellKind.Version);
        rows[0].Cells[0].Value.Should().Be("1.4.0");
        rows[0].Cells[1].Kind.Should().Be(StatusGrid.GridCellKind.Version);
        rows[0].Cells[1].Value.Should().Be("1.5.0");
    }

    // ── Repo column ordering ───────────────────────────────────────────────────

    [Fact]
    public void BuildGridRows_InsertsRepoRightAfterDev_WhenDevConfigured()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusGrid.EnvStatus>
        {
            EnvResult("Dev", "https://dev.crm.dynamics.com/", new WhoAmIInfo("user@contoso.com")),
            EnvResult("Test", "https://test.crm.dynamics.com/", new WhoAmIInfo("user@contoso.com")),
        };

        var (headers, _) = StatusGrid.BuildGridRows(solutions, envResults, _ => "1.0.0", _ => false);

        headers.Should().Equal("Dev", "Repo", "Test");
    }

    [Fact]
    public void BuildGridRows_RepoLeadsChain_WhenDevNotConfigured()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusGrid.EnvStatus>
        {
            EnvResult("Test", "https://test.crm.dynamics.com/", new WhoAmIInfo("user@contoso.com")),
        };

        var (headers, rows) = StatusGrid.BuildGridRows(solutions, envResults, _ => "1.0.0", _ => false);

        headers.Should().Equal("Repo", "Test");
        rows[0].Cells[0].Kind.Should().Be(StatusGrid.GridCellKind.Version);
        rows[0].Cells[0].Value.Should().Be("1.0.0");
    }

    // ── Repo cell ────────────────────────────────────────────────────────────

    [Fact]
    public void BuildGridRows_RepoIsDash_WhenRepoVersionReaderThrowsFlowlineException()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusGrid.EnvStatus>();

        var (_, rows) = StatusGrid.BuildGridRows(solutions, envResults,
            _ => throw new FlowlineException(ExitCode.NotFound, "not cloned"), _ => false);

        rows[0].Cells[0].Kind.Should().Be(StatusGrid.GridCellKind.Dash);
    }

    [Fact]
    public void BuildGridRows_RepoIsDash_WhenRepoVersionReaderThrowsMalformedXml()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusGrid.EnvStatus>();

        var (_, rows) = StatusGrid.BuildGridRows(solutions, envResults,
            _ => throw new XmlException("malformed Solution.xml"), _ => false);

        rows[0].Cells[0].Kind.Should().Be(StatusGrid.GridCellKind.Dash);
    }

    // ── Repo cell: dirty indicator ────────────────────────────────────────────

    [Fact]
    public void BuildGridRows_MarksRepoDirty_WhenIsRepoDirtyReturnsTrue()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusGrid.EnvStatus>();

        var (_, rows) = StatusGrid.BuildGridRows(solutions, envResults, _ => "1.0.36", _ => true);

        rows[0].Cells[0].IsDirty.Should().BeTrue();
    }

    [Fact]
    public void BuildGridRows_RepoNotDirty_WhenIsRepoDirtyReturnsFalse()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusGrid.EnvStatus>();

        var (_, rows) = StatusGrid.BuildGridRows(solutions, envResults, _ => "1.0.36", _ => false);

        rows[0].Cells[0].IsDirty.Should().BeFalse();
    }

    [Fact]
    public void BuildGridRows_MarksRepoDirty_EvenWhenRepoVersionIsDash()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusGrid.EnvStatus>();

        var (_, rows) = StatusGrid.BuildGridRows(solutions, envResults,
            _ => throw new FlowlineException(ExitCode.NotFound, "not cloned"), _ => true);

        rows[0].Cells[0].Kind.Should().Be(StatusGrid.GridCellKind.Dash);
        rows[0].Cells[0].IsDirty.Should().BeTrue();
    }

    // ── Env columns: not deployed ───────────────────────────────────────────────

    [Fact]
    public void BuildGridRows_EnvAndRepoCellsAreDash_WhenAuthenticatedButSolutionNotInVersions()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusGrid.EnvStatus>
        {
            EnvResult("Dev", "https://dev.crm.dynamics.com/", new WhoAmIInfo("user@contoso.com")),
        };

        var (_, rows) = StatusGrid.BuildGridRows(solutions, envResults, _ => null, _ => false);

        rows[0].Cells[0].Kind.Should().Be(StatusGrid.GridCellKind.Dash);
        rows[0].Cells[1].Kind.Should().Be(StatusGrid.GridCellKind.Dash);
    }

    // ── Env columns: auth failure ───────────────────────────────────────────────

    [Fact]
    public void BuildGridRows_EveryRowGetsAuthFailedMarker_WhenEnvConfiguredButUnauthenticated()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution"), Solution("OtherSolution") };
        var envResults = new List<StatusGrid.EnvStatus>
        {
            EnvResult("UAT", "https://uat.crm.dynamics.com/", null,
                new Dictionary<string, string?> { ["MySolution"] = "1.3.0" }),
        };

        var (_, rows) = StatusGrid.BuildGridRows(solutions, envResults, _ => null, _ => false);

        rows.Should().AllSatisfy(r => r.Cells[1].Kind.Should().Be(StatusGrid.GridCellKind.AuthFailed));
    }

    // ── Env columns: unconfigured omitted ───────────────────────────────────────

    [Fact]
    public void BuildGridRows_UnconfiguredEnvIsAbsentFromHeaders()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusGrid.EnvStatus>
        {
            EnvResult("Dev", "https://dev.crm.dynamics.com/", new WhoAmIInfo("user@contoso.com")),
            EnvResult("Test", null, null),
        };

        var (headers, rows) = StatusGrid.BuildGridRows(solutions, envResults, _ => null, _ => false);

        headers.Should().Equal("Dev", "Repo");
        rows[0].Cells.Should().HaveCount(2);
    }

    // ── Zero solutions ───────────────────────────────────────────────────────────

    [Fact]
    public void BuildGridRows_ReturnsEmptyRows_WhenNoSolutionsConfigured()
    {
        var envResults = new List<StatusGrid.EnvStatus>
        {
            EnvResult("Dev", "https://dev.crm.dynamics.com/", new WhoAmIInfo("user@contoso.com")),
        };

        var (headers, rows) = StatusGrid.BuildGridRows([], envResults, _ => null, _ => false);

        headers.Should().Equal("Dev", "Repo");
        rows.Should().BeEmpty();
    }

    // ── TrimUnusedRevisionSegment ──────────────────────────────────────────────

    [Fact]
    public void TrimUnusedRevisionSegment_DropsFourthSegment_WhenRevisionIsZeroEverywhere()
    {
        var rows = new List<StatusGrid.GridRow>
        {
            new("MySolution",
                [StatusGrid.GridCell.OfVersion("1.0.36.0"), StatusGrid.GridCell.OfVersion("1.0.36.0"), StatusGrid.GridCell.Dash]),
        };

        var trimmed = StatusGrid.TrimUnusedRevisionSegment(rows);

        trimmed[0].Cells[0].Value.Should().Be("1.0.36");
        trimmed[0].Cells[1].Value.Should().Be("1.0.36");
        trimmed[0].Cells[2].Kind.Should().Be(StatusGrid.GridCellKind.Dash);
    }

    [Fact]
    public void TrimUnusedRevisionSegment_PreservesIsDirty_WhenTrimmingVersion()
    {
        var rows = new List<StatusGrid.GridRow>
        {
            new("MySolution", [StatusGrid.GridCell.OfVersion("1.0.36.0") with { IsDirty = true }]),
        };

        var trimmed = StatusGrid.TrimUnusedRevisionSegment(rows);

        trimmed[0].Cells[0].Value.Should().Be("1.0.36");
        trimmed[0].Cells[0].IsDirty.Should().BeTrue();
    }

    [Fact]
    public void TrimUnusedRevisionSegment_LeavesVersionsUntouched_WhenAnyRevisionIsNonZero()
    {
        var rows = new List<StatusGrid.GridRow>
        {
            new("MySolution", [StatusGrid.GridCell.OfVersion("1.0.36.0"), StatusGrid.GridCell.OfVersion("1.0.36.1")]),
        };

        var trimmed = StatusGrid.TrimUnusedRevisionSegment(rows);

        trimmed[0].Cells[0].Value.Should().Be("1.0.36.0");
        trimmed[0].Cells[1].Value.Should().Be("1.0.36.1");
    }

    [Fact]
    public void TrimUnusedRevisionSegment_LeavesVersionsUntouched_WhenAnyVersionHasDifferentSegmentCount()
    {
        var rows = new List<StatusGrid.GridRow>
        {
            new("MySolution", [StatusGrid.GridCell.OfVersion("1.0.36.0"), StatusGrid.GridCell.OfVersion("1.0.0")]),
        };

        var trimmed = StatusGrid.TrimUnusedRevisionSegment(rows);

        trimmed[0].Cells[0].Value.Should().Be("1.0.36.0");
        trimmed[0].Cells[1].Value.Should().Be("1.0.0");
    }

    // ── DetectVersionDrift: walks Cells positionally ────────────────────────────

    [Fact]
    public void DetectVersionDrift_MarksLaggingCellPending_WhenUpstreamIsNewer()
    {
        var rows = new List<StatusGrid.GridRow>
        {
            new("MySolution", [StatusGrid.GridCell.OfVersion("1.0.37"), StatusGrid.GridCell.OfVersion("1.0.36")]),
        };

        var drifted = StatusGrid.DetectVersionDrift(rows);

        drifted[0].Cells[0].Drift.Should().Be(StatusGrid.DriftKind.None);
        drifted[0].Cells[1].Drift.Should().Be(StatusGrid.DriftKind.Pending);
    }

    [Fact]
    public void DetectVersionDrift_MarksCellInverted_WhenDownstreamIsNewerThanUpstream()
    {
        var rows = new List<StatusGrid.GridRow>
        {
            new("MySolution",
                [StatusGrid.GridCell.OfVersion("1.0.36"), StatusGrid.GridCell.OfVersion("1.0.36"), StatusGrid.GridCell.OfVersion("1.0.38")]),
        };

        var drifted = StatusGrid.DetectVersionDrift(rows);

        drifted[0].Cells[0].Drift.Should().Be(StatusGrid.DriftKind.None);
        drifted[0].Cells[1].Drift.Should().Be(StatusGrid.DriftKind.None);
        drifted[0].Cells[2].Drift.Should().Be(StatusGrid.DriftKind.Inverted);
    }

    [Fact]
    public void DetectVersionDrift_NoDrift_WhenAllVersionsMatch()
    {
        var rows = new List<StatusGrid.GridRow>
        {
            new("MySolution",
                [StatusGrid.GridCell.OfVersion("1.0.36"), StatusGrid.GridCell.OfVersion("1.0.36"), StatusGrid.GridCell.OfVersion("1.0.36")]),
        };

        var drifted = StatusGrid.DetectVersionDrift(rows);

        drifted[0].Cells.Should().AllSatisfy(c => c.Drift.Should().Be(StatusGrid.DriftKind.None));
    }

    [Fact]
    public void DetectVersionDrift_SkipsOverDashAndAuthFailedCells_WhenComparingUpstream()
    {
        var rows = new List<StatusGrid.GridRow>
        {
            new("MySolution",
                [StatusGrid.GridCell.OfVersion("1.0.37"), StatusGrid.GridCell.Dash, StatusGrid.GridCell.AuthFailed, StatusGrid.GridCell.OfVersion("1.0.36")]),
        };

        var drifted = StatusGrid.DetectVersionDrift(rows);

        drifted[0].Cells[0].Drift.Should().Be(StatusGrid.DriftKind.None);
        drifted[0].Cells[3].Drift.Should().Be(StatusGrid.DriftKind.Pending);
    }

    // ── Full pipeline: Repo's true predecessor is Dev, not display position ────

    [Fact]
    public void FullPipeline_MarksRepoInverted_WhenRepoAheadOfDev()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusGrid.EnvStatus>
        {
            EnvResult("Dev", "https://dev.crm.dynamics.com/", new WhoAmIInfo("user@contoso.com"),
                new Dictionary<string, string?> { ["MySolution"] = "1.0.36" }),
        };

        var (headers, rows) = StatusGrid.BuildGridRows(solutions, envResults, _ => "1.0.40", _ => false);
        rows = StatusGrid.DetectVersionDrift(rows);

        headers.Should().Equal("Dev", "Repo");
        rows[0].Cells[0].Drift.Should().Be(StatusGrid.DriftKind.None);
        rows[0].Cells[1].Drift.Should().Be(StatusGrid.DriftKind.Inverted);
    }

    [Fact]
    public void FullPipeline_MarksRepoPending_WhenRepoBehindDev()
    {
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusGrid.EnvStatus>
        {
            EnvResult("Dev", "https://dev.crm.dynamics.com/", new WhoAmIInfo("user@contoso.com"),
                new Dictionary<string, string?> { ["MySolution"] = "1.0.36" }),
        };

        var (_, rows) = StatusGrid.BuildGridRows(solutions, envResults, _ => "1.0.35", _ => false);
        rows = StatusGrid.DetectVersionDrift(rows);

        rows[0].Cells[1].Drift.Should().Be(StatusGrid.DriftKind.Pending);
    }

    [Fact]
    public void FullPipeline_ComparesTestAgainstRepo_NotDev()
    {
        // Repo is ahead of Dev; Test matches Dev exactly. Because the true dependency chain is
        // Dev -> Repo -> Test, Test's predecessor is Repo, not Dev, so Test (behind Repo) is
        // flagged Pending even though it exactly matches Dev.
        var solutions = new List<ProjectSolution> { Solution("MySolution") };
        var envResults = new List<StatusGrid.EnvStatus>
        {
            EnvResult("Dev", "https://dev.crm.dynamics.com/", new WhoAmIInfo("user@contoso.com"),
                new Dictionary<string, string?> { ["MySolution"] = "1.0.36" }),
            EnvResult("Test", "https://test.crm.dynamics.com/", new WhoAmIInfo("user@contoso.com"),
                new Dictionary<string, string?> { ["MySolution"] = "1.0.36" }),
        };

        var (headers, rows) = StatusGrid.BuildGridRows(solutions, envResults, _ => "1.0.40", _ => false);
        rows = StatusGrid.DetectVersionDrift(rows);

        headers.Should().Equal("Dev", "Repo", "Test");
        rows[0].Cells[0].Drift.Should().Be(StatusGrid.DriftKind.None);
        rows[0].Cells[1].Drift.Should().Be(StatusGrid.DriftKind.Inverted);
        rows[0].Cells[2].Drift.Should().Be(StatusGrid.DriftKind.Pending);
    }

    // ── RenderGrid ───────────────────────────────────────────────────────────────

    [Fact]
    public void RenderGrid_HeadersInSolutionDevRepoEnvOrder_WithMixedCells()
    {
        var console = new TestConsole();
        var rows = new List<StatusGrid.GridRow>
        {
            new("MySolution",
                [StatusGrid.GridCell.OfVersion("1.4.0"), StatusGrid.GridCell.OfVersion("1.5.0"), StatusGrid.GridCell.Dash, StatusGrid.GridCell.AuthFailed]),
        };

        StatusGrid.RenderGrid(console, ["Dev", "Repo", "Test", "UAT"], rows);

        var output = console.Output;
        output.Should().Contain("Solution");
        output.Should().Contain("Repo");
        output.IndexOf("Solution", StringComparison.Ordinal).Should().BeLessThan(output.IndexOf("Dev", StringComparison.Ordinal));
        output.IndexOf("Dev", StringComparison.Ordinal).Should().BeLessThan(output.IndexOf("Repo", StringComparison.Ordinal));
        output.IndexOf("Repo", StringComparison.Ordinal).Should().BeLessThan(output.IndexOf("Test", StringComparison.Ordinal));
        output.IndexOf("Test", StringComparison.Ordinal).Should().BeLessThan(output.IndexOf("UAT", StringComparison.Ordinal));
        output.Should().Contain("MySolution");
        output.Should().Contain("1.4.0");
        output.Should().Contain("1.5.0");
        output.Should().Contain("—");
        output.Should().Contain("✗");
    }

    [Fact]
    public void RenderGrid_RendersDriftMarkers_ForPendingAndInvertedCells()
    {
        var console = new TestConsole();
        var rows = new List<StatusGrid.GridRow>
        {
            new("MySolution",
                [
                    StatusGrid.GridCell.OfVersion("1.0.37"),
                    StatusGrid.GridCell.OfVersion("1.0.36") with { Drift = StatusGrid.DriftKind.Pending },
                    StatusGrid.GridCell.OfVersion("1.0.38") with { Drift = StatusGrid.DriftKind.Inverted },
                ]),
        };

        StatusGrid.RenderGrid(console, ["Dev", "Test", "Prod"], rows);

        var output = console.Output;
        output.Should().Contain("1.0.36 ↑");
        output.Should().Contain("1.0.38 ⚠");
    }

    [Fact]
    public void RenderGrid_RendersDirtyMarker_IndependentlyOfDriftMarker()
    {
        var console = new TestConsole();
        var rows = new List<StatusGrid.GridRow>
        {
            new("MySolution",
                [
                    StatusGrid.GridCell.OfVersion("1.0.37") with { IsDirty = true },
                    StatusGrid.GridCell.OfVersion("1.0.36") with { Drift = StatusGrid.DriftKind.Pending, IsDirty = true },
                ]),
        };

        StatusGrid.RenderGrid(console, ["Dev", "Test"], rows);

        var output = console.Output;
        output.Should().Contain("1.0.37 ●");
        output.Should().Contain("1.0.36 ↑ ●");
    }

    [Fact]
    public void RenderGrid_ExcludedColumnDoesNotAppear()
    {
        var console = new TestConsole();
        var rows = new List<StatusGrid.GridRow>
        {
            new("MySolution", [StatusGrid.GridCell.OfVersion("1.4.0")]),
        };

        StatusGrid.RenderGrid(console, ["Dev"], rows);

        console.Output.Should().NotContain("Prod");
        console.Output.Should().NotContain("UAT");
    }
}
