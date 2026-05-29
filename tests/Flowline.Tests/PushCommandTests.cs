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
    public void ResolveProjectScope_WithAssemblyOnly_ShouldReturnAssemblyOnly()
    {
        var settings = new PushCommand.Settings { Scopes = [PushCommand.PushScope.AssemblyOnly] };

        PushCommand.ResolveProjectScope(settings).Should().Be(PushCommand.PushScope.AssemblyOnly);
    }

    [Fact]
    public void ResolveProjectScope_WithAssemblyOnlyAndPlugins_ShouldThrow()
    {
        var settings = new PushCommand.Settings { Scopes = [PushCommand.PushScope.AssemblyOnly, PushCommand.PushScope.Plugins] };

        var act = () => PushCommand.ResolveProjectScope(settings);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ResolveStandaloneScope_WithDllAndAssemblyOnlyScope_ShouldReturnAssemblyOnly()
    {
        var settings = new PushCommand.Settings { Dll = "plugins.dll", Scopes = [PushCommand.PushScope.AssemblyOnly] };

        PushCommand.ResolveStandaloneScope(settings).Should().Be(PushCommand.PushScope.AssemblyOnly);
    }

    [Fact]
    public void ValidateStandaloneMode_WithScope_ShouldThrow()
    {
        var settings = new PushCommand.Settings
        {
            Dll = "plugins.dll",
            Scopes = [PushCommand.PushScope.Plugins]
        };

        var act = () => PushCommand.ValidateStandaloneMode(settings, _root);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ValidateStandaloneMode_WithDllAndAssemblyOnlyScope_ShouldNotThrow()
    {
        var settings = new PushCommand.Settings
        {
            Dll = "plugins.dll",
            Scopes = [PushCommand.PushScope.AssemblyOnly]
        };

        var act = () => PushCommand.ValidateStandaloneMode(settings, _root);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateStandaloneMode_WithFlowlineFile_ShouldThrow()
    {
        File.WriteAllText(Path.Combine(_root, ".flowline"), "{}");
        var settings = new PushCommand.Settings { Dll = "plugins.dll" };

        var act = () => PushCommand.ValidateStandaloneMode(settings, _root);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ResolveStandaloneSolutionName_WithoutSolution_ShouldThrow()
    {
        var settings = new PushCommand.Settings { Dll = "plugins.dll" };

        var act = () => PushCommand.ResolveStandaloneSolutionName(settings);

        act.Should().Throw<FlowlineException>();
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
    public void ResolveStandaloneDllPath_WithNonDll_ShouldThrow()
    {
        var file = Path.Combine(_root, "plugins.txt");
        File.WriteAllText(file, "");
        var settings = new PushCommand.Settings { Dll = file };

        var act = () => PushCommand.ResolveStandaloneDllPath(settings);

        act.Should().Throw<FlowlineException>();
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