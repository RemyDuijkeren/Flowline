using System.Runtime.InteropServices;
using Microsoft.Xrm.Sdk;
using Flowline.Attributes;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Core;

namespace Flowline.Core.Tests;

public class AssemblyAnalysisServiceTests
{
    private static string DllPath => typeof(AssemblyAnalysisServiceTests).Assembly.Location;

    private static PluginAssemblyMetadata Analyze() =>
        new AssemblyAnalysisService(new NullFlowlineOutput()).Analyze(DllPath);

    private static PluginTypeMetadata GetPlugin(PluginAssemblyMetadata meta, string name) =>
        meta.Plugins.Single(p => p.Name == name);

    [Fact]
    public void Analyze_ReturnsAssemblyMetadata()
    {
        var metadata = Analyze();

        Assert.Equal("Flowline.Core.Tests", metadata.Name);
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
    public void Analyze_PluginWithWhitespaceInFilter_NormalizesFilteringAttributes()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockWithWhitespacePreUpdatePlugin)).Steps);

        Assert.Equal("name,firstname", step.FilteringAttributes);
    }

    [Fact]
    public void Analyze_PluginWithImages_DetectsPreAndPostImages()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockBothImagesPostUpdatePlugin)).Steps);

        Assert.Equal(2, step.Images.Count);

        var pre = step.Images.Single(i => i.ImageType == (int)ImageType.PreImage);
        Assert.Equal("preimage", pre.Alias);
        Assert.Equal("Pre Image", pre.Name);
        Assert.Equal("name", pre.Attributes);

        var post = step.Images.Single(i => i.ImageType == (int)ImageType.PostImage);
        Assert.Equal("postimage", post.Alias);
        Assert.Equal("name,telephone1", post.Attributes);
    }

    [Fact]
    public void Analyze_PluginWithImageAndNoFilter_AddsPerformanceWarning()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockPostDeletePlugin)).Steps);

        Assert.Single(step.Warnings);
        Assert.Contains("PreImage", step.Warnings[0]);
        Assert.Contains("no attribute filter", step.Warnings[0]);
        Assert.Contains("https://learn.microsoft.com", step.Warnings[0]);
    }

    [Fact]
    public void ValidateExecutionMode_AsyncOnPreOperation_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AssemblyAnalysisService.ValidateExecutionMode("MockPreUpdateAsyncPlugin",
                (int)ProcessingStage.PreOperation, (int)ProcessingMode.Asynchronous));
        Assert.Contains("Asynchronous", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateExecutionMode_AsyncOnPreValidation_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AssemblyAnalysisService.ValidateExecutionMode("MockValidationUpdateAsyncPlugin",
                (int)ProcessingStage.PreValidation, (int)ProcessingMode.Asynchronous));
        Assert.Contains("Asynchronous", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateExecutionMode_AsyncOnPostOperation_DoesNotThrow()
    {
        AssemblyAnalysisService.ValidateExecutionMode("MockPostUpdateAsyncPlugin",
            (int)ProcessingStage.PostOperation, (int)ProcessingMode.Asynchronous);
    }

    [Fact]
    public void ValidateEntityName_EmptyName_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AssemblyAnalysisService.ValidateEntityName("MockPreCreatePlugin", ""));
        Assert.Contains("[Entity]", ex.Message);
    }

    [Fact]
    public void ValidateEntityName_WhitespaceName_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AssemblyAnalysisService.ValidateEntityName("MockPreCreatePlugin", "  "));
        Assert.Contains("[Entity]", ex.Message);
    }

    [Fact]
    public void ValidateEntityName_ValidName_DoesNotThrow()
    {
        AssemblyAnalysisService.ValidateEntityName("MockPreCreatePlugin", "account");
    }

    [Fact]
    public void ValidateCustomApiAttributesOnStep_WithAttributes_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AssemblyAnalysisService.ValidateCustomApiAttributesOnStep("MockPreCreatePlugin", true));
        Assert.Contains("[Input]", ex.Message);
        Assert.Contains("[Output]", ex.Message);
    }

    [Fact]
    public void ValidateCustomApiAttributesOnStep_WithoutAttributes_DoesNotThrow()
    {
        AssemblyAnalysisService.ValidateCustomApiAttributesOnStep("MockPreCreatePlugin", false);
    }

    [Fact]
    public void ValidateSecondaryEntity_OnNonAssociateMessage_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AssemblyAnalysisService.ValidateSecondaryEntity("MockPreCreatePlugin", "Create", "account"));
        Assert.Contains("[SecondaryEntity]", ex.Message);
        Assert.Contains("Create", ex.Message);
    }

    [Fact]
    public void ValidateSecondaryEntity_OnAssociate_DoesNotThrow()
    {
        AssemblyAnalysisService.ValidateSecondaryEntity("MockPreAssociatePlugin", "Associate", "account");
    }

    [Fact]
    public void ValidateSecondaryEntity_NullOnAnyMessage_DoesNotThrow()
    {
        AssemblyAnalysisService.ValidateSecondaryEntity("MockPreCreatePlugin", "Create", null);
    }

    [Fact]
    public void Analyze_UpdatePluginWithoutFilter_AddsWarning()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockNoFilterPostUpdatePlugin)).Steps);

        Assert.Single(step.Warnings);
        Assert.Contains("[Filter]", step.Warnings[0]);
        Assert.Contains("Update", step.Warnings[0]);
    }

    [Fact]
    public void Analyze_AssociatePluginWithSecondaryEntity_DetectsSecondaryEntity()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockPreAssociatePlugin)).Steps);

        Assert.Equal("account", step.SecondaryEntity);
        Assert.Empty(step.Warnings);
    }

    [Fact]
    public void Analyze_AssociatePluginWithoutSecondaryEntity_AddsWarning()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockNoSecondaryPreAssociatePlugin)).Steps);

        Assert.Single(step.Warnings);
        Assert.Contains("[SecondaryEntity]", step.Warnings[0]);
    }

    [Fact]
    public void ValidateFilter_FilterOnCreate_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AssemblyAnalysisService.ValidateFilter("MockPreCreatePlugin", "Create", "name,telephone1"));
        Assert.Contains("[Filter]", ex.Message);
        Assert.Contains("Create", ex.Message);
    }

    [Fact]
    public void ValidateFilter_FilterOnDelete_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AssemblyAnalysisService.ValidateFilter("MockPreDeletePlugin", "Delete", "name"));
        Assert.Contains("[Filter]", ex.Message);
        Assert.Contains("Delete", ex.Message);
    }

    [Fact]
    public void ValidateFilter_FilterOnUpdate_DoesNotThrow()
    {
        AssemblyAnalysisService.ValidateFilter("MockPreUpdatePlugin", "Update", "name,telephone1");
    }

    [Fact]
    public void ValidateFilter_NullFilter_DoesNotThrow()
    {
        AssemblyAnalysisService.ValidateFilter("MockPreCreatePlugin", "Create", null);
    }

    [Fact]
    public void ValidateImages_PreImageOnCreate_Throws()
    {
        var images = new List<PluginImageMetadata> { new("Pre Image", "preimage", (int)ImageType.PreImage, "name") };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AssemblyAnalysisService.ValidateImages("MockPlugin", "Create", (int)ProcessingStage.PostOperation, images));
        Assert.Contains("[PreImage]", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateImages_PostImageOnDelete_Throws()
    {
        var images = new List<PluginImageMetadata> { new("Post Image", "postimage", (int)ImageType.PostImage, "name") };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AssemblyAnalysisService.ValidateImages("MockPlugin", "Delete", (int)ProcessingStage.PostOperation, images));
        Assert.Contains("[PostImage]", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateImages_PostImageOnPreOperation_Throws()
    {
        var images = new List<PluginImageMetadata> { new("Post Image", "postimage", (int)ImageType.PostImage, "name") };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AssemblyAnalysisService.ValidateImages("MockPlugin", "Update", (int)ProcessingStage.PreOperation, images));
        Assert.Contains("[PostImage]", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateImages_PostImageOnPreValidation_Throws()
    {
        var images = new List<PluginImageMetadata> { new("Post Image", "postimage", (int)ImageType.PostImage, "name") };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AssemblyAnalysisService.ValidateImages("MockPlugin", "Update", (int)ProcessingStage.PreValidation, images));
        Assert.Contains("[PostImage]", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateImages_ImageOnUnsupportedMessage_Throws()
    {
        var images = new List<PluginImageMetadata> { new("Pre Image", "preimage", (int)ImageType.PreImage, "name") };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            AssemblyAnalysisService.ValidateImages("MockPlugin", "Retrieve", (int)ProcessingStage.PostOperation, images));
        Assert.Contains("Retrieve", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
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
[Filter(" name ", " firstname ")]
public class MockWithWhitespacePreUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Entity("account")]
[PreImage]
public class MockPostDeletePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Entity("account")]
[PreImage("name")]
[PostImage("name", "telephone1")]
public class MockBothImagesPostUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Entity("account")]
[PreImage(Alias = "before")]
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

[Entity("account")]
public class MockNoFilterPostUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Entity("contact")]
[SecondaryEntity("account")]
public class MockPreAssociatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Entity("contact")]
public class MockNoSecondaryPreAssociatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}
