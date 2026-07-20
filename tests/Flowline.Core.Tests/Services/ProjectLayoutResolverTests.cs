using Flowline.Core;
using Flowline.Core.Services;
using FluentAssertions;

namespace Flowline.Core.Tests.Services;

/// <summary>
/// U4: the package project and the WebResources project come from solution-file membership, so no folder
/// or project name is welded into the commands any more.
/// </summary>
public class ProjectLayoutResolverTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"ProjectLayout_{Guid.NewGuid():N}");

    public ProjectLayoutResolverTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string WriteProject(string relativePath, string xml)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, xml);
        return full;
    }

    /// <summary>Writes a solution file through the real writer, so both formats are the ones Flowline emits.</summary>
    async Task WriteSolutionAsync(string fileName, params string[] relativeProjectPaths)
    {
        var writer = new MsBuildSolutionWriter();
        var path = Path.Combine(_root, fileName);

        foreach (var project in relativeProjectPaths)
            await writer.AddProjectAsync(path, project);
    }

    Task WriteSlnxAsync(params string[] relativeProjectPaths) => WriteSolutionAsync("Test.slnx", relativeProjectPaths);

    Task WriteSlnAsync(params string[] relativeProjectPaths) => WriteSolutionAsync("Test.sln", relativeProjectPaths);

    const string CdsprojXml = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";
    const string WebResourcesXml = """<Project Sdk="Microsoft.Build.NoTargets/3.7.134"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";
    const string PluginXml = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.CrmSdk.CoreAssemblies" Version="9.0.2" /></ItemGroup></Project>""";

    // ── Package project ──────────────────────────────────────────────────────

    [Fact]
    public async Task ResolvePackageProjectAsync_SolutionFolderWithSolutionNamedCdsproj_ResolvesIt()
    {
        var cdsproj = WriteProject(Path.Combine("Solution", "DWE_Base.cdsproj"), CdsprojXml);
        await WriteSlnxAsync(@"Solution\DWE_Base.cdsproj");

        (await ProjectLayoutResolver.ResolvePackageProjectAsync(_root)).Should().Be(cdsproj);
    }

    [Fact]
    public async Task ResolvePackageProjectAsync_ProjectRelocatedAndSolutionFileUpdated_StillResolves()
    {
        // The proof the coupling is gone rather than renamed: nothing named "Solution" is left on disk.
        var cdsproj = WriteProject(Path.Combine("src", "Package", "DWE_Base.cdsproj"), CdsprojXml);
        await WriteSlnxAsync(@"src\Package\DWE_Base.cdsproj");

        (await ProjectLayoutResolver.ResolvePackageProjectAsync(_root)).Should().Be(cdsproj);
        Directory.Exists(Path.Combine(_root, "Solution")).Should().BeFalse();
    }

    [Fact]
    public async Task ResolvePackageProjectAsync_SlnAndSlnxWithTheSameEntry_ResolveIdentically()
    {
        var cdsproj = WriteProject(Path.Combine("Solution", "DWE_Base.cdsproj"), CdsprojXml);

        await WriteSlnAsync(@"Solution\DWE_Base.cdsproj");
        var fromSln = await ProjectLayoutResolver.ResolvePackageProjectAsync(_root);

        // .slnx wins the preference order once both are present, so this second call reads the other format.
        await WriteSlnxAsync(@"Solution\DWE_Base.cdsproj");
        var fromSlnx = await ProjectLayoutResolver.ResolvePackageProjectAsync(_root);

        fromSln.Should().Be(cdsproj);
        fromSlnx.Should().Be(cdsproj);
    }

    [Fact]
    public async Task ResolvePackageProjectAsync_SolutionFileWithoutACdsproj_SaysHowToAddOne()
    {
        WriteProject(Path.Combine("Plugins", "DWE_Base.Plugins.csproj"), PluginXml);
        await WriteSlnxAsync(@"Plugins\DWE_Base.Plugins.csproj");

        var act = () => ProjectLayoutResolver.ResolvePackageProjectAsync(_root);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
        (await act.Should().ThrowAsync<FlowlineException>())
            .WithMessage("*no .cdsproj*sln add*");
    }

    [Fact]
    public async Task ResolvePackageProjectAsync_NoSolutionFile_PointsAtClone()
    {
        var act = () => ProjectLayoutResolver.ResolvePackageProjectAsync(_root);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.ExitCode.Should().Be(ExitCode.NotFound);
        (await act.Should().ThrowAsync<FlowlineException>()).WithMessage("*clone*");
    }

    [Fact]
    public async Task ResolvePackageProjectAsync_CdsprojEntryPointsAtNothing_NamesThePathRatherThanReturningIt()
    {
        await WriteSlnxAsync(@"Solution\DWE_Base.cdsproj");

        var act = () => ProjectLayoutResolver.ResolvePackageProjectAsync(_root);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.ExitCode.Should().Be(ExitCode.NotFound);
        (await act.Should().ThrowAsync<FlowlineException>()).WithMessage("*DWE_Base*isn't there*");
    }

    [Fact]
    public async Task ResolvePackageProjectAsync_TwoCdsprojEntries_RefusesRatherThanPicking()
    {
        WriteProject(Path.Combine("Solution", "A.cdsproj"), CdsprojXml);
        WriteProject(Path.Combine("Other", "B.cdsproj"), CdsprojXml);
        await WriteSlnxAsync(@"Solution\A.cdsproj", @"Other\B.cdsproj");

        var act = () => ProjectLayoutResolver.ResolvePackageProjectAsync(_root);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
    }

    // ── WebResources project ─────────────────────────────────────────────────

    [Fact]
    public async Task ResolveWebResourcesProjectAsync_SolutionNamedProjectFile_ResolvesIt()
    {
        var webResources = WriteProject(Path.Combine("WebResources", "DWE_Base.WebResources.csproj"), WebResourcesXml);
        await WriteSlnxAsync(@"WebResources\DWE_Base.WebResources.csproj");

        (await ProjectLayoutResolver.ResolveWebResourcesProjectAsync(_root)).Should().Be(webResources);
    }

    [Fact]
    public async Task ResolveWebResourcesProjectAsync_ProjectRelocatedAndSolutionFileUpdated_StillResolves()
    {
        var webResources = WriteProject(Path.Combine("assets", "Web.csproj"), WebResourcesXml);
        await WriteSlnxAsync(@"assets\Web.csproj");

        // Neither the folder nor the filename says "WebResources" — only the SDK does.
        (await ProjectLayoutResolver.ResolveWebResourcesProjectAsync(_root)).Should().Be(webResources);
    }

    [Fact]
    public async Task ResolveWebResourcesProjectAsync_PluginProjectAlongside_PicksTheNoTargetsOne()
    {
        WriteProject(Path.Combine("Plugins", "DWE_Base.Plugins.csproj"), PluginXml);
        var webResources = WriteProject(Path.Combine("WebResources", "DWE_Base.WebResources.csproj"), WebResourcesXml);
        await WriteSlnxAsync(@"Plugins\DWE_Base.Plugins.csproj", @"WebResources\DWE_Base.WebResources.csproj");

        (await ProjectLayoutResolver.ResolveWebResourcesProjectAsync(_root)).Should().Be(webResources);
    }

    [Fact]
    public async Task ResolveWebResourcesProjectAsync_SlnAndSlnx_ResolveIdentically()
    {
        var webResources = WriteProject(Path.Combine("WebResources", "DWE_Base.WebResources.csproj"), WebResourcesXml);

        await WriteSlnAsync(@"WebResources\DWE_Base.WebResources.csproj");
        var fromSln = await ProjectLayoutResolver.ResolveWebResourcesProjectAsync(_root);

        await WriteSlnxAsync(@"WebResources\DWE_Base.WebResources.csproj");
        var fromSlnx = await ProjectLayoutResolver.ResolveWebResourcesProjectAsync(_root);

        fromSln.Should().Be(webResources);
        fromSlnx.Should().Be(webResources);
    }

    [Fact]
    public async Task ResolveWebResourcesProjectAsync_NoSolutionFile_DegradesToTheConventionalPath()
    {
        // Drift check, push and deploy scoping all run in repos Flowline hasn't finished scaffolding.
        (await ProjectLayoutResolver.ResolveWebResourcesProjectAsync(_root))
            .Should().Be(Path.Combine(_root, "WebResources", "WebResources.csproj"));
    }

    [Fact]
    public async Task ResolveWebResourcesProjectAsync_SolutionFileWithNoWebResourcesProject_DegradesRatherThanThrows()
    {
        WriteProject(Path.Combine("Plugins", "DWE_Base.Plugins.csproj"), PluginXml);
        await WriteSlnxAsync(@"Plugins\DWE_Base.Plugins.csproj");

        (await ProjectLayoutResolver.ResolveWebResourcesProjectAsync(_root))
            .Should().Be(Path.Combine(_root, "WebResources", "WebResources.csproj"));
    }
}
