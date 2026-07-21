using Flowline.Core.Models;
using Flowline.Core.Services;
using FluentAssertions;

namespace Flowline.Core.Tests.Services;

/// <summary>
/// U2: <see cref="WebResourcesProjectResolver"/> — WebResources detection by elimination plus weighted
/// signals (R9, KD3), replacing the substring-on-SDK check that missed the ClientHooks shape (finding A)
/// and picked alphabetically on a tie (finding F).
/// </summary>
public class WebResourcesProjectResolverTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"WebResourcesProjectResolver_{Guid.NewGuid():N}");
    const string SolutionFileName = "Test.slnx";

    public WebResourcesProjectResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    // ── fixture helpers ──────────────────────────────────────────────────────

    string WriteFile(string relativePath, string content)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
        return full;
    }

    static MsBuildSolutionProject CsProjectEntry(string relativePath, string name) =>
        new(relativePath, name, ".csproj");

    const string NoTargetsXml = """<Project Sdk="Microsoft.Build.NoTargets/3.7.134"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";
    const string SuppressedCompileXml = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><Target Name="CoreCompile" /><Target Name="Build" AfterTargets="CoreCompile"><Exec Command="npm run build" /></Target></Project>""";
    const string PlainCsprojXml = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";
    const string PcfCsprojXml = """<Project ToolsVersion="15.0"><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.PowerApps.MSBuild.Pcf" Version="1.*" /></ItemGroup></Project>""";
    const string TestSdkXml = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" /></ItemGroup></Project>""";

    // ── AE7: zero candidates ──────────────────────────────────────────────────

    [Fact]
    public void Resolve_NoCandidates_ThrowsConfigInvalid()
    {
        var plugins = WriteFile(Path.Combine("Plugins", "Contoso.Plugins.csproj"), PlainCsprojXml);
        var projects = new List<MsBuildSolutionProject> { CsProjectEntry(@"Plugins\Contoso.Plugins.csproj", "Contoso.Plugins") };
        var pluginPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { plugins };

        var act = () => WebResourcesProjectResolver.Resolve(projects, _root, pluginPaths, SolutionFileName);

        act.Should().Throw<FlowlineException>()
           .Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
        act.Should().Throw<FlowlineException>()
           .WithMessage("*no WebResources project*");
    }

    // ── AE8: two tied candidates ──────────────────────────────────────────────

    [Fact]
    public void Resolve_TwoEquallyWeightedCandidates_ThrowsConfigInvalidNamingBoth()
    {
        // Neither carries any signal — a 0-0 tie is still a tie (KD4): never picked alphabetically.
        WriteFile(Path.Combine("Alpha", "Alpha.csproj"), PlainCsprojXml);
        WriteFile(Path.Combine("Beta", "Beta.csproj"), PlainCsprojXml);
        var projects = new List<MsBuildSolutionProject>
        {
            CsProjectEntry(@"Alpha\Alpha.csproj", "Alpha"),
            CsProjectEntry(@"Beta\Beta.csproj", "Beta"),
        };

        var act = () => WebResourcesProjectResolver.Resolve(projects, _root, NoPlugins, SolutionFileName);

        var thrown = act.Should().Throw<FlowlineException>().Which;
        thrown.ExitCode.Should().Be(ExitCode.ConfigInvalid);
        thrown.Message.Should().Contain("Alpha.csproj").And.Contain("Beta.csproj");
    }

    // ── AE9: no plugins, WebResources still resolves ─────────────────────────

    [Fact]
    public void Resolve_NoPluginProjects_StillResolvesTheSoleWebResourcesCandidate()
    {
        var webResources = WriteFile(Path.Combine("WebResources", "Contoso.WebResources.csproj"), NoTargetsXml);
        var projects = new List<MsBuildSolutionProject>
        {
            CsProjectEntry(@"WebResources\Contoso.WebResources.csproj", "Contoso.WebResources"),
        };

        var resolved = WebResourcesProjectResolver.Resolve(projects, _root, NoPlugins, SolutionFileName);

        resolved.Should().Be(webResources);
    }

    // ── the deliberate KD3 judgment call: single candidate, zero signals ─────

    [Fact]
    public void Resolve_SingleCandidateWithZeroSignals_ResolvesByEliminationAlone()
    {
        // No NoTargets, no dist/, no package.json, no assets, no annotation, no WebResources-named folder —
        // every signal is absent, and it still resolves. Deliberate per KD3: with plugin/PCF/test already
        // excluded, a lone survivor is the WebResources project by elimination, not by score.
        var onlySurvivor = WriteFile(Path.Combine("Assets", "Assets.csproj"), PlainCsprojXml);
        var projects = new List<MsBuildSolutionProject> { CsProjectEntry(@"Assets\Assets.csproj", "Assets") };

        var resolved = WebResourcesProjectResolver.Resolve(projects, _root, NoPlugins, SolutionFileName);

        resolved.Should().Be(onlySurvivor);
    }

    // ── AE3: ClientHooks shape — no NoTargets, no WebResources folder name ───

    [Fact]
    public void Resolve_ClientHooksShape_ResolvesByEliminationAndContentSignals()
    {
        // Real shape (docs audit): empty CoreCompile + npm build, a dist/ folder, .ts sources — no
        // NoTargets SDK, folder named after the product ("mda-client-hooks"), not "WebResources". The old
        // substring-on-NoTargets check missed this project entirely (finding A); it is also the sole
        // non-plugin/PCF/test .csproj here, so it resolves by elimination regardless.
        var clientHooks = WriteFile(Path.Combine("mda-client-hooks", "ClientHooks.csproj"), SuppressedCompileXml);
        Directory.CreateDirectory(Path.Combine(_root, "mda-client-hooks", "dist"));
        WriteFile(Path.Combine("mda-client-hooks", "src", "index.ts"), "export {};");
        var projects = new List<MsBuildSolutionProject> { CsProjectEntry(@"mda-client-hooks\ClientHooks.csproj", "ClientHooks") };

        var resolved = WebResourcesProjectResolver.Resolve(projects, _root, NoPlugins, SolutionFileName);

        resolved.Should().Be(clientHooks);
    }

    // ── AE2: relocated + renamed folder, competing against a second candidate ─

    [Fact]
    public void Resolve_RelocatedWebResourcesProject_OutscoresAWeakerSecondCandidate()
    {
        // Folder-name convention signal is gone (src/WebAssets, not WebResources/) — SDK + dist/ + assets
        // must carry it against a second, signal-free non-plugin project.
        var relocated = WriteFile(Path.Combine("src", "WebAssets", "Contoso.csproj"), NoTargetsXml);
        Directory.CreateDirectory(Path.Combine(_root, "src", "WebAssets", "dist"));
        WriteFile(Path.Combine("src", "WebAssets", "index.ts"), "export {};");
        WriteFile(Path.Combine("Shared", "Shared.csproj"), PlainCsprojXml);
        var projects = new List<MsBuildSolutionProject>
        {
            CsProjectEntry(@"src\WebAssets\Contoso.csproj", "Contoso"),
            CsProjectEntry(@"Shared\Shared.csproj", "Shared"),
        };

        var resolved = WebResourcesProjectResolver.Resolve(projects, _root, NoPlugins, SolutionFileName);

        resolved.Should().Be(relocated);
    }

    // ── AE4: Flowline-annotated source resolves even against a second candidate ─

    [Fact]
    public void Resolve_FlowlineAnnotatedSource_ResolvesEvenAgainstASecondNonPluginProject()
    {
        var annotated = WriteFile(Path.Combine("Web", "Web.csproj"), PlainCsprojXml);
        WriteFile(Path.Combine("Web", "forms", "account.ts"), "// flowline:onload account main\nexport {};");
        WriteFile(Path.Combine("Other", "Other.csproj"), PlainCsprojXml);
        var projects = new List<MsBuildSolutionProject>
        {
            CsProjectEntry(@"Web\Web.csproj", "Web"),
            CsProjectEntry(@"Other\Other.csproj", "Other"),
        };

        var resolved = WebResourcesProjectResolver.Resolve(projects, _root, NoPlugins, SolutionFileName);

        resolved.Should().Be(annotated);
    }

    // ── medium-weight signals: package.json build script, bundler config, folder-name convention ─

    [Fact]
    public void Resolve_PackageJsonBundlerAndFolderNameSignals_OutscoreAZeroSignalCandidate()
    {
        // No NoTargets, no dist/, no annotation — only the three medium-weight, build-tooling signals
        // (package.json's build script, a bundler config file, the WebResources folder-name convention),
        // stacked to beat a signal-free second candidate.
        var webResources = WriteFile(Path.Combine("WebResources", "Contoso.csproj"), PlainCsprojXml);
        WriteFile(Path.Combine("WebResources", "package.json"), """{"scripts":{"build":"rollup -c"}}""");
        WriteFile(Path.Combine("WebResources", "rollup.config.js"), "export default {};");
        WriteFile(Path.Combine("Shared", "Shared.csproj"), PlainCsprojXml);
        var projects = new List<MsBuildSolutionProject>
        {
            CsProjectEntry(@"WebResources\Contoso.csproj", "Contoso"),
            CsProjectEntry(@"Shared\Shared.csproj", "Shared"),
        };

        var resolved = WebResourcesProjectResolver.Resolve(projects, _root, NoPlugins, SolutionFileName);

        resolved.Should().Be(webResources);
    }

    // ── AE5: PCF alongside a real WebResources project ───────────────────────

    [Fact]
    public void Resolve_PcfControlAlongsideWebResources_OnlyWebResourcesResolves()
    {
        var webResources = WriteFile(Path.Combine("WebResources", "Contoso.WebResources.csproj"), NoTargetsXml);
        WriteFile(Path.Combine("image-grid-pcf", "image-grid-pcf.pcfproj"), PlainCsprojXml);
        var projects = new List<MsBuildSolutionProject>
        {
            CsProjectEntry(@"WebResources\Contoso.WebResources.csproj", "Contoso.WebResources"),
            // .pcfproj is never a WebResources candidate in the first place — IsCsProject excludes it by
            // extension — so this entry only proves the resolver still lands on the real one.
        };

        var resolved = WebResourcesProjectResolver.Resolve(projects, _root, NoPlugins, SolutionFileName);

        resolved.Should().Be(webResources);
    }

    // ── AE6: PCF wrapped as .csproj, even with web assets ────────────────────

    [Fact]
    public void Resolve_PcfWrappedAsCsproj_IsExcludedEvenWithWebAssets()
    {
        var pcf = WriteFile(Path.Combine("image-grid-pcf", "image-grid-pcf.csproj"), PcfCsprojXml);
        WriteFile(Path.Combine("image-grid-pcf", "index.ts"), "export {};");
        var webResources = WriteFile(Path.Combine("WebResources", "Contoso.WebResources.csproj"), NoTargetsXml);
        var projects = new List<MsBuildSolutionProject>
        {
            CsProjectEntry(@"image-grid-pcf\image-grid-pcf.csproj", "image-grid-pcf"),
            CsProjectEntry(@"WebResources\Contoso.WebResources.csproj", "Contoso.WebResources"),
        };

        var resolved = WebResourcesProjectResolver.Resolve(projects, _root, NoPlugins, SolutionFileName);

        resolved.Should().Be(webResources);
        resolved.Should().NotBe(pcf);
    }

    // ── test-project exclusion (feeds R9's candidate filter; not its own AE) ─

    [Fact]
    public void Resolve_TestProjectAlongsideWebResources_TestProjectIsNeverACandidate()
    {
        var webResources = WriteFile(Path.Combine("WebResources", "Contoso.WebResources.csproj"), NoTargetsXml);
        WriteFile(Path.Combine("Contoso.Tests", "Contoso.Tests.csproj"), TestSdkXml);
        var projects = new List<MsBuildSolutionProject>
        {
            CsProjectEntry(@"WebResources\Contoso.WebResources.csproj", "Contoso.WebResources"),
            CsProjectEntry(@"Contoso.Tests\Contoso.Tests.csproj", "Contoso.Tests"),
        };

        var resolved = WebResourcesProjectResolver.Resolve(projects, _root, NoPlugins, SolutionFileName);

        resolved.Should().Be(webResources);
    }

    static IReadOnlySet<string> NoPlugins { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
