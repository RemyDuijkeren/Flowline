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

    // No solution file at this path, so these cover the degraded shape: discovery falls back to the
    // conventional Plugins project and the list is what it was before .sln-membership discovery landed.

    [Fact]
    public async Task GetDeploymentInputPathsAsync_NoSolutionFile_ReturnsSolutionFolderAndPluginsAndWebResourcesProjectFiles()
    {
        var slnFolder = Path.Combine("C:", "repo");

        var paths = await DeployCommand.GetDeploymentInputPathsAsync(slnFolder);

        paths.Should().BeEquivalentTo(
        [
            Path.Combine(slnFolder, "Solution"),
            Path.Combine(slnFolder, "Plugins", "Plugins.csproj"),
            Path.Combine(slnFolder, "WebResources", "WebResources.csproj")
        ]);
    }

    [Fact]
    public async Task GetDeploymentInputPathsAsync_ExcludesDocsTestsAndAgentInstructionFiles()
    {
        var slnFolder = Path.Combine("C:", "repo");

        var paths = await DeployCommand.GetDeploymentInputPathsAsync(slnFolder);

        paths.Should().NotContain(p => p.Contains("docs"));
        paths.Should().NotContain(p => p.Contains("tests"));
        paths.Should().NotContain(p => p.EndsWith("CHANGES.md"));
        paths.Should().NotContain(p => p.EndsWith("AGENTS.md") || p.EndsWith("CLAUDE.md"));
    }
}

// U5: the deploy input-path scope — which feeds both the git-clean gate and the artifact cache key —
// now comes from solution-file membership rather than a fixed Plugins/Plugins.csproj (KTD12).
public class DeployCommandDeploymentInputPathDiscoveryTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"DeployInputPaths_{Guid.NewGuid():N}");

    public DeployCommandDeploymentInputPathDiscoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    void WriteSolution(params string[] relativeProjectPaths)
    {
        var projects = string.Concat(relativeProjectPaths.Select(p => $"""<Project Path="{p}" />"""));
        File.WriteAllText(Path.Combine(_root, "Test.slnx"), $"<Solution>{projects}</Solution>");
    }

    string WriteProject(string relativePath, string xml)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, xml);
        return full;
    }

    static string PluginProjectXml() =>
        """
        <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup>
        <ItemGroup><PackageReference Include="Microsoft.CrmSdk.CoreAssemblies" Version="9.0.2" /></ItemGroup></Project>
        """;

    [Fact]
    public async Task GetDeploymentInputPathsAsync_TwoPluginProjects_IncludesBothProjectFiles()
    {
        var sales = WriteProject(Path.Combine("Sales", "Sales.Plugins.csproj"), PluginProjectXml());
        var support = WriteProject(Path.Combine("Support", "Support.Plugins.csproj"), PluginProjectXml());
        WriteSolution(@"Sales\Sales.Plugins.csproj", @"Support\Support.Plugins.csproj");

        var paths = await DeployCommand.GetDeploymentInputPathsAsync(_root);

        // The second project is the point: a change to it used to escape both the git-dirty gate and
        // the cache key, so a deploy could ship an artifact that didn't match the tree.
        paths.Should().Contain(sales);
        paths.Should().Contain(support);
        paths.Should().Contain(Path.Combine(_root, "Solution"));
    }

    [Fact]
    public async Task GetDeploymentInputPathsAsync_PluginProjectNotNamedPlugins_IsIncluded()
    {
        var project = WriteProject(Path.Combine("Sales", "AV.Sales.Plugins.csproj"), PluginProjectXml());
        WriteSolution(@"Sales\AV.Sales.Plugins.csproj");

        var paths = await DeployCommand.GetDeploymentInputPathsAsync(_root);

        paths.Should().Contain(project);
        paths.Should().NotContain(Path.Combine(_root, "Plugins", "Plugins.csproj"));
    }

    [Fact]
    public async Task GetDeploymentInputPathsAsync_NonPluginProjectInSolution_StaysOutOfScope()
    {
        // Keeps the cache key narrow: an unrelated project must not invalidate a deploy.
        WriteProject(Path.Combine("Sales", "Sales.Plugins.csproj"), PluginProjectXml());
        var tests = WriteProject(Path.Combine("tests", "Sales.Tests.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        WriteSolution(@"Sales\Sales.Plugins.csproj", @"tests\Sales.Tests.csproj");

        var paths = await DeployCommand.GetDeploymentInputPathsAsync(_root);

        paths.Should().NotContain(tests);
    }

    [Fact]
    public async Task GetDeploymentInputPathsAsync_NoSolutionFile_DegradesToConventionalList()
    {
        var paths = await DeployCommand.GetDeploymentInputPathsAsync(_root);

        paths.Should().BeEquivalentTo(
        [
            Path.Combine(_root, "Solution"),
            Path.Combine(_root, "Plugins", "Plugins.csproj"),
            Path.Combine(_root, "WebResources", "WebResources.csproj")
        ]);
    }
}
