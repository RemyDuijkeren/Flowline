using Flowline.Core.Services;
using FluentAssertions;
using Xunit;

namespace Flowline.Core.Tests;

public class UpdateVersionComparerTests
{
    [Fact]
    public void GetNewerVersion_ReturnsNewestStable_WhenRunningIsStable()
    {
        UpdateVersionComparer.GetNewerVersion("0.16.0", ["0.15.0", "0.16.0", "0.17.0"])
            .Should().Be("0.17.0");
    }

    [Fact]
    public void GetNewerVersion_ReturnsNothing_WhenOnlyNewerVersionIsPrerelease()
    {
        UpdateVersionComparer.GetNewerVersion("0.16.0", ["0.16.0", "0.17.0-beta.1"])
            .Should().BeNull();
    }

    [Fact]
    public void GetNewerVersion_ReturnsNewerPrerelease_WhenRunningIsPrerelease()
    {
        UpdateVersionComparer.GetNewerVersion("0.17.0-beta.1", ["0.16.0", "0.17.0-beta.1", "0.17.0-beta.2"])
            .Should().Be("0.17.0-beta.2");
    }

    [Fact]
    public void GetNewerVersion_ReturnsStable_WhenRunningPrereleaseAndStableShipped()
    {
        UpdateVersionComparer.GetNewerVersion("0.17.0-beta.1", ["0.17.0-beta.1", "0.17.0"])
            .Should().Be("0.17.0");
    }

    [Fact]
    public void GetNewerVersion_ReturnsNothing_WhenRunningPrereleaseIsAheadOfAllPublished()
    {
        UpdateVersionComparer.GetNewerVersion("0.16.1-alpha.0.46", ["0.16.0"])
            .Should().BeNull();
    }

    [Fact]
    public void GetNewerVersion_UsesSemanticOrdering_NotStringOrdering()
    {
        UpdateVersionComparer.GetNewerVersion("0.9.0", ["0.10.0"])
            .Should().Be("0.10.0");
    }

    [Fact]
    public void GetNewerVersion_IgnoresUnparsableEntries()
    {
        UpdateVersionComparer.GetNewerVersion("0.16.0", ["not-a-version", "0.17.0"])
            .Should().Be("0.17.0");
    }

    [Fact]
    public void GetNewerVersion_ReturnsNothing_WhenPublishedListOnlyContainsRunningVersion()
    {
        // The ordinary already-up-to-date case, and the guard the cached-verdict recheck relies on.
        UpdateVersionComparer.GetNewerVersion("0.16.0", ["0.16.0"])
            .Should().BeNull();
    }

    [Fact]
    public void GetNewerVersion_ReturnsNothing_WhenPublishedListIsEmpty()
    {
        UpdateVersionComparer.GetNewerVersion("0.16.0", [])
            .Should().BeNull();
    }

    [Fact]
    public void GetNewerVersion_ReturnsNothing_WhenRunningVersionIsUnparsable()
    {
        UpdateVersionComparer.GetNewerVersion("not-a-version", ["0.17.0"])
            .Should().BeNull();
    }
}
