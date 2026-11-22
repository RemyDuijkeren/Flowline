using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Solutions;

namespace Flowline.Tests;

// U5: the deploy input-path scope that feeds the git-clean gate now comes from solution-file membership rather than a fixed Plugins/Plugins.csproj (KTD12).
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
