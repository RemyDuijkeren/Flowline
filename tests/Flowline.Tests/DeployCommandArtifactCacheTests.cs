using FluentAssertions;
using Flowline.Commands;

namespace Flowline.Tests;

public class DeployCommandArtifactCacheTests
{
    private const string Sha = "abc123def456";
    private const string OtherSha = "999999999999";

    // ── ArtifactCacheHit ──────────────────────────────────────────────────────

    [Fact]
    public void ArtifactCacheHit_ReturnsTrue_WhenShaAndManagedMatch()
    {
        var entry = new DeployCommand.ArtifactCacheEntry("1.0.0.1", true, Sha);

        DeployCommand.ArtifactCacheHit(entry, Sha, wantManaged: true).Should().BeTrue();
    }

    [Fact]
    public void ArtifactCacheHit_ReturnsFalse_WhenShaDiffers()
    {
        var entry = new DeployCommand.ArtifactCacheEntry("1.0.0.1", true, Sha);

        DeployCommand.ArtifactCacheHit(entry, OtherSha, wantManaged: true).Should().BeFalse();
    }

    [Fact]
    public void ArtifactCacheHit_ReturnsFalse_WhenManagedFlagDiffers()
    {
        var entry = new DeployCommand.ArtifactCacheEntry("1.0.0.1", false, Sha);

        DeployCommand.ArtifactCacheHit(entry, Sha, wantManaged: true).Should().BeFalse();
    }

    [Fact]
    public void ArtifactCacheHit_ReturnsFalse_WhenEntryIsNull()
    {
        DeployCommand.ArtifactCacheHit(null, Sha, wantManaged: true).Should().BeFalse();
    }

    [Fact]
    public void ArtifactCacheHit_ReturnsFalse_WhenCurrentCommitShaIsNull()
    {
        var entry = new DeployCommand.ArtifactCacheEntry("1.0.0.1", true, Sha);

        DeployCommand.ArtifactCacheHit(entry, null, wantManaged: true).Should().BeFalse();
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
}
