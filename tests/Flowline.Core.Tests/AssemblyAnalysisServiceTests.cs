using System.Runtime.InteropServices;
using Microsoft.Xrm.Sdk;
using Flowline.Attributes;
using Flowline.Core.Models;
using Flowline.Core.Services;

namespace Flowline.Core.Tests;

public class AssemblyAnalysisServiceTests
{
    private static string DllPath => typeof(AssemblyAnalysisServiceTests).Assembly.Location;

    private static PluginAssemblyMetadata Analyze() =>
        new AssemblyAnalysisService().Analyze(DllPath, IsolationMode.Sandbox);

    private static PluginTypeMetadata GetPlugin(PluginAssemblyMetadata meta, string name) =>
        meta.Plugins.Single(p => p.Name == name);

    [Fact]
    public void Analyze_ReturnsAssemblyMetadata()
    {
        var metadata = Analyze();

        Assert.Equal("Flowline.Core.Tests", metadata.Name);
        Assert.Equal(IsolationMode.Sandbox, metadata.IsolationMode);
        Assert.NotNull(metadata.Plugins);
    }

    [Fact]
    public void Analyze_NonPluginClass_NotDetected()
    {
        var metadata = Analyze();

        Assert.DoesNotContain(metadata.Plugins, p => p.Name == nameof(AssemblyAnalysisServiceTests));
    }

    [Fact]
    public void Analyze_AbstractPlugin_NotDetected()
    {
        var metadata = Analyze();

        Assert.DoesNotContain(metadata.Plugins, p => p.Name == nameof(MockAbstractPlugin));
    }

    [Fact]
    public void Analyze_PluginWithoutEntityAttribute_HasNoSteps()
    {
        var plugin = GetPlugin(Analyze(), nameof(MockNoEntityPlugin));

        Assert.Empty(plugin.Steps);
    }

    [Fact]
    public void Analyze_BasicPlugin_DetectsStep()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockPreCreatePlugin)).Steps);

        Assert.Equal("account", step.EntityName);
        Assert.Equal("Create", step.Message);
        Assert.Equal((int)ProcessingStage.PreOperation, step.Stage);
        Assert.Equal((int)ProcessingMode.Synchronous, step.Mode);
        Assert.Equal(1, step.Order);
        Assert.Null(step.FilteringAttributes);
        Assert.Empty(step.Images);
    }

    [Fact]
    public void Analyze_PostPlugin_DetectsPostOperationStage()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockPostUpdatePlugin)).Steps);

        Assert.Equal((int)ProcessingStage.PostOperation, step.Stage);
        Assert.Equal("Update", step.Message);
        Assert.Equal("contact", step.EntityName);
    }

    [Fact]
    public void Analyze_AsyncPlugin_DetectsAsynchronousMode()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockPostUpdateAsyncPlugin)).Steps);

        Assert.Equal((int)ProcessingMode.Asynchronous, step.Mode);
        Assert.Equal((int)ProcessingStage.PostOperation, step.Stage);
    }

    [Fact]
    public void Analyze_ValidatePlugin_DetectsPreValidationStage()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockValidateCreatePlugin)).Steps);

        Assert.Equal((int)ProcessingStage.PreValidation, step.Stage);
        Assert.Equal("Create", step.Message);
    }

    [Fact]
    public void Analyze_ValidationSuffixPlugin_DetectsPreValidationStage()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockValidationCreatePlugin)).Steps);

        Assert.Equal((int)ProcessingStage.PreValidation, step.Stage);
    }

    [Fact]
    public void Analyze_PluginWithFilter_DetectsFilteringAttributes()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockPreUpdatePlugin)).Steps);

        Assert.Equal("name,telephone1", step.FilteringAttributes);
    }

    [Fact]
    public void Analyze_PluginWithImages_DetectsPreAndPostImages()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockPostDeletePlugin)).Steps);

        Assert.Equal(2, step.Images.Count);

        var pre = step.Images.Single(i => i.ImageType == (int)ImageType.PreImage);
        Assert.Equal("preimage", pre.Alias);
        Assert.Equal("Pre Image", pre.Name);
        Assert.Null(pre.Attributes);

        var post = step.Images.Single(i => i.ImageType == (int)ImageType.PostImage);
        Assert.Equal("postimage", post.Alias);
        Assert.Equal("name,telephone1", post.Attributes);
    }

    [Fact]
    public void Analyze_PluginWithCustomImageAlias_UsesProvidedAlias()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockPostAssignPlugin)).Steps);

        var img = Assert.Single(step.Images);
        Assert.Equal("before", img.Alias);
        Assert.Equal((int)ImageType.PreImage, img.ImageType);
    }

    [Fact]
    public void Analyze_PluginWithOrderAndConfig_DetectsNamedProperties()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockPreRetrievePlugin)).Steps);

        Assert.Equal(2, step.Order);
        Assert.Equal("{\"key\":\"value\"}", step.Configuration);
    }

    [Fact]
    public void Analyze_PluginWithNoStageKeyword_HasNoSteps()
    {
        var plugin = GetPlugin(Analyze(), nameof(MockNoStagePlugin));

        Assert.Empty(plugin.Steps);
    }

    [Fact]
    public void TryParseClassName_PreCreate_ParsesCorrectly()
    {
        Assert.True(AssemblyAnalysisService.TryParseClassName("AccountPreCreatePlugin", out var msg, out var stage, out var mode));
        Assert.Equal("Create", msg);
        Assert.Equal((int)ProcessingStage.PreOperation, stage);
        Assert.Equal((int)ProcessingMode.Synchronous, mode);
    }

    [Fact]
    public void TryParseClassName_PostUpdateAsync_ParsesCorrectly()
    {
        Assert.True(AssemblyAnalysisService.TryParseClassName("InvoicePostUpdateAsyncPlugin", out var msg, out var stage, out var mode));
        Assert.Equal("Update", msg);
        Assert.Equal((int)ProcessingStage.PostOperation, stage);
        Assert.Equal((int)ProcessingMode.Asynchronous, mode);
    }

    [Fact]
    public void TryParseClassName_ValidationCreate_ParsesPreValidationStage()
    {
        Assert.True(AssemblyAnalysisService.TryParseClassName("AccountValidationCreatePlugin", out _, out var stage, out _));
        Assert.Equal((int)ProcessingStage.PreValidation, stage);
    }

    [Fact]
    public void TryParseClassName_NoStageKeyword_ReturnsFalse()
    {
        Assert.False(AssemblyAnalysisService.TryParseClassName("AccountCreatePlugin", out _, out _, out _));
    }

    [Fact]
    public void TryParseClassName_NoMessageKeyword_ReturnsFalse()
    {
        Assert.False(AssemblyAnalysisService.TryParseClassName("AccountPostPlugin", out _, out _, out _));
    }

    [Fact]
    public void TryParseClassName_RetrieveMultiple_MatchesBeforeRetrieve()
    {
        Assert.True(AssemblyAnalysisService.TryParseClassName("AccountPreRetrieveMultiplePlugin", out var msg, out _, out _));
        Assert.Equal("RetrieveMultiple", msg);
    }
}

// -- Mock plugin types used by the integration tests above --

[Entity("account")]
public class MockPreCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Entity("contact")]
public class MockPostUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Entity("lead")]
public class MockPostUpdateAsyncPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Entity("account")]
public class MockValidateCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Entity("account")]
public class MockValidationCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Entity("account")]
[Filter("name", "telephone1")]
public class MockPreUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Entity("account")]
[Image(ImageType.PreImage)]
[Image(ImageType.PostImage, "name", "telephone1")]
public class MockPostDeletePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Entity("account")]
[Image("before", ImageType.PreImage)]
public class MockPostAssignPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Entity("account", Order = 2, Configuration = "{\"key\":\"value\"}")]
public class MockPreRetrievePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// No [Entity] — detected as plugin type but produces no step
public class MockNoEntityPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// No stage keyword in class name — has [Entity] but can't parse a step
[Entity("account")]
public class MockNoStagePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// Abstract — excluded from detection entirely
[Entity("account")]
public abstract class MockAbstractPlugin : IPlugin
{
    public abstract void Execute(IServiceProvider serviceProvider);
}
