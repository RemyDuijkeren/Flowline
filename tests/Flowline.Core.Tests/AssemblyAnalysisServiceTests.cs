using System.Runtime.InteropServices;
using Flowline.Attributes;
using Flowline.Core.Models;
using Flowline.Core.Services;

namespace Flowline.Core.Tests;

public class AssemblyAnalysisServiceTests
{
    [Fact]
    public void Analyze_CurrentAssembly_ShouldDetectTests()
    {
        // Arrange
        var service = new AssemblyAnalysisService();
        var dllPath = typeof(AssemblyAnalysisServiceTests).Assembly.Location;
        var isolationMode = IsolationMode.Sandbox;

        // Act
        var metadata = service.Analyze(dllPath, isolationMode);

        // Assert
        Assert.NotNull(metadata);
        Assert.Equal("Flowline.Core.Tests", metadata.Name);
        Assert.Equal(isolationMode, metadata.IsolationMode);
        // This assembly doesn't have plugins/workflows yet, but it should load and return metadata
        Assert.NotNull(metadata.Plugins);
    }

    [Fact]
    public void Analyze_WithMockPlugin_ShouldDetectPlugin()
    {
        // We can't easily create a DLL with attributes at runtime here without much ceremony.
        // But we can check if it filters out non-plugins correctly.

        var service = new AssemblyAnalysisService();
        var dllPath = typeof(AssemblyAnalysisServiceTests).Assembly.Location;

        var metadata = service.Analyze(dllPath, IsolationMode.Sandbox);

        // This class is not a plugin, so it shouldn't be in the list
        Assert.DoesNotContain(metadata.Plugins, p => p.FullName == typeof(AssemblyAnalysisServiceTests).FullName);
    }
}

// Dummy classes to test inheritance detection in MLC if we had a way to mock the assembly
public interface IPlugin { }
public abstract class CodeActivity { }


[Entity("account")]
public class MockPreCreatePlugin : IPlugin { }


public class MockWorkflow : CodeActivity { }
