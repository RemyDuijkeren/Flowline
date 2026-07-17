using FluentAssertions;
using Flowline.Commands;

namespace Flowline.Tests;

public class DeployCommandArtifactCacheTests
{
    private const string Sha = "abc123def456";
    private const string OtherSha = "999999999999";

    // ── ResolveCacheOutcome ───────────────────────────────────────────────────

    [Fact]
    public void ResolveCacheOutcome_ReturnsHit_WhenShaAndManagedMatchAndFileExists()
    {
        var entry = new DeployCommand.ArtifactCacheEntry("1.0.0.1", true, Sha);

        DeployCommand.ResolveCacheOutcome(entry, Sha, wantManaged: true, noCache: false, artifactFileExists: true)
            .Should().Be(DeployCommand.CacheOutcome.Hit);
    }

    [Fact]
    public void ResolveCacheOutcome_ReturnsNoEntry_WhenEntryIsNull()
    {
        DeployCommand.ResolveCacheOutcome(null, Sha, wantManaged: true, noCache: false, artifactFileExists: true)
            .Should().Be(DeployCommand.CacheOutcome.NoEntry);
    }

    [Fact]
    public void ResolveCacheOutcome_ReturnsCommitChanged_WhenShaDiffers()
    {
        var entry = new DeployCommand.ArtifactCacheEntry("1.0.0.1", true, Sha);

        DeployCommand.ResolveCacheOutcome(entry, OtherSha, wantManaged: true, noCache: false, artifactFileExists: true)
            .Should().Be(DeployCommand.CacheOutcome.CommitChanged);
    }

    [Fact]
    public void ResolveCacheOutcome_ReturnsManagedMismatch_WhenManagedFlagDiffers()
    {
        var entry = new DeployCommand.ArtifactCacheEntry("1.0.0.1", false, Sha);

        DeployCommand.ResolveCacheOutcome(entry, Sha, wantManaged: true, noCache: false, artifactFileExists: true)
            .Should().Be(DeployCommand.CacheOutcome.ManagedMismatch);
    }

    [Fact]
    public void ResolveCacheOutcome_ReturnsArtifactFileMissing_WhenShaAndManagedMatchButFileGone()
    {
        var entry = new DeployCommand.ArtifactCacheEntry("1.0.0.1", true, Sha);

        DeployCommand.ResolveCacheOutcome(entry, Sha, wantManaged: true, noCache: false, artifactFileExists: false)
            .Should().Be(DeployCommand.CacheOutcome.ArtifactFileMissing);
    }

    [Fact]
    public void ResolveCacheOutcome_ReturnsNoCacheFlag_WhenNoCacheIsSet_EvenWithMatchingEntry()
    {
        var entry = new DeployCommand.ArtifactCacheEntry("1.0.0.1", true, Sha);

        DeployCommand.ResolveCacheOutcome(entry, Sha, wantManaged: true, noCache: true, artifactFileExists: true)
            .Should().Be(DeployCommand.CacheOutcome.NoCacheFlag);
    }

    [Fact]
    public void ResolveCacheOutcome_ReturnsNoCurrentCommit_WhenCurrentCommitShaIsNull()
    {
        var entry = new DeployCommand.ArtifactCacheEntry("1.0.0.1", true, Sha);

        DeployCommand.ResolveCacheOutcome(entry, null, wantManaged: true, noCache: false, artifactFileExists: true)
            .Should().Be(DeployCommand.CacheOutcome.NoCurrentCommit);
    }

    [Fact]
    public void ResolveCacheOutcome_ReturnsNoCacheFlag_TakesPrecedenceOverNoCurrentCommit()
    {
        // KTD6 precedence: --no-cache is checked first, before any other miss reason.
        DeployCommand.ResolveCacheOutcome(null, null, wantManaged: true, noCache: true, artifactFileExists: true)
            .Should().Be(DeployCommand.CacheOutcome.NoCacheFlag);
    }

    // ── ReadCacheEntryIfExists ────────────────────────────────────────────────

    [Fact]
    public void ReadCacheEntryIfExists_ReturnsNull_WhenFileMissing()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".manifest.json");

        DeployCommand.ReadCacheEntryIfExists(path).Should().BeNull();
    }

    [Fact]
    public void ReadCacheEntryIfExists_ReturnsNull_WhenJsonIsCorrupt()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".manifest.json");
        File.WriteAllText(path, "{ not valid json");
        try
        {
            DeployCommand.ReadCacheEntryIfExists(path).Should().BeNull();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadCacheEntryIfExists_ReturnsEntry_WhenJsonIsValid()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".manifest.json");
        File.WriteAllText(path, """{"Version":"1.0.0.1","Managed":true,"CommitSha":"abc123def456"}""");
        try
        {
            var entry = DeployCommand.ReadCacheEntryIfExists(path);

            entry.Should().NotBeNull();
            entry!.Version.Should().Be("1.0.0.1");
            entry.Managed.Should().BeTrue();
            entry.CommitSha.Should().Be("abc123def456");
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── GetDeploymentInputPaths (R15) ─────────────────────────────────────────

    [Fact]
    public void GetDeploymentInputPaths_ReturnsPackageFolderAndPluginsAndWebResourcesProjectFiles()
    {
        var slnFolder = Path.Combine("C:", "repo");

        var paths = DeployCommand.GetDeploymentInputPaths(slnFolder);

        paths.Should().BeEquivalentTo(
        [
            Path.Combine(slnFolder, "Package"),
            Path.Combine(slnFolder, "Plugins", "Plugins.csproj"),
            Path.Combine(slnFolder, "WebResources", "WebResources.csproj")
        ]);
    }

    [Fact]
    public void GetDeploymentInputPaths_ExcludesDocsTestsAndAgentInstructionFiles()
    {
        var slnFolder = Path.Combine("C:", "repo");

        var paths = DeployCommand.GetDeploymentInputPaths(slnFolder);

        paths.Should().NotContain(p => p.Contains("docs"));
        paths.Should().NotContain(p => p.Contains("tests"));
        paths.Should().NotContain(p => p.EndsWith("CHANGES.md"));
        paths.Should().NotContain(p => p.EndsWith("AGENTS.md") || p.EndsWith("CLAUDE.md"));
    }
}
