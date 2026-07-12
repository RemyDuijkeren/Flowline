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
    public void IsStandaloneMode_WithPluginFile_ShouldReturnTrue()
    {
        var settings = new PushCommand.Settings { PluginFile = "plugins.dll" };

        PushCommand.IsStandaloneMode(settings).Should().BeTrue();
    }

    [Fact]
    public void IsStandaloneMode_WithWebResources_ShouldReturnTrue()
    {
        var settings = new PushCommand.Settings { WebResources = "dist" };

        PushCommand.IsStandaloneMode(settings).Should().BeTrue();
    }

    [Fact]
    public void ResolveScope_ProjectMode_WithoutScope_ShouldDefaultToAll()
    {
        var settings = new PushCommand.Settings();

        PushCommand.ResolveScope(settings, standaloneMode: false).Should().Be(PushCommand.PushScope.All);
    }

    [Fact]
    public void ResolveScope_StandaloneMode_WithPluginFileAndWebResources_ShouldDeriveScope()
    {
        var settings = new PushCommand.Settings { PluginFile = "plugins.dll", WebResources = "dist" };

        PushCommand.ResolveScope(settings, standaloneMode: true).Should().Be(PushCommand.PushScope.Plugins | PushCommand.PushScope.WebResources);
    }

    [Fact]
    public void ResolveScope_WithAssemblyOnly_ShouldReturnAssemblyOnly()
    {
        var settings = new PushCommand.Settings { Scopes = [PushCommand.PushScope.AssemblyOnly] };

        PushCommand.ResolveScope(settings, standaloneMode: false).Should().Be(PushCommand.PushScope.AssemblyOnly);
    }

    [Fact]
    public void ResolveScope_WithAssemblyOnlyAndPlugins_ShouldThrow()
    {
        var settings = new PushCommand.Settings { Scopes = [PushCommand.PushScope.AssemblyOnly, PushCommand.PushScope.Plugins] };

        var act = () => PushCommand.ResolveScope(settings, standaloneMode: false);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ResolveScope_StandaloneMode_WithPluginFileAndAssemblyOnlyScope_ShouldReturnAssemblyOnly()
    {
        var settings = new PushCommand.Settings { PluginFile = "plugins.dll", Scopes = [PushCommand.PushScope.AssemblyOnly] };

        PushCommand.ResolveScope(settings, standaloneMode: true).Should().Be(PushCommand.PushScope.AssemblyOnly);
    }

    [Fact]
    public void ResolveScope_StandaloneMode_WithPluginsScopeAndPluginFile_ShouldReturnPlugins()
    {
        var settings = new PushCommand.Settings { PluginFile = "plugins.dll", Scopes = [PushCommand.PushScope.Plugins] };

        PushCommand.ResolveScope(settings, standaloneMode: true).Should().Be(PushCommand.PushScope.Plugins);
    }

    [Fact]
    public void ResolveScope_StandaloneMode_WithPluginsScopeButNoPluginFile_ShouldThrow()
    {
        var settings = new PushCommand.Settings { Scopes = [PushCommand.PushScope.Plugins] };

        var act = () => PushCommand.ResolveScope(settings, standaloneMode: true);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ResolveScope_StandaloneMode_WithWebResourcesScopeButNoPath_ShouldThrow()
    {
        var settings = new PushCommand.Settings { Scopes = [PushCommand.PushScope.WebResources] };

        var act = () => PushCommand.ResolveScope(settings, standaloneMode: true);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ResolveScope_WithFormEvents_ShouldReturnFormEvents()
    {
        var settings = new PushCommand.Settings { Scopes = [PushCommand.PushScope.FormEvents] };

        PushCommand.ResolveScope(settings, standaloneMode: false).Should().Be(PushCommand.PushScope.FormEvents);
    }

    [Fact]
    public void ResolveScope_StandaloneMode_WithFormEventsScopeButNoWebResourcesPath_ShouldThrow()
    {
        // FormEvents reads its annotations from the same --webresources folder web resource sync uses,
        // so it needs that path even when requested on its own, without --scope webresources.
        var settings = new PushCommand.Settings { Scopes = [PushCommand.PushScope.FormEvents] };

        var act = () => PushCommand.ResolveScope(settings, standaloneMode: true);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ResolveScope_StandaloneMode_WithFormEventsScopeAndWebResourcesPath_ShouldReturnFormEvents()
    {
        var settings = new PushCommand.Settings { WebResources = "dist", Scopes = [PushCommand.PushScope.FormEvents] };

        PushCommand.ResolveScope(settings, standaloneMode: true).Should().Be(PushCommand.PushScope.FormEvents);
    }

    [Fact]
    public void ValidateStandaloneMode_WithFlowlineFile_ShouldThrow()
    {
        File.WriteAllText(Path.Combine(_root, ".flowline"), "{}");
        var settings = new PushCommand.Settings { PluginFile = "plugins.dll" };

        var act = () => PushCommand.ValidateStandaloneMode(settings, _root);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ResolveStandaloneSolutionName_WithoutSolution_ShouldThrow()
    {
        var settings = new PushCommand.Settings { PluginFile = "plugins.dll" };

        var act = () => PushCommand.ResolveStandaloneSolutionName(settings);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ResolveStandalonePluginFilePath_WithExistingDll_ShouldReturnFullPath()
    {
        var dll = Path.Combine(_root, "plugins.dll");
        File.WriteAllText(dll, "");
        var settings = new PushCommand.Settings { PluginFile = dll };

        PushCommand.ResolveStandalonePluginFilePath(settings).Should().Be(Path.GetFullPath(dll));
    }

    [Fact]
    public void ResolveStandalonePluginFilePath_WithNonDll_ShouldThrow()
    {
        var file = Path.Combine(_root, "plugins.txt");
        File.WriteAllText(file, "");
        var settings = new PushCommand.Settings { PluginFile = file };

        var act = () => PushCommand.ResolveStandalonePluginFilePath(settings);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void ResolveStandalonePluginFilePath_WithNupkg_ShouldThrow()
    {
        var file = Path.Combine(_root, "plugins.nupkg");
        File.WriteAllText(file, "");
        var settings = new PushCommand.Settings { PluginFile = file };

        var act = () => PushCommand.ResolveStandalonePluginFilePath(settings);

        act.Should().Throw<FlowlineException>().WithMessage("*NuGet*");
    }

    [Fact]
    public void ResolveStandaloneWebResourcesPath_WithExistingFolder_ShouldReturnFullPath()
    {
        var folder = Path.Combine(_root, "dist");
        Directory.CreateDirectory(folder);
        var settings = new PushCommand.Settings { WebResources = folder };

        PushCommand.ResolveStandaloneWebResourcesPath(settings).Should().Be(Path.GetFullPath(folder));
    }

    [Fact]
    public void EnsureBuiltWebResources_WithFiles_ShouldNotThrow()
    {
        var dist = Path.Combine(_root, "dist");
        Directory.CreateDirectory(dist);
        File.WriteAllText(Path.Combine(dist, "account.js"), "");

        var act = () => PushCommand.EnsureBuiltWebResources(dist);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureBuiltWebResources_WithNestedFiles_ShouldNotThrow()
    {
        var dist = Path.Combine(_root, "dist");
        var sub = Path.Combine(dist, "forms");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "form.js"), "");

        var act = () => PushCommand.EnsureBuiltWebResources(dist);

        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureBuiltWebResources_WithEmptyFolder_ShouldThrow()
    {
        var dist = Path.Combine(_root, "dist");
        Directory.CreateDirectory(dist);

        var act = () => PushCommand.EnsureBuiltWebResources(dist);

        act.Should().Throw<FlowlineException>();
    }

    [Fact]
    public void EnsureBuiltWebResources_WithMissingFolder_ShouldThrow()
    {
        var dist = Path.Combine(_root, "dist");

        var act = () => PushCommand.EnsureBuiltWebResources(dist);

        act.Should().Throw<FlowlineException>();
    }
}
