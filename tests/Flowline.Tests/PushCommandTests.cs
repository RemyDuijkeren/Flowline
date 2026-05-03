using Flowline.Commands;
using FluentAssertions;

namespace Flowline.Tests;

public class PushCommandTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public PushCommandTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public void IsStandaloneMode_WithDll_ShouldReturnTrue()
    {
        var settings = new PushCommand.Settings { Dll = "plugins.dll" };

        PushCommand.IsStandaloneMode(settings).Should().BeTrue();
    }

    [Fact]
    public void IsStandaloneMode_WithWebResources_ShouldReturnTrue()
    {
        var settings = new PushCommand.Settings { WebResources = "dist" };

        PushCommand.IsStandaloneMode(settings).Should().BeTrue();
    }

    [Fact]
    public void ResolveProjectScope_WithoutScope_ShouldDefaultToAll()
    {
        var settings = new PushCommand.Settings();

        PushCommand.ResolveProjectScope(settings).Should().Be(PushCommand.PushScope.All);
    }

    [Fact]
    public void ResolveStandaloneScope_WithDllAndWebResources_ShouldUseProvidedArtifacts()
    {
        var settings = new PushCommand.Settings { Dll = "plugins.dll", WebResources = "dist" };

        PushCommand.ResolveStandaloneScope(settings).Should().Be(PushCommand.PushScope.Plugins | PushCommand.PushScope.WebResources);
    }

    [Fact]
    public void ValidateStandaloneMode_WithScope_ShouldReturnFalse()
    {
        var settings = new PushCommand.Settings
        {
            Dll = "plugins.dll",
            Scopes = [PushCommand.PushScope.Plugins]
        };

        PushCommand.ValidateStandaloneMode(settings, _root).Should().BeFalse();
    }

    [Fact]
    public void ValidateStandaloneMode_WithFlowlineFile_ShouldReturnFalse()
    {
        File.WriteAllText(Path.Combine(_root, ".flowline"), "{}");
        var settings = new PushCommand.Settings { Dll = "plugins.dll" };

        PushCommand.ValidateStandaloneMode(settings, _root).Should().BeFalse();
    }

    [Fact]
    public void ResolveStandaloneSolutionName_WithoutSolution_ShouldReturnNull()
    {
        var settings = new PushCommand.Settings { Dll = "plugins.dll" };

        PushCommand.ResolveStandaloneSolutionName(settings).Should().BeNull();
    }

    [Fact]
    public void ResolveStandaloneDllPath_WithExistingDll_ShouldReturnFullPath()
    {
        var dll = Path.Combine(_root, "plugins.dll");
        File.WriteAllText(dll, "");
        var settings = new PushCommand.Settings { Dll = dll };

        PushCommand.ResolveStandaloneDllPath(settings).Should().Be(Path.GetFullPath(dll));
    }

    [Fact]
    public void ResolveStandaloneDllPath_WithNonDll_ShouldReturnNull()
    {
        var file = Path.Combine(_root, "plugins.txt");
        File.WriteAllText(file, "");
        var settings = new PushCommand.Settings { Dll = file };

        PushCommand.ResolveStandaloneDllPath(settings).Should().BeNull();
    }

    [Fact]
    public void ResolveStandaloneWebResourcesPath_WithExistingFolder_ShouldReturnFullPath()
    {
        var folder = Path.Combine(_root, "dist");
        Directory.CreateDirectory(folder);
        var settings = new PushCommand.Settings { WebResources = folder };

        PushCommand.ResolveStandaloneWebResourcesPath(settings).Should().Be(Path.GetFullPath(folder));
    }
}
