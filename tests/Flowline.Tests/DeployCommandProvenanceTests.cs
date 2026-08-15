using FluentAssertions;
using Flowline.Commands;

namespace Flowline.Tests;

// U5/R11: deploy states how far its orphan verdicts can be trusted, by route. These target the pure
// message-builders directly (matching this file's established DeployCommand static-helper pattern) plus
// the git-history probe that decides which "supplied artifact" message applies.
public class DeployCommandProvenanceTests
{
    // ── BuildPackedRouteProvenanceNote (packed or cached build) ────────────────

    [Fact]
    public void BuildPackedRouteProvenanceNote_CommitKnown_ReturnsNull()
    {
        // R11: verdicts compare exactly on this route, so the trusted case says nothing extra.
        DeployCommand.BuildPackedRouteProvenanceNote("abc123def456").Should().BeNull();
    }

    [Fact]
    public void BuildPackedRouteProvenanceNote_CommitUnresolved_StatesVerdictsDescribeTheCheckout()
    {
        var note = DeployCommand.BuildPackedRouteProvenanceNote(null);

        note.Should().NotBeNull();
        note.Should().Contain("checkout");
    }

    // ── BuildPathInsideProjectProvenanceNote (--path inside a project) ─────────

    [Fact]
    public void BuildPathInsideProjectProvenanceNote_VersionFoundInHistory_WarnsNotProof()
    {
        var note = DeployCommand.BuildPathInsideProjectProvenanceNote(versionFoundInHistory: true, artifactVersion: "1.0.0.1");

        note.Should().Contain("1.0.0.1");
        note.Should().Contain("isn't proof");
    }

    [Fact]
    public void BuildPathInsideProjectProvenanceNote_VersionAbsentFromHistory_SaysCouldNotBePlaced()
    {
        var note = DeployCommand.BuildPathInsideProjectProvenanceNote(versionFoundInHistory: false, artifactVersion: "1.0.0.1");

        note.Should().Contain("1.0.0.1");
        // R11: the artifact couldn't be placed against the checkout. Asserts the claim, not one phrasing
        // of it — the wording is tone-reviewed and moves; "not in history" is the claim that must survive.
        note.Should().Contain("isn't in this checkout's history");
    }

    [Fact]
    public void BuildPathInsideProjectProvenanceNote_FoundAndNotFoundWording_AreDistinct()
    {
        var found = DeployCommand.BuildPathInsideProjectProvenanceNote(true, "1.0.0.1");
        var notFound = DeployCommand.BuildPathInsideProjectProvenanceNote(false, "1.0.0.1");

        found.Should().NotBe(notFound);
    }

    // ── PathStandaloneProvenanceNote (--path in stand-alone mode) ──────────────

    [Fact]
    public void PathStandaloneProvenanceNote_SaysResolutionWasSkipped()
    {
        DeployCommand.PathStandaloneProvenanceNote.Should().Contain("unresolved");
    }
}

// U5/R11: SolutionVersionExistsInHistoryAsync walks Other/Solution.xml's real commit history — needs a
// real temporary git repository, matching the established fixture in SolutionChangeSummaryTests.cs.
public class DeployCommandProvenanceHistoryTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "flowline-tests-provenance", Guid.NewGuid().ToString("N"));
    readonly string _dataverseSolutionFolder;

    public DeployCommandProvenanceHistoryTests()
    {
        _dataverseSolutionFolder = Path.Combine(_root, "Solution");
        Directory.CreateDirectory(Path.Combine(_dataverseSolutionFolder, "src", "Other"));
        RunGit("init");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test");
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
        {
            try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
        }
        Directory.Delete(_root, true);
    }

    void CommitSolutionXml(string version)
    {
        var path = Path.Combine(_dataverseSolutionFolder, "src", "Other", "Solution.xml");
        File.WriteAllText(path, $"""
            <?xml version="1.0" encoding="utf-8"?>
            <ImportExportXml>
              <SolutionManifest>
                <UniqueName>TestSolution</UniqueName>
                <Version>{version}</Version>
              </SolutionManifest>
            </ImportExportXml>
            """);
        RunGit("add", "Solution/src/Other/Solution.xml");
        RunGit("commit", "-m", $"bump to {version}");
    }

    void RunGit(params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }

    [Fact]
    public async Task SolutionVersionExistsInHistoryAsync_VersionCommittedEarlier_ReturnsTrue()
    {
        CommitSolutionXml("1.0.0.0");
        CommitSolutionXml("1.0.0.1");

        var found = await DeployCommand.SolutionVersionExistsInHistoryAsync(_dataverseSolutionFolder, "1.0.0.0", _root, null, default);

        found.Should().BeTrue();
    }

    [Fact]
    public async Task SolutionVersionExistsInHistoryAsync_VersionAtHead_ReturnsTrue()
    {
        CommitSolutionXml("1.0.0.0");
        CommitSolutionXml("1.0.0.1");

        var found = await DeployCommand.SolutionVersionExistsInHistoryAsync(_dataverseSolutionFolder, "1.0.0.1", _root, null, default);

        found.Should().BeTrue();
    }

    [Fact]
    public async Task SolutionVersionExistsInHistoryAsync_VersionNeverCommitted_ReturnsFalse()
    {
        CommitSolutionXml("1.0.0.0");
        CommitSolutionXml("1.0.0.1");

        var found = await DeployCommand.SolutionVersionExistsInHistoryAsync(_dataverseSolutionFolder, "9.9.9.9", _root, null, default);

        found.Should().BeFalse();
    }

    [Fact]
    public async Task SolutionVersionExistsInHistoryAsync_NoCommitsAtAll_ReturnsFalse()
    {
        // Placeholder commit unrelated to Solution.xml so HEAD exists but the file was never committed.
        File.WriteAllText(Path.Combine(_root, ".gitkeep"), "");
        RunGit("add", ".gitkeep");
        RunGit("commit", "-m", "init");

        var found = await DeployCommand.SolutionVersionExistsInHistoryAsync(_dataverseSolutionFolder, "1.0.0.0", _root, null, default);

        found.Should().BeFalse();
    }
}
