using System.Runtime.InteropServices;
using Microsoft.Xrm.Sdk;
using Flowline.Attributes;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Core;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests;

public class PluginAssemblyReaderTests
{
    private static string DllPath => typeof(PluginAssemblyReaderTests).Assembly.Location;

    private static PluginAssemblyMetadata Analyze() =>
        new PluginAssemblyReader(new TestConsole(), isVerbose: false).Analyze(DllPath);

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

        Assert.DoesNotContain(metadata.Plugins, p => p.Name == nameof(PluginAssemblyReaderTests));
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

        Assert.Equal("account", step.TableName);
        Assert.Equal("Create", step.Message);
        Assert.Equal((int)ProcessingStage.PreOperation, step.Stage);
        Assert.Equal((int)ProcessingMode.Synchronous, step.Mode);
        Assert.Equal(1, step.Order);
        Assert.Null(step.FilteringColumns);
        Assert.Empty(step.Images);
    }

    [Fact]
    public void Analyze_PostPlugin_DetectsPostOperationStage()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockPostUpdatePlugin)).Steps);

        Assert.Equal((int)ProcessingStage.PostOperation, step.Stage);
        Assert.Equal("Update", step.Message);
        Assert.Equal("contact", step.TableName);
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

        Assert.Equal("name,telephone1", step.FilteringColumns);
    }

    [Fact]
    public void Analyze_PluginWithWhitespaceInFilter_NormalizesFilteringAttributes()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockWithWhitespacePreUpdatePlugin)).Steps);

        Assert.Equal("name,firstname", step.FilteringColumns);
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
        Assert.Contains("no column filter", step.Warnings[0]);
        Assert.Contains("https://learn.microsoft.com", step.Warnings[0]);
    }

    [Fact]
    public void ValidateExecutionMode_AsyncOnPreOperation_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateExecutionMode("MockPreUpdateAsyncPlugin",
                (int)ProcessingStage.PreOperation, (int)ProcessingMode.Asynchronous));
        Assert.Contains("Asynchronous", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateExecutionMode_AsyncOnPreValidation_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateExecutionMode("MockValidationUpdateAsyncPlugin",
                (int)ProcessingStage.PreValidation, (int)ProcessingMode.Asynchronous));
        Assert.Contains("Asynchronous", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateExecutionMode_AsyncOnPostOperation_DoesNotThrow()
    {
        PluginAssemblyReader.ValidateExecutionMode("MockPostUpdateAsyncPlugin",
            (int)ProcessingStage.PostOperation, (int)ProcessingMode.Asynchronous);
    }

    [Fact]
    public void ValidateLogicalName_EmptyString_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateLogicalName("MockPreCreatePlugin", ""));
        Assert.Contains("[Step]", ex.Message);
        Assert.Contains("none", ex.Message);
    }

    [Fact]
    public void ValidateLogicalName_WhitespaceString_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateLogicalName("MockPreCreatePlugin", "  "));
        Assert.Contains("[Step]", ex.Message);
    }

    [Fact]
    public void ValidateLogicalName_Null_DoesNotThrow()
    {
        PluginAssemblyReader.ValidateLogicalName("MockPreCreatePlugin", null);
    }

    [Fact]
    public void ValidateLogicalName_ValidName_DoesNotThrow()
    {
        PluginAssemblyReader.ValidateLogicalName("MockPreCreatePlugin", "account");
    }

    [Fact]
    public void ValidateSecondaryLogicalName_EmptyString_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateSecondaryLogicalName("MockPreAssociatePlugin", ""));
        Assert.Contains("SecondaryTable", ex.Message);
        Assert.Contains("none", ex.Message);
    }

    [Fact]
    public void ValidateSecondaryLogicalName_Null_DoesNotThrow()
    {
        PluginAssemblyReader.ValidateSecondaryLogicalName("MockPreAssociatePlugin", null);
    }

    [Fact]
    public void ValidateCustomApiAttributesOnStep_WithAttributes_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateCustomApiAttributesOnStep("MockPreCreatePlugin", true));
        Assert.Contains("[Input]", ex.Message);
        Assert.Contains("[Output]", ex.Message);
    }

    [Fact]
    public void ValidateCustomApiAttributesOnStep_WithoutAttributes_DoesNotThrow()
    {
        PluginAssemblyReader.ValidateCustomApiAttributesOnStep("MockPreCreatePlugin", false);
    }

    [Fact]
    public void ValidateSecondaryTable_OnNonAssociateMessage_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateSecondaryTable("MockPreCreatePlugin", "Create", "account"));
        Assert.Contains("SecondaryTable", ex.Message);
        Assert.Contains("Create", ex.Message);
    }

    [Fact]
    public void ValidateSecondaryTable_OnAssociate_DoesNotThrow()
    {
        PluginAssemblyReader.ValidateSecondaryTable("MockPreAssociatePlugin", "Associate", "account");
    }

    [Fact]
    public void ValidateSecondaryTable_Null_DoesNotThrow()
    {
        PluginAssemblyReader.ValidateSecondaryTable("MockPreCreatePlugin", "Create", null);
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
    public void Analyze_AssociatePluginWithSecondaryTable_DetectsSecondaryTable()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockPreAssociatePlugin)).Steps);

        Assert.Equal("account", step.SecondaryTable);
        Assert.Empty(step.Warnings);
    }

    [Fact]
    public void Analyze_AssociatePluginWithoutSecondaryTable_AddsWarning()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockNoSecondaryPreAssociatePlugin)).Steps);

        Assert.Single(step.Warnings);
        Assert.Contains("secondary table", step.Warnings[0]);
    }

    [Fact]
    public void Analyze_StepWithNoEntity_ProducesStepWithWarning()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockNoEntityStepPreCreatePlugin)).Steps);

        Assert.Null(step.TableName);
        Assert.Single(step.Warnings);
        Assert.Contains("[Step]", step.Warnings[0]);
        Assert.Contains("[Step(\"none\")]", step.Warnings[0]);
    }

    [Fact]
    public void Analyze_StepWithNoneEntity_ProducesStepWithNoEntityWarning()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockNoneEntityPreCreatePlugin)).Steps);

        Assert.Equal("none", step.TableName);
        Assert.Empty(step.Warnings);
    }

    [Fact]
    public void ValidateFilter_FilterOnCreate_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateFilter("MockPreCreatePlugin", "Create", "name,telephone1"));
        Assert.Contains("[Filter]", ex.Message);
        Assert.Contains("Create", ex.Message);
    }

    [Fact]
    public void ValidateFilter_FilterOnDelete_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateFilter("MockPreDeletePlugin", "Delete", "name"));
        Assert.Contains("[Filter]", ex.Message);
        Assert.Contains("Delete", ex.Message);
    }

    [Fact]
    public void ValidateFilter_FilterOnUpdate_DoesNotThrow()
    {
        PluginAssemblyReader.ValidateFilter("MockPreUpdatePlugin", "Update", "name,telephone1");
    }

    [Fact]
    public void ValidateFilter_NullFilter_DoesNotThrow()
    {
        PluginAssemblyReader.ValidateFilter("MockPreCreatePlugin", "Create", null);
    }

    [Fact]
    public void ValidateImages_PreImageOnCreate_Throws()
    {
        var images = new List<PluginImageMetadata> { new("Pre Image", "preimage", (int)ImageType.PreImage, "name") };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateImages("MockPlugin", "Create", (int)ProcessingStage.PostOperation, images));
        Assert.Contains("[PreImage]", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateImages_PostImageOnDelete_Throws()
    {
        var images = new List<PluginImageMetadata> { new("Post Image", "postimage", (int)ImageType.PostImage, "name") };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateImages("MockPlugin", "Delete", (int)ProcessingStage.PostOperation, images));
        Assert.Contains("[PostImage]", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateImages_PostImageOnPreOperation_Throws()
    {
        var images = new List<PluginImageMetadata> { new("Post Image", "postimage", (int)ImageType.PostImage, "name") };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateImages("MockPlugin", "Update", (int)ProcessingStage.PreOperation, images));
        Assert.Contains("[PostImage]", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateImages_PostImageOnPreValidation_Throws()
    {
        var images = new List<PluginImageMetadata> { new("Post Image", "postimage", (int)ImageType.PostImage, "name") };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateImages("MockPlugin", "Update", (int)ProcessingStage.PreValidation, images));
        Assert.Contains("[PostImage]", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateImages_ImageOnUnsupportedMessage_Throws()
    {
        var images = new List<PluginImageMetadata> { new("Pre Image", "preimage", (int)ImageType.PreImage, "name") };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateImages("MockPlugin", "Retrieve", (int)ProcessingStage.PostOperation, images));
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
    public void Analyze_PluginWithRunAs_ParsesGuid()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockRunAsPostCreatePlugin)).Steps);

        Assert.Equal(new Guid("3b36b50c-03e5-4b5f-8882-aabbccddeeff"), step.RunAs);
    }

    [Fact]
    public void Analyze_PluginWithoutStepAttribute_HasNoSteps()
    {
        var plugin = GetPlugin(Analyze(), nameof(MockNoStagePlugin));

        Assert.Empty(plugin.Steps);
    }

    [Fact]
    public void ParseStepClassNameOrThrow_NoStageKeyword_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ParseStepClassNameOrThrow("AccountCreatePlugin", out _, out _, out _));

        Assert.Contains("[Step]", ex.Message);
        Assert.Contains("no stage", ex.Message);
        Assert.Contains("AccountPreCreatePlugin", ex.Message);
        Assert.Contains(nameof(Message), ex.Message);
    }

    [Fact]
    public void ParseStepClassNameOrThrow_NoMessageKeyword_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ParseStepClassNameOrThrow("AccountPostPlugin", out _, out _, out _));

        Assert.Contains("[Step]", ex.Message);
        Assert.Contains("no message", ex.Message);
        Assert.Contains("AccountPostUpdatePlugin", ex.Message);
    }

    [Fact]
    public void ParseStepClassNameOrThrow_NoStageOrMessageKeyword_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ParseStepClassNameOrThrow("AccountPlugin", out _, out _, out _));

        Assert.Contains("[Step]", ex.Message);
        Assert.Contains("stage or message", ex.Message);
    }

    [Fact]
    public void ValidateStepUsage_StepOnNonPlugin_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateStepUsage("NotAPlugin", hasStepAttribute: true, isPlugin: false));

        Assert.Contains("[Step]", ex.Message);
        Assert.Contains("IPlugin", ex.Message);
    }

    [Fact]
    public void ValidateStepUsage_StepOnPlugin_DoesNotThrow()
    {
        PluginAssemblyReader.ValidateStepUsage("AccountPreCreatePlugin", hasStepAttribute: true, isPlugin: true);
    }

    [Fact]
    public void TryParseClassName_PreCreate_ParsesCorrectly()
    {
        Assert.True(PluginAssemblyReader.TryParseClassName("AccountPreCreatePlugin", out var msg, out var stage, out var mode));
        Assert.Equal("Create", msg);
        Assert.Equal((int)ProcessingStage.PreOperation, stage);
        Assert.Equal((int)ProcessingMode.Synchronous, mode);
    }

    [Fact]
    public void TryParseClassName_PostUpdateAsync_ParsesCorrectly()
    {
        Assert.True(PluginAssemblyReader.TryParseClassName("InvoicePostUpdateAsyncPlugin", out var msg, out var stage, out var mode));
        Assert.Equal("Update", msg);
        Assert.Equal((int)ProcessingStage.PostOperation, stage);
        Assert.Equal((int)ProcessingMode.Asynchronous, mode);
    }

    [Fact]
    public void TryParseClassName_ValidationCreate_ParsesPreValidationStage()
    {
        Assert.True(PluginAssemblyReader.TryParseClassName("AccountValidationCreatePlugin", out _, out var stage, out _));
        Assert.Equal((int)ProcessingStage.PreValidation, stage);
    }

    [Fact]
    public void TryParseClassName_NoStageKeyword_ReturnsFalse()
    {
        Assert.False(PluginAssemblyReader.TryParseClassName("AccountCreatePlugin", out _, out _, out _));
    }

    [Fact]
    public void TryParseClassName_NoMessageKeyword_ReturnsFalse()
    {
        Assert.False(PluginAssemblyReader.TryParseClassName("AccountPostPlugin", out _, out _, out _));
    }

    [Fact]
    public void TryParseClassName_RetrieveMultiple_MatchesBeforeRetrieve()
    {
        Assert.True(PluginAssemblyReader.TryParseClassName("AccountPreRetrieveMultiplePlugin", out var msg, out _, out _));
        Assert.Equal("RetrieveMultiple", msg);
    }

    // ---- Bulk operation message tests ----

    [Fact]
    public void ValidateFilter_FilterOnUpdateMultiple_DoesNotThrow()
    {
        PluginAssemblyReader.ValidateFilter("MockPostUpdateMultiplePlugin", "UpdateMultiple", "name,telephone1");
    }

    [Fact]
    public void ValidateFilter_FilterOnDeleteMultiple_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateFilter("MockPreDeleteMultiplePlugin", "DeleteMultiple", "name"));
        Assert.Contains("[Filter]", ex.Message);
        Assert.Contains("DeleteMultiple", ex.Message);
    }

    [Fact]
    public void Analyze_UpdateMultiplePluginWithoutFilter_AddsWarning()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockNoFilterPostUpdateMultiplePlugin)).Steps);

        Assert.Single(step.Warnings);
        Assert.Contains("[Filter]", step.Warnings[0]);
    }

    [Fact]
    public void Analyze_DeleteMultiplePlugin_AddsElasticTableWarning()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockPreDeleteMultiplePlugin)).Steps);

        Assert.Single(step.Warnings);
        Assert.Contains("elastic", step.Warnings[0]);
        Assert.Contains("DeleteMultiple", step.Warnings[0]);
    }

    [Fact]
    public void ValidateImages_ImageOnCreateMultiple_DoesNotThrow()
    {
        var images = new List<PluginImageMetadata> { new("Post Image", "postimage", (int)ImageType.PostImage, "name") };
        PluginAssemblyReader.ValidateImages("MockPostCreateMultiplePlugin", "CreateMultiple", (int)ProcessingStage.PostOperation, images);
    }

    [Fact]
    public void ValidateImages_ImageOnUpdateMultiple_DoesNotThrow()
    {
        var images = new List<PluginImageMetadata> { new("Pre Image", "preimage", (int)ImageType.PreImage, "name") };
        PluginAssemblyReader.ValidateImages("MockPostUpdateMultiplePlugin", "UpdateMultiple", (int)ProcessingStage.PostOperation, images);
    }

    [Fact]
    public void TryParseClassName_PostCreateMultiple_MatchesBeforeCreate()
    {
        Assert.True(PluginAssemblyReader.TryParseClassName("AccountPostCreateMultiplePlugin", out var msg, out _, out _));
        Assert.Equal("CreateMultiple", msg);
    }

    [Fact]
    public void TryParseClassName_PostUpdateMultiple_MatchesBeforeUpdate()
    {
        Assert.True(PluginAssemblyReader.TryParseClassName("AccountPostUpdateMultiplePlugin", out var msg, out _, out _));
        Assert.Equal("UpdateMultiple", msg);
    }

    [Fact]
    public void TryParseClassName_PostUpsert_ParsesCorrectly()
    {
        Assert.True(PluginAssemblyReader.TryParseClassName("AccountPostUpsertPlugin", out var msg, out var stage, out _));
        Assert.Equal("Upsert", msg);
        Assert.Equal((int)ProcessingStage.PostOperation, stage);
    }

    [Fact]
    public void TryParseClassName_PostUpsertMultiple_MatchesBeforeUpsert()
    {
        Assert.True(PluginAssemblyReader.TryParseClassName("AccountPostUpsertMultiplePlugin", out var msg, out _, out _));
        Assert.Equal("UpsertMultiple", msg);
    }

    [Fact]
    public void TryParseClassName_PreDeleteMultiple_MatchesBeforeDelete()
    {
        Assert.True(PluginAssemblyReader.TryParseClassName("AccountPreDeleteMultiplePlugin", out var msg, out _, out _));
        Assert.Equal("DeleteMultiple", msg);
    }

    // ---- [Handles] tests ----

    [Fact]
    public void Analyze_HandlesEnumOverload_DetectsStep()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockHandlesUpdatePrePlugin)).Steps);

        Assert.Equal("Update", step.Message);
        Assert.Equal((int)ProcessingStage.PreOperation, step.Stage);
        Assert.Equal((int)ProcessingMode.Synchronous, step.Mode);
        Assert.Equal("account", step.TableName);
    }

    [Fact]
    public void Analyze_HandlesPostOperationAsync_DetectsAsyncMode()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockHandlesAsyncPostCreatePlugin)).Steps);

        Assert.Equal("Create", step.Message);
        Assert.Equal((int)ProcessingStage.PostOperation, step.Stage);
        Assert.Equal((int)ProcessingMode.Asynchronous, step.Mode);
        Assert.Empty(step.Warnings);
    }

    [Fact]
    public void Analyze_HandlesStringOverload_DetectsCustomApiMessage()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockHandlesCustomApiPlugin)).Steps);

        Assert.Equal("mynamespace_MyAction", step.Message);
        Assert.Equal((int)ProcessingStage.PostOperation, step.Stage);
        Assert.Equal((int)ProcessingMode.Synchronous, step.Mode);
    }

    [Fact]
    public void Analyze_HandlesOnConventionClassName_AddsRedundancyWarning()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockHandlesRedundantAccountPreUpdatePlugin)).Steps);

        Assert.NotEmpty(step.Warnings);
        Assert.Contains("redundant", step.Warnings[^1]);
    }

    [Fact]
    public void Analyze_HandlesWithoutStep_ProducesNoStep()
    {
        var plugin = GetPlugin(Analyze(), nameof(MockHandlesWithoutStepPlugin));

        Assert.Empty(plugin.Steps);
    }

    [Fact]
    public void Analyze_HandlesWithSecondaryTable_DetectsSecondaryTable()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockHandlesAssociatePlugin)).Steps);

        Assert.Equal("Associate", step.Message);
        Assert.Equal("account", step.SecondaryTable);
        Assert.Empty(step.Warnings);
    }

    // ---- multi-[Handles] tests ----

    [Fact]
    public void Analyze_MultiHandles_DistinctMessages_ProducesTwoStepsWithoutStageSuffix()
    {
        var plugin = GetPlugin(Analyze(), nameof(MockMultiHandlesCreateAndUpdatePlugin));

        Assert.Equal(2, plugin.Steps.Count);
        var create = plugin.Steps.Single(s => s.Message == "Create");
        var update = plugin.Steps.Single(s => s.Message == "Update");
        Assert.DoesNotContain(" at ", create.Name);
        Assert.DoesNotContain(" at ", update.Name);
    }

    [Fact]
    public void Analyze_MultiHandles_SameMessageTwoStages_ProducesStageSuffix()
    {
        var plugin = GetPlugin(Analyze(), nameof(MockMultiHandlesUpdateTwoStagesPlugin));

        Assert.Equal(2, plugin.Steps.Count);
        var pre = plugin.Steps.Single(s => s.Stage == (int)ProcessingStage.PreOperation);
        var post = plugin.Steps.Single(s => s.Stage == (int)ProcessingStage.PostOperation);
        Assert.EndsWith(" at PreOperation", pre.Name);
        Assert.EndsWith(" at PostOperation", post.Name);
    }

    [Fact]
    public void Analyze_MultiHandles_ThreeHandlesOneMessageRepeats_AllStepsQualified()
    {
        var plugin = GetPlugin(Analyze(), nameof(MockMultiHandlesThreeHandlesPlugin));

        Assert.Equal(3, plugin.Steps.Count);
        Assert.All(plugin.Steps, s => Assert.Contains(" at ", s.Name));
    }

    [Fact]
    public void Analyze_MultiHandles_FilterOnlyAppliedToUpdateStep()
    {
        var plugin = GetPlugin(Analyze(), nameof(MockMultiHandlesFilterCreateUpdatePlugin));

        var create = plugin.Steps.Single(s => s.Message == "Create");
        var update = plugin.Steps.Single(s => s.Message == "Update");
        Assert.Null(create.FilteringColumns);
        Assert.Equal("name", update.FilteringColumns);
    }

    [Fact]
    public void Analyze_MultiHandles_PreImageOnlyAppliedToNonCreateStep()
    {
        var plugin = GetPlugin(Analyze(), nameof(MockMultiHandlesPreImagePlugin));

        var create = plugin.Steps.Single(s => s.Message == "Create");
        var update = plugin.Steps.Single(s => s.Message == "Update");
        Assert.Empty(create.Images);
        Assert.Single(update.Images);
    }

    [Fact]
    public void Analyze_MultiHandles_PostImageOnlyAppliedToNonDeletePostOpStep()
    {
        var plugin = GetPlugin(Analyze(), nameof(MockMultiHandlesPostImagePlugin));

        var delete = plugin.Steps.Single(s => s.Message == "Delete");
        var update = plugin.Steps.Single(s => s.Message == "Update");
        Assert.Empty(delete.Images);
        Assert.Single(update.Images);
    }

    [Fact]
    public void Analyze_MultiHandles_NudgeWarningPresentOnEachStep()
    {
        var plugin = GetPlugin(Analyze(), nameof(MockMultiHandlesCreateAndUpdatePlugin));

        Assert.All(plugin.Steps, s =>
            Assert.Contains(s.Warnings, w => w.Contains("multiple [[Handles]] detected")));
    }

    [Fact]
    public void Analyze_SingleHandles_NoNudgeWarning()
    {
        var step = Assert.Single(GetPlugin(Analyze(), nameof(MockHandlesUpdatePrePlugin)).Steps);

        Assert.DoesNotContain(step.Warnings, w => w.Contains("multiple [[Handles]] detected"));
    }

    [Fact]
    public void Analyze_MultiHandles_PostOperationAsyncSuffix_DistinguishesFromSync()
    {
        var plugin = GetPlugin(Analyze(), nameof(MockMultiHandlesUpdateSyncAndAsyncPlugin));

        Assert.Equal(2, plugin.Steps.Count);
        var sync = plugin.Steps.Single(s => s.Mode == (int)ProcessingMode.Synchronous);
        var async_ = plugin.Steps.Single(s => s.Mode == (int)ProcessingMode.Asynchronous);
        Assert.EndsWith(" at PostOperation", sync.Name);
        Assert.EndsWith(" at PostOperationAsync", async_.Name);
    }

    [Theory]
    [InlineData("MyPlugin", false, false)]
    [InlineData("MyPlugin", true, true)]
    public void ValidateMultiHandlesFilter_NoThrow(string name, bool hasFilter, bool anyCompatible)
    {
        PluginAssemblyReader.ValidateMultiHandlesFilter(name, hasFilter, anyCompatible);
    }

    [Fact]
    public void ValidateMultiHandlesFilter_FilterPresentNoCompatible_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateMultiHandlesFilter("MyPlugin", hasFilter: true, anyFilterCompatible: false));
        Assert.Contains("[Filter]", ex.Message);
        Assert.Contains("Update", ex.Message);
    }

    [Theory]
    [InlineData("MyPlugin", false, false)]
    [InlineData("MyPlugin", true, true)]
    public void ValidateMultiHandlesPreImage_NoThrow(string name, bool hasPreImage, bool anyCompatible)
    {
        PluginAssemblyReader.ValidateMultiHandlesPreImage(name, hasPreImage, anyCompatible);
    }

    [Fact]
    public void ValidateMultiHandlesPreImage_PreImagePresentNoCompatible_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateMultiHandlesPreImage("MyPlugin", hasPreImage: true, anyPreImageCompatible: false));
        Assert.Contains("[PreImage]", ex.Message);
    }

    [Theory]
    [InlineData("MyPlugin", false, false)]
    [InlineData("MyPlugin", true, true)]
    public void ValidateMultiHandlesPostImage_NoThrow(string name, bool hasPostImage, bool anyCompatible)
    {
        PluginAssemblyReader.ValidateMultiHandlesPostImage(name, hasPostImage, anyCompatible);
    }

    [Fact]
    public void ValidateMultiHandlesPostImage_PostImagePresentNoCompatible_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.ValidateMultiHandlesPostImage("MyPlugin", hasPostImage: true, anyPostImageCompatible: false));
        Assert.Contains("[PostImage]", ex.Message);
    }

    [Fact]
    public void TryBuildSteps_MultiHandles_FilterNoCompatibleMessage_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.TryBuildSteps(typeof(MockErrMultiHandlesFilterNoCompatiblePlugin)).ToList());
        Assert.Contains("[Filter]", ex.Message);
    }

    [Fact]
    public void TryBuildSteps_MultiHandles_PreImageNoCompatibleMessage_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.TryBuildSteps(typeof(MockErrMultiHandlesPreImageNoCompatiblePlugin)).ToList());
        Assert.Contains("[PreImage]", ex.Message);
    }

    [Fact]
    public void TryBuildSteps_MultiHandles_PostImageNoCompatibleMessage_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginAssemblyReader.TryBuildSteps(typeof(MockErrMultiHandlesPostImageNoCompatiblePlugin)).ToList());
        Assert.Contains("[PostImage]", ex.Message);
    }

}

// -- Mock plugin types used by the integration tests above --

[Step("account")]
public class MockPreCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Step("contact")]
public class MockPostUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Step("lead")]
public class MockPostUpdateAsyncPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Step("account")]
public class MockValidationCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Step("account")]
[Filter("name", "telephone1")]
public class MockPreUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Step("account")]
[Filter(" name ", " firstname ")]
public class MockWithWhitespacePreUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Step("account")]
[PreImage]
public class MockPostDeletePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Step("account")]
[PreImage("name")]
[PostImage("name", "telephone1")]
public class MockBothImagesPostUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Step("account")]
[PreImage(Alias = "before")]
public class MockPostAssignPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Step("account", Order = 2, Config = "{\"key\":\"value\"}")]
public class MockPreRetrievePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Step("account", RunAs = "3b36b50c-03e5-4b5f-8882-aabbccddeeff")]
public class MockRunAsPostCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// No [Step] — detected as plugin type but produces no step
public class MockNoEntityPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// No [Step] — detected as plugin type but produces no step
public class MockNoStagePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// Abstract — excluded from detection entirely
[Step("account")]
public abstract class MockAbstractPlugin : IPlugin
{
    public abstract void Execute(IServiceProvider serviceProvider);
}

[Step("account")]
public class MockNoFilterPostUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Step("contact", SecondaryTable = "account")]
public class MockPreAssociatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Step("contact")]
public class MockNoSecondaryPreAssociatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// [Step] with no entity — should produce a step with a warning
[Step]
public class MockNoEntityStepPreCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// [Step("none")] — intentional any-entity, no warning
[Step("none")]
public class MockNoneEntityPreCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// [Handles] with Message enum overload — non-convention class name
[Step("account")]
[Handles(Message.Update, Stage.PreOperation)]
public class MockHandlesUpdatePrePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// [Handles] with PostOperationAsync — folds async mode into stage
[Step("account")]
[Handles(Message.Create, Stage.PostOperationAsync)]
public class MockHandlesAsyncPostCreatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// [Handles] with string overload for Custom API message
[Step("account")]
[Handles("mynamespace_MyAction", Stage.PostOperation)]
public class MockHandlesCustomApiPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// [Handles] on a class that already follows convention → R7 warning
[Step("account")]
[Handles(Message.Update, Stage.PreOperation)]
public class MockHandlesRedundantAccountPreUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// [Handles] without [Step] — no step produced
[Handles(Message.Update, Stage.PreOperation)]
public class MockHandlesWithoutStepPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// [Handles] with SecondaryTable on [Step]
[Step("contact", SecondaryTable = "account")]
[Handles(Message.Associate, Stage.PostOperation)]
public class MockHandlesAssociatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// Bulk operation mock plugins
[Step("account")]
public class MockNoFilterPostUpdateMultiplePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Step("account")]
public class MockPreDeleteMultiplePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// Multi-[Handles] fixtures

// Two distinct messages → no stage suffix in names
[Step("account")]
[Handles(Message.Create, Stage.PostOperation)]
[Handles(Message.Update, Stage.PostOperation)]
public class MockMultiHandlesCreateAndUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// Same message at two stages → stage suffix in all names
[Step("account")]
[Handles(Message.Update, Stage.PreOperation)]
[Handles(Message.Update, Stage.PostOperation)]
public class MockMultiHandlesUpdateTwoStagesPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// Three handles, one message repeats → all names qualified
[Step("account")]
[Handles(Message.Create, Stage.PostOperation)]
[Handles(Message.Update, Stage.PreOperation)]
[Handles(Message.Update, Stage.PostOperation)]
public class MockMultiHandlesThreeHandlesPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// [Filter] with Create + Update → filter only on Update step
[Step("account")]
[Filter("name")]
[Handles(Message.Create, Stage.PostOperation)]
[Handles(Message.Update, Stage.PostOperation)]
public class MockMultiHandlesFilterCreateUpdatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// [PreImage] with Create + Update → PreImage only on Update step
[Step("account")]
[PreImage("pre", "name")]
[Handles(Message.Create, Stage.PostOperation)]
[Handles(Message.Update, Stage.PostOperation)]
public class MockMultiHandlesPreImagePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// [PostImage] with Update + Delete → PostImage only on Update step
[Step("account")]
[PostImage("post", "name")]
[Handles(Message.Update, Stage.PostOperation)]
[Handles(Message.Delete, Stage.PostOperation)]
public class MockMultiHandlesPostImagePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// Same message, PostOperation sync + async → both steps name-qualified with mode
[Step("account")]
[Handles(Message.Update, Stage.PostOperation)]
[Handles(Message.Update, Stage.PostOperationAsync)]
public class MockMultiHandlesUpdateSyncAndAsyncPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

// Internal fixtures for error-path reader tests — internal keeps them out of Analyze() (which filters IsPublic: true)

[Step("account")]
[Filter("name")]
[Handles(Message.Create, Stage.PostOperation)]
[Handles(Message.Delete, Stage.PostOperation)]
internal class MockErrMultiHandlesFilterNoCompatiblePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Step("account")]
[PreImage("name")]
[Handles(Message.Create, Stage.PreOperation)]
[Handles(Message.Create, Stage.PostOperation)]
internal class MockErrMultiHandlesPreImageNoCompatiblePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[Step("account")]
[PostImage("name")]
[Handles(Message.Delete, Stage.PreOperation)]
[Handles(Message.Delete, Stage.PostOperation)]
internal class MockErrMultiHandlesPostImageNoCompatiblePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}
