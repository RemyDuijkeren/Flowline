using Flowline.Core;
using Flowline.Core.Services;
using FluentAssertions;

namespace Flowline.Core.Tests.Services;

/// <summary>
/// U1: <see cref="SolutionFileLayout"/> reads the solution file once and classifies every project from
/// that single in-memory list — the facade that replaces three separate resolver reads.
/// </summary>
public class SolutionFileLayoutTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), $"SolutionFileLayout_{Guid.NewGuid():N}");

    public SolutionFileLayoutTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string WriteProject(string relativePath, string xml)
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, xml);
        return full;
    }

    /// <summary>Writes a solution file through the real writer, so the fixture is the shape Flowline emits.</summary>
    async Task WriteSlnxAsync(params string[] relativeProjectPaths)
    {
        var writer = new MsBuildSolutionWriter();
        var path = Path.Combine(_root, "Test.slnx");

        foreach (var project in relativeProjectPaths)
            await writer.AddProjectAsync(path, project);
    }

    const string CdsprojXml = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";
    const string WebResourcesXml = """<Project Sdk="Microsoft.Build.NoTargets/3.7.134"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""";
    const string PluginXml = """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net462</TargetFramework></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.CrmSdk.CoreAssemblies" Version="9.0.2" /></ItemGroup></Project>""";

    // ── AE1: one read resolves all three project types ──────────────────────

    // R4 (one parse per load) is not asserted at runtime here — LoadAsync's public shape takes no
    // injectable reader, so there is no seam to count reads through without changing the contract. Verified
    // by construction instead: SolutionFileLayout.cs calls reader.FindSolutionFile once (line 77) and
    // reader.ReadProjectsAsync once (line 82), and every classification step below that reads from the
    // resulting in-memory `projects` list rather than touching disk again. This test proves the *outcome*
    // (all three types resolve), not the read count.
    [Fact]
    public async Task LoadAsync_ScaffoldedRepo_ResolvesAllThreeProjectTypesFromOneRead()
    {
        var cdsproj = WriteProject(Path.Combine("Solution", "DWE_Base.cdsproj"), CdsprojXml);
        var plugins = WriteProject(Path.Combine("Plugins", "DWE_Base.Plugins.csproj"), PluginXml);
        var webResources = WriteProject(Path.Combine("WebResources", "DWE_Base.WebResources.csproj"), WebResourcesXml);
        await WriteSlnxAsync(
            @"Solution\DWE_Base.cdsproj",
            @"Plugins\DWE_Base.Plugins.csproj",
            @"WebResources\DWE_Base.WebResources.csproj");

        var layout = await SolutionFileLayout.LoadAsync(_root);

        layout.DataverseSolutionProjectPath.Should().Be(cdsproj);
        layout.DataverseSolutionFolder.Should().Be(Path.Combine(_root, "Solution"));
        layout.PluginProjects.Should().ContainSingle(p => p.ProjectPath == plugins);
        layout.WebResourcesProjectPath.Should().Be(webResources);
    }

    [Fact]
    public async Task LoadAsync_NoPluginProjects_ResolvesFineWithAnEmptyList()
    {
        // R8/AE9: zero plugin projects is a legitimate state, not an error — only WebResources and the
        // package project are load-bearing here.
        WriteProject(Path.Combine("Solution", "DWE_Base.cdsproj"), CdsprojXml);
        WriteProject(Path.Combine("WebResources", "DWE_Base.WebResources.csproj"), WebResourcesXml);
        await WriteSlnxAsync(@"Solution\DWE_Base.cdsproj", @"WebResources\DWE_Base.WebResources.csproj");

        var layout = await SolutionFileLayout.LoadAsync(_root);

        layout.PluginProjects.Should().BeEmpty();
    }

    // ── R6: no solution file is an error, not a fallback ─────────────────────

    [Fact]
    public async Task LoadAsync_NoSolutionFile_ThrowsNamingStandAloneMode()
    {
        var act = () => SolutionFileLayout.LoadAsync(_root);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.ExitCode.Should().Be(ExitCode.NotFound);
        (await act.Should().ThrowAsync<FlowlineException>())
            .WithMessage("*push --pluginFile*");
    }

    // ── R7: exactly one .cdsproj ──────────────────────────────────────────────

    [Fact]
    public async Task LoadAsync_TwoCdsprojEntries_ThrowsConfigInvalidNamingBoth()
    {
        WriteProject(Path.Combine("Solution", "A.cdsproj"), CdsprojXml);
        WriteProject(Path.Combine("Other", "B.cdsproj"), CdsprojXml);
        await WriteSlnxAsync(@"Solution\A.cdsproj", @"Other\B.cdsproj");

        var act = () => SolutionFileLayout.LoadAsync(_root);

        var thrown = (await act.Should().ThrowAsync<FlowlineException>()).Which;
        thrown.ExitCode.Should().Be(ExitCode.ConfigInvalid);
        thrown.Message.Should().Contain("A").And.Contain("B");
    }

    [Fact]
    public async Task LoadAsync_NoCdsprojEntry_ThrowsConfigInvalid()
    {
        WriteProject(Path.Combine("Plugins", "DWE_Base.Plugins.csproj"), PluginXml);
        await WriteSlnxAsync(@"Plugins\DWE_Base.Plugins.csproj");

        var act = () => SolutionFileLayout.LoadAsync(_root);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
        (await act.Should().ThrowAsync<FlowlineException>())
            .WithMessage("*no .cdsproj*sln add*");
    }

    [Fact]
    public async Task LoadAsync_CdsprojEntryPointsAtNothing_ThrowsNotFoundNamingThePath()
    {
        await WriteSlnxAsync(@"Solution\DWE_Base.cdsproj");

        var act = () => SolutionFileLayout.LoadAsync(_root);

        (await act.Should().ThrowAsync<FlowlineException>())
            .Which.ExitCode.Should().Be(ExitCode.NotFound);
        (await act.Should().ThrowAsync<FlowlineException>())
            .WithMessage("*DWE_Base*isn't there*");
    }

    // ── DataverseSolutionFolder follows a relocated .cdsproj ─────────────────

    [Fact]
    public async Task LoadAsync_RelocatedDataverseSolutionFolder_ResolvesToWhereTheCdsprojIs()
    {
        var cdsproj = WriteProject(Path.Combine("src", "Package", "Contoso.cdsproj"), CdsprojXml);
        WriteProject(Path.Combine("WebResources", "Contoso.WebResources.csproj"), WebResourcesXml);
        await WriteSlnxAsync(@"src\Package\Contoso.cdsproj", @"WebResources\Contoso.WebResources.csproj");

        var layout = await SolutionFileLayout.LoadAsync(_root);

        layout.DataverseSolutionProjectPath.Should().Be(cdsproj);
        layout.DataverseSolutionFolder.Should().Be(Path.Combine(_root, "src", "Package"));
        Directory.Exists(Path.Combine(_root, "Solution")).Should().BeFalse();
    }
}
