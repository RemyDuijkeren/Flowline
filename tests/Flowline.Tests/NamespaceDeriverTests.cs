using Flowline.Utils;
using FluentAssertions;

namespace Flowline.Tests;

public class NamespaceDeriverTests : IDisposable
{
    readonly string _tempDir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public NamespaceDeriverTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose() => Directory.Delete(_tempDir, recursive: true);

    // Helper: the pre-Flowline conventional project — Plugins/Plugins.csproj with no solution file, so
    // derivation goes through PluginProjectResolver.ConventionalCandidate. Deliberately NOT renamed to the
    // scaffolded <SolutionName>.Plugins.csproj: that fallback only ever fires in hand-built or migrated
    // repos, which are exactly the ones carrying this name. The scaffolded layout is covered by the
    // CreateSolutionWith tests below.
    void CreateCsproj(string content)
    {
        var pluginsDir = Path.Combine(_tempDir, "Plugins");
        Directory.CreateDirectory(pluginsDir);
        File.WriteAllText(Path.Combine(pluginsDir, "Plugins.csproj"), content);
    }

    [Fact]
    public async Task Derive_NoCsproj_ReturnsSolutionNameModels()
    {
        var result = await NamespaceDeriver.DeriveAsync(_tempDir, "MyApp");

        result.Should().Be("MyApp.Models");
    }

    [Fact]
    public async Task Derive_RootNamespace_ReturnsRootNamespaceDotModels()
    {
        CreateCsproj("<Project><PropertyGroup><RootNamespace>Contoso.Plugins</RootNamespace></PropertyGroup></Project>");

        var result = await NamespaceDeriver.DeriveAsync(_tempDir, "MyApp");

        result.Should().Be("Contoso.Plugins.Models");
    }

    [Fact]
    public async Task Derive_PackageIdNoRootNamespace_ReturnsPackageIdDotModels()
    {
        CreateCsproj("<Project><PropertyGroup><PackageId>Contoso.Plugins</PackageId></PropertyGroup></Project>");

        var result = await NamespaceDeriver.DeriveAsync(_tempDir, "MyApp");

        result.Should().Be("Contoso.Plugins.Models");
    }

    [Fact]
    public async Task Derive_EmptyRootNamespace_FallsBackToPackageId()
    {
        CreateCsproj("<Project><PropertyGroup><RootNamespace></RootNamespace><PackageId>Contoso.Plugins</PackageId></PropertyGroup></Project>");

        var result = await NamespaceDeriver.DeriveAsync(_tempDir, "MyApp");

        result.Should().Be("Contoso.Plugins.Models");
    }

    [Fact]
    public async Task Derive_ConventionalProjectDeclaringNeither_ReturnsFilenameModels()
    {
        CreateCsproj("<Project><PropertyGroup></PropertyGroup></Project>");

        var result = await NamespaceDeriver.DeriveAsync(_tempDir, "MyApp");

        // The conventional fallback's filename is still literally "Plugins".
        result.Should().Be("Plugins.Models");
    }

    [Fact]
    public async Task Derive_RootNamespaceTakesPrecedenceOverPackageId()
    {
        CreateCsproj("<Project><PropertyGroup><RootNamespace>Root.NS</RootNamespace><PackageId>Pkg.Id</PackageId></PropertyGroup></Project>");

        var result = await NamespaceDeriver.DeriveAsync(_tempDir, "MyApp");

        result.Should().Be("Root.NS.Models");
    }

    [Fact]
    public async Task Derive_WhitespaceOnlyRootNamespace_FallsBackToPackageId()
    {
        CreateCsproj("<Project><PropertyGroup><RootNamespace>   </RootNamespace><PackageId>Contoso.Plugins</PackageId></PropertyGroup></Project>");

        var result = await NamespaceDeriver.DeriveAsync(_tempDir, "MyApp");

        result.Should().Be("Contoso.Plugins.Models");
    }

    [Fact]
    public async Task Derive_InvalidXmlCsproj_ReturnsSolutionNameModels()
    {
        var pluginsDir = Path.Combine(_tempDir, "Plugins");
        Directory.CreateDirectory(pluginsDir);
        File.WriteAllText(Path.Combine(pluginsDir, "Plugins.csproj"), "not xml <<<");

        var result = await NamespaceDeriver.DeriveAsync(_tempDir, "MyApp");

        result.Should().Be("MyApp.Models");
    }
    /// <summary>Writes a project plus a solution file listing both, so discovery has real candidates.</summary>
    void CreateSolutionWith(params (string Folder, string File, string Content)[] projects)
    {
        foreach (var (folder, file, content) in projects)
        {
            Directory.CreateDirectory(Path.Combine(_tempDir, folder));
            File.WriteAllText(Path.Combine(_tempDir, folder, file), content);
        }

        var entries = projects.Select(p => $"  <Project Path=\"{p.Folder}/{p.File}\" />");
        File.WriteAllText(
            Path.Combine(_tempDir, "App.slnx"),
            "<Solution>" + Environment.NewLine + string.Join(Environment.NewLine, entries) + Environment.NewLine + "</Solution>");
    }

    [Fact]
    public async Task DeriveAsync_LibrarySortsBeforeThePluginProject_StillTakesTheOneDeclaringANamespace()
    {
        // "Common/" sorts before "Plugins/", and the pre-filter lets a marker-free project through when a
        // Directory.Build.props could be supplying its SDK reference — so path order alone would generate
        // models under Contoso.Common.Models and break every file referencing them.
        File.WriteAllText(Path.Combine(_tempDir, "Directory.Build.props"), "<Project />");
        CreateSolutionWith(
            ("Common", "Contoso.Common.csproj", "<Project><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.CrmSdk.CoreAssemblies\" /></ItemGroup></Project>"),
            ("Plugins", "Contoso.Plugins.csproj", "<Project><PropertyGroup><TargetFramework>net462</TargetFramework><RootNamespace>Contoso.Plugins</RootNamespace></PropertyGroup></Project>"));

        var result = await NamespaceDeriver.DeriveAsync(_tempDir, "MyApp");

        result.Should().Be("Contoso.Plugins.Models");
    }

    [Fact]
    public async Task DeriveAsync_NoCandidateDeclaresANamespace_FallsBackToPathOrder()
    {
        // Nothing to prefer, so the deterministic choice stands rather than inventing a tie-break.
        CreateSolutionWith(
            ("Common", "Contoso.Common.csproj", "<Project><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.CrmSdk.CoreAssemblies\" /></ItemGroup></Project>"),
            ("Plugins", "Contoso.Plugins.csproj", "<Project><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.CrmSdk.CoreAssemblies\" /></ItemGroup></Project>"));

        var result = await NamespaceDeriver.DeriveAsync(_tempDir, "MyApp");

        result.Should().Be("Contoso.Common.Models");
    }

    [Fact]
    public async Task ResolvePrimaryProjectAsync_RelocatedPluginProject_ReturnsItsPath()
    {
        // generate's default output folder is derived from this path, so a plugin project moved out of
        // Plugins/ must resolve to its new location — otherwise the models land beside the old folder while
        // the namespace follows the new one.
        CreateSolutionWith(
            ("src/Plugins", "MyApp.Plugins.csproj", "<Project><PropertyGroup><TargetFramework>net462</TargetFramework><PackageId>MyApp.Plugins</PackageId></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.CrmSdk.CoreAssemblies\" /></ItemGroup></Project>"));

        var result = await NamespaceDeriver.ResolvePrimaryProjectAsync(_tempDir);

        result.Should().Be(Path.GetFullPath(Path.Combine(_tempDir, "src", "Plugins", "MyApp.Plugins.csproj")));
    }

    [Fact]
    public async Task ResolvePrimaryProjectAsync_NoPluginProjectOnDisk_ReturnsNull()
    {
        var result = await NamespaceDeriver.ResolvePrimaryProjectAsync(_tempDir);

        result.Should().BeNull();
    }

    [Fact]
    public async Task DeriveAsync_ScaffoldedProjectDeclaringOnlyPackageId_IsPreferred()
    {
        // pac plugin init always writes PackageId, so it is the marker a scaffolded project carries.
        CreateSolutionWith(
            ("Common", "Contoso.Common.csproj", "<Project><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.CrmSdk.CoreAssemblies\" /></ItemGroup></Project>"),
            ("Plugins", "Contoso.Plugins.csproj", "<Project><PropertyGroup><TargetFramework>net462</TargetFramework><PackageId>Contoso.Plugins</PackageId></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.CrmSdk.CoreAssemblies\" /></ItemGroup></Project>"));

        var result = await NamespaceDeriver.DeriveAsync(_tempDir, "MyApp");

        result.Should().Be("Contoso.Plugins.Models");
    }

    [Fact]
    public async Task DeriveAsync_ScaffoldedProjectDeclaringNeither_FollowsTheSolutionNamedFilename()
    {
        // Clone scaffolds Plugins/<SolutionName>.Plugins.csproj, so rule (3) — the csproj filename —
        // now yields the solution-prefixed namespace rather than the bare "Plugins".
        CreateSolutionWith(
            ("Plugins", "MyApp.Plugins.csproj", "<Project><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include=\"Microsoft.CrmSdk.CoreAssemblies\" /></ItemGroup></Project>"));

        var result = await NamespaceDeriver.DeriveAsync(_tempDir, "MyApp");

        result.Should().Be("MyApp.Plugins.Models");
    }

}
