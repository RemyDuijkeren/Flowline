using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Services;

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

}

// U5: the deploy input-path scope — which feeds both the git-clean gate and the artifact cache key —
// now comes from solution-file membership rather than a fixed Plugins/Plugins.csproj (KTD12).
public class DeployCommandDeploymentInputPathDiscoveryTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"DeployInputPaths_{Guid.NewGuid():N}");

    const string WebResourcesProjectRelPath = @"WebResources\WebResources.csproj";

    public DeployCommandDeploymentInputPathDiscoveryTests()
    {
        Directory.CreateDirectory(_root);

        // A WebResources project is required (R5) — every test needs one on disk so `layout.WebResourcesProjectPath`
        // resolves instead of throwing; a plain marker-free csproj resolves by elimination alone as long as
        // it's the only non-plugin/PCF/test candidate (WebResourcesProjectResolver).
        WriteProject(WebResourcesProjectRelPath, WebResourcesProjectXml());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Writes a .slnx referencing the WebResources project plus each given plugin project path.</summary>
    void WriteSolution(params string[] relativePluginProjectPaths)
    {
        var allPaths = new[] { WebResourcesProjectRelPath }.Concat(relativePluginProjectPaths);
        var projects = string.Concat(allPaths.Select(p => $"""<Project Path="{p}" />"""));
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

    static string WebResourcesProjectXml() =>
        """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";

    [Fact]
    public async Task GetDeploymentInputPaths_TwoPluginProjects_IncludesBothProjectFiles()
    {
        var sales = WriteProject(Path.Combine("Sales", "Sales.Plugins.csproj"), PluginProjectXml());
        var support = WriteProject(Path.Combine("Support", "Support.Plugins.csproj"), PluginProjectXml());
        WriteSolution(@"Sales\Sales.Plugins.csproj", @"Support\Support.Plugins.csproj");

        var layout = await SolutionFileLayout.LoadAsync(_root);
        var paths = DeployCommand.GetDeploymentInputPaths(layout, Path.Combine(_root, "Solution"));

        // The second project is the point: a change to it used to escape both the git-dirty gate and
        // the cache key, so a deploy could ship an artifact that didn't match the tree.
        paths.Should().Contain(sales);
        paths.Should().Contain(support);
        paths.Should().Contain(Path.Combine(_root, "Solution"));
    }

    [Fact]
    public async Task GetDeploymentInputPaths_PluginProjectNotNamedPlugins_IsIncluded()
    {
        var project = WriteProject(Path.Combine("Sales", "AV.Sales.Plugins.csproj"), PluginProjectXml());
        WriteSolution(@"Sales\AV.Sales.Plugins.csproj");

        var layout = await SolutionFileLayout.LoadAsync(_root);
        var paths = DeployCommand.GetDeploymentInputPaths(layout, Path.Combine(_root, "Solution"));

        paths.Should().Contain(project);
        paths.Should().NotContain(Path.Combine(_root, "Plugins", "Plugins.csproj"));
    }

    [Fact]
    public async Task GetDeploymentInputPaths_NonPluginProjectInSolution_StaysOutOfScope()
    {
        // Keeps the cache key narrow: an unrelated project must not invalidate a deploy.
        WriteProject(Path.Combine("Sales", "Sales.Plugins.csproj"), PluginProjectXml());
        var tests = WriteProject(Path.Combine("tests", "Sales.Tests.csproj"),
            """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>""");
        WriteSolution(@"Sales\Sales.Plugins.csproj", @"tests\Sales.Tests.csproj");

        var layout = await SolutionFileLayout.LoadAsync(_root);
        var paths = DeployCommand.GetDeploymentInputPaths(layout, Path.Combine(_root, "Solution"));

        paths.Should().NotContain(tests);
    }

    [Fact]
    public async Task GetDeploymentInputPaths_NoSolutionFile_ThrowsRatherThanDegradingToConventionalList()
    {
        // R6: no solution file is an error now, not a fallback to the conventional Plugins/WebResources list
        // — the failure now surfaces from loading the layout, before GetDeploymentInputPaths ever runs.
        var act = async () => await SolutionFileLayout.LoadAsync(_root);

        (await act.Should().ThrowAsync<FlowlineException>()).Which.ExitCode.Should().Be(ExitCode.NotFound);
    }
}
