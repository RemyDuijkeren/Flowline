using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using Flowline.Attributes;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Core.Plugins;
using Microsoft.Xrm.Sdk;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests;

public class PluginAssemblyReaderTests
{
    private static string DllPath => typeof(PluginAssemblyReaderTests).Assembly.Location;

    private static PluginAssemblyMetadata Analyze() =>
        new PluginAssemblyReader(new TestConsole()).Analyze(DllPath);

    private static PluginTypeMetadata GetPlugin(PluginAssemblyMetadata meta, string name) =>
        meta.Plugins.Single(p => p.Name == name);

    // ---- AnalyzePackage fixtures ----
    // AnalyzePackage reflects real DLLs extracted from a .nupkg's lib/<tfm>/ folder, so these tests
    // need genuine separate assemblies on disk (not just in-memory mock classes like the Analyze()
    // fixtures above). PersistedAssemblyBuilder (System.Reflection.Emit, no new package) builds tiny
    // real DLLs at test time, each referencing nothing but corelib and Microsoft.Xrm.Sdk (for the real
    // IPlugin interface) — deliberately minimal so resolving them doesn't require a wide dependency
    // closure. Microsoft.Xrm.Sdk.dll itself is never placed inside a fixture .nupkg's lib/ folder: a
    // real pac-plugin-init package excludes it there too (PrivateAssets="All" — the very mechanism KD5
    // describes), so it's instead copied next to the .nupkg on disk, mirroring the real "copy-local but
    // not packaged" layout that AnalyzePackage's nupkg-sibling-directory resolver widening depends on.

    private static List<PluginAssemblyMetadata> AnalyzePackage(string nupkgPath) =>
        new PluginAssemblyReader(new TestConsole()).AnalyzePackage(nupkgPath);

    // Builds a minimal real assembly on disk with one public class implementing IPlugin, and optionally
    // a second class deriving from a fake System.Activities.CodeActivity (workflowTypeName) — used to
    // test AnalyzePackage's CodeActivity rejection. IsDerivedFrom matches by FullName string, so a
    // same-named local type (defined in the same dynamic module) is sufficient without a real
    // System.Activities package reference.
    private static string BuildPluginDll(string dir, string assemblyName, string pluginTypeName, string? workflowTypeName = null)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule("MainModule");

        var pluginTb = mb.DefineType(pluginTypeName, TypeAttributes.Public | TypeAttributes.Class, typeof(object), [typeof(IPlugin)]);
        var executeMethod = typeof(IPlugin).GetMethod(nameof(IPlugin.Execute))!;
        var methodBuilder = pluginTb.DefineMethod(nameof(IPlugin.Execute),
            MethodAttributes.Public | MethodAttributes.Virtual, typeof(void), [typeof(IServiceProvider)]);
        methodBuilder.GetILGenerator().Emit(OpCodes.Ret);
        pluginTb.DefineMethodOverride(methodBuilder, executeMethod);
        pluginTb.CreateType();

        if (workflowTypeName != null)
        {
            var codeActivityType = mb.DefineType("System.Activities.CodeActivity", TypeAttributes.Public | TypeAttributes.Class, typeof(object)).CreateType();
            mb.DefineType(workflowTypeName, TypeAttributes.Public | TypeAttributes.Class, codeActivityType).CreateType();
        }

        var path = Path.Combine(dir, $"{assemblyName}.dll");
        ab.Save(path);
        return path;
    }

    // Builds a minimal real assembly on disk with one public class that does NOT implement IPlugin —
    // a stand-in for a pure-dependency DLL (e.g. Newtonsoft.Json) that AnalyzePackage must skip. When
    // referencedFrom is given, that assembly's type becomes the base type of typeName, exercising
    // cross-DLL type resolution (KD5's integration scenario) — since both live in the same nupkg lib/
    // folder once extracted, BuildResolverPaths' same-directory scan resolves the reference.
    private static string BuildDependencyDll(string dir, string assemblyName, string typeName)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule("MainModule");
        mb.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class, typeof(object)).CreateType();

        var path = Path.Combine(dir, $"{assemblyName}.dll");
        ab.Save(path);
        return path;
    }

    // Builds a plugin DLL whose IPlugin-implementing type derives from a class defined in a SEPARATE,
    // already-built dependency DLL — a genuine cross-assembly reference, not a same-module one. Loads
    // the dependency into a collectible ALC just long enough to get a real Type for the base-type
    // argument, then unloads it and polls (via WeakReference) until unload completes, so the dependency
    // DLL's file handle is released before the caller's temp directory cleanup runs.
    private static string BuildCrossRefPluginDll(string dir, string assemblyName, string typeName, string dependencyDllPath, string dependencyTypeName)
    {
        var loadContextRef = LoadAndBuildCrossRefPlugin(dir, assemblyName, typeName, dependencyDllPath, dependencyTypeName, out var path);

        for (var i = 0; i < 10 && loadContextRef.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        return path;
    }

    // Isolated into its own non-inlined method so the collectible ALC and every local referencing it
    // (loadContext, baseType, ab, mb, tb) are provably out of scope once this method returns — inlining
    // it into the caller could keep those locals rooted for the rest of the caller's stack frame,
    // preventing the ALC from ever becoming collectible.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndBuildCrossRefPlugin(string dir, string assemblyName, string typeName, string dependencyDllPath, string dependencyTypeName, out string path)
    {
        var loadContext = new System.Runtime.Loader.AssemblyLoadContext($"{assemblyName}Load", isCollectible: true);
        var baseType = loadContext.LoadFromAssemblyPath(dependencyDllPath).GetType(dependencyTypeName)!;

        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule("MainModule");
        var tb = mb.DefineType(typeName, TypeAttributes.Public | TypeAttributes.Class, baseType, [typeof(IPlugin)]);

        var executeMethod = typeof(IPlugin).GetMethod(nameof(IPlugin.Execute))!;
        var methodBuilder = tb.DefineMethod(nameof(IPlugin.Execute),
            MethodAttributes.Public | MethodAttributes.Virtual, typeof(void), [typeof(IServiceProvider)]);
        methodBuilder.GetILGenerator().Emit(OpCodes.Ret);
        tb.DefineMethodOverride(methodBuilder, executeMethod);
        tb.CreateType();

        path = Path.Combine(dir, $"{assemblyName}.dll");
        ab.Save(path);

        loadContext.Unload();
        return new WeakReference(loadContext);
    }

    // Zips the given DLLs into a .nupkg under lib/<tfm>/, mirroring the real OPC package layout.
    private static string BuildNupkg(string dir, params string[] dllPaths)
    {
        var nupkgPath = Path.Combine(dir, $"{Guid.NewGuid():N}.nupkg");
        using var archive = ZipFile.Open(nupkgPath, ZipArchiveMode.Create);
        foreach (var dllPath in dllPaths)
            archive.CreateEntryFromFile(dllPath, $"lib/net10.0/{Path.GetFileName(dllPath)}");
        return nupkgPath;
    }

    // Copies Microsoft.Xrm.Sdk.dll next to the .nupkg (NOT inside its lib/ folder) — mirroring a real
    // pac-plugin-init package, where the SDK assembly is copy-local to the build output but excluded
    // from the packed nupkg content (PrivateAssets="All"). AnalyzePackage's resolver widening to the
    // nupkg's own directory is what makes this resolvable without bundling it into the package.
    private static void CopyXrmSdkDllNextTo(string dir) =>
        File.Copy(
            Path.Combine(Path.GetDirectoryName(DllPath)!, "Microsoft.Xrm.Sdk.dll"),
            Path.Combine(dir, "Microsoft.Xrm.Sdk.dll"),
            overwrite: true);

    // Copies Microsoft.Xrm.Sdk.dll into a "net462"-style subfolder of dir, not flat alongside the
    // .nupkg — mirroring a real `dotnet build` of a pac-plugin-init-shaped project, where the .nupkg
    // lands directly in bin/Release/ while copy-local dependencies land one level down in
    // bin/Release/net462/ (and again in its own net462/publish/ sub-copy). AnalyzePackage's
    // nupkg-sibling-directory widening must recurse to find this, not just scan dir's top level.
    private static void CopyXrmSdkDllOneLevelDeeper(string dir)
    {
        var tfmDir = Directory.CreateDirectory(Path.Combine(dir, "net462")).FullName;
        File.Copy(
            Path.Combine(Path.GetDirectoryName(DllPath)!, "Microsoft.Xrm.Sdk.dll"),
            Path.Combine(tfmDir, "Microsoft.Xrm.Sdk.dll"),
            overwrite: true);

        var publishDir = Directory.CreateDirectory(Path.Combine(tfmDir, "publish")).FullName;
        File.Copy(
            Path.Combine(Path.GetDirectoryName(DllPath)!, "Microsoft.Xrm.Sdk.dll"),
            Path.Combine(publishDir, "Microsoft.Xrm.Sdk.dll"),
            overwrite: true);
    }

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
            PluginTypeMetadataScanner.ValidateExecutionMode("MockPreUpdateAsyncPlugin",
                (int)ProcessingStage.PreOperation, (int)ProcessingMode.Asynchronous));
        Assert.Contains("Asynchronous", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateExecutionMode_AsyncOnPreValidation_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateExecutionMode("MockValidationUpdateAsyncPlugin",
                (int)ProcessingStage.PreValidation, (int)ProcessingMode.Asynchronous));
        Assert.Contains("Asynchronous", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateExecutionMode_AsyncOnPostOperation_DoesNotThrow()
    {
        PluginTypeMetadataScanner.ValidateExecutionMode("MockPostUpdateAsyncPlugin",
            (int)ProcessingStage.PostOperation, (int)ProcessingMode.Asynchronous);
    }

    [Fact]
    public void ValidateLogicalName_EmptyString_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateLogicalName("MockPreCreatePlugin", ""));
        Assert.Contains("[Step]", ex.Message);
        Assert.Contains("none", ex.Message);
    }

    [Fact]
    public void ValidateLogicalName_WhitespaceString_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateLogicalName("MockPreCreatePlugin", "  "));
        Assert.Contains("[Step]", ex.Message);
    }

    [Fact]
    public void ValidateLogicalName_Null_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateLogicalName("MockPreCreatePlugin", null));
        Assert.Contains("[Step]", ex.Message);
        Assert.Contains("\"none\"", ex.Message);
    }

    [Fact]
    public void ValidateLogicalName_ValidName_DoesNotThrow()
    {
        PluginTypeMetadataScanner.ValidateLogicalName("MockPreCreatePlugin", "account");
    }

    [Fact]
    public void ValidateSecondaryLogicalName_EmptyString_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateSecondaryLogicalName("MockPreAssociatePlugin", ""));
        Assert.Contains("SecondaryTable", ex.Message);
        Assert.Contains("none", ex.Message);
    }

    [Fact]
    public void ValidateSecondaryLogicalName_Null_DoesNotThrow()
    {
        PluginTypeMetadataScanner.ValidateSecondaryLogicalName("MockPreAssociatePlugin", null);
    }

    [Fact]
    public void ValidateCustomApiAttributesOnStep_WithAttributes_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateCustomApiAttributesOnStep("MockPreCreatePlugin", true));
        Assert.Contains("[Input]", ex.Message);
        Assert.Contains("[Output]", ex.Message);
    }

    [Fact]
    public void ValidateCustomApiAttributesOnStep_WithoutAttributes_DoesNotThrow()
    {
        PluginTypeMetadataScanner.ValidateCustomApiAttributesOnStep("MockPreCreatePlugin", false);
    }

    [Fact]
    public void Analyze_CustomApiWithoutUniqueName_DerivesBaseNameFromClassName()
    {
        var api = GetPlugin(Analyze(), nameof(MockGetAccountRiskApi)).CustomApis.Single();

        Assert.Equal("MockGetAccountRisk", api.BaseName);
        Assert.Null(api.UniqueNameOverride);
    }

    [Fact]
    public void Analyze_CustomApiWithUniqueName_UsesOverrideVerbatim()
    {
        var api = GetPlugin(Analyze(), nameof(MockApproveOrderCustomApiPlugin)).CustomApis.Single();

        Assert.Equal("dev1_LegacyOrderApproval", api.UniqueNameOverride);
    }

    [Fact]
    public void Analyze_CustomApiWithUniqueNameNoDisplayName_DefaultsFromSegmentAfterFirstUnderscore()
    {
        var api = GetPlugin(Analyze(), nameof(MockUniqueNameDisplayDefaultApi)).CustomApis.Single();

        Assert.Equal("My Custom Api", api.DisplayName);
    }

    [Fact]
    public void Analyze_CustomApiWithUniqueNameAndExplicitDisplayName_ExplicitWins()
    {
        var api = GetPlugin(Analyze(), nameof(MockUniqueNameExplicitDisplayApi)).CustomApis.Single();

        Assert.Equal("Explicit Label", api.DisplayName);
    }

    [Fact]
    public void Analyze_CustomApiWithUniqueName_RequestParametersStillKeyOffBaseName()
    {
        var api = GetPlugin(Analyze(), nameof(MockUniqueNameWithParamsApi)).CustomApis.Single();

        Assert.Equal("MockUniqueNameWithParams", api.BaseName);
        var param = Assert.Single(api.RequestParameters);
        // ReadClassLevelParameters composes the composite label into DisplayName's positional slot
        // (a pre-existing quirk, not something this feature changes) — assert there, not on Name.
        Assert.Contains("MockUniqueNameWithParams.accountId", param.DisplayName);
    }

    [Fact]
    public void ValidateCustomApiUniqueNameFormat_Null_DoesNotThrow()
    {
        PluginTypeMetadataScanner.ValidateCustomApiUniqueNameFormat("MockGetAccountRiskApi", null);
    }

    [Fact]
    public void ValidateCustomApiUniqueNameFormat_Empty_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateCustomApiUniqueNameFormat("MockGetAccountRiskApi", ""));
        Assert.Contains("UniqueName", ex.Message);
        Assert.Contains("empty", ex.Message);
    }

    [Fact]
    public void ValidateCustomApiUniqueNameFormat_Whitespace_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateCustomApiUniqueNameFormat("MockGetAccountRiskApi", "   "));
        Assert.Contains("UniqueName", ex.Message);
    }

    [Fact]
    public void ValidateCustomApiUniqueNameFormat_LeadingDigit_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateCustomApiUniqueNameFormat("MockGetAccountRiskApi", "123Bad_Name"));
        Assert.Contains("invalid", ex.Message);
    }

    [Fact]
    public void ValidateCustomApiUniqueNameFormat_EmbeddedSpace_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateCustomApiUniqueNameFormat("MockGetAccountRiskApi", "Bad Name_Here"));
        Assert.Contains("invalid", ex.Message);
    }

    [Fact]
    public void ValidateCustomApiUniqueNameFormat_NoUnderscore_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateCustomApiUniqueNameFormat("MockGetAccountRiskApi", "NoUnderscoreAtAll"));
        Assert.Contains("publisher prefix", ex.Message);
    }

    [Fact]
    public void ValidateCustomApiUniqueNameFormat_EmptyAfterPrefix_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateCustomApiUniqueNameFormat("MockGetAccountRiskApi", "dev1_"));
        Assert.Contains("nothing after", ex.Message);
    }

    [Fact]
    public void ValidateSecondaryTable_OnNonAssociateMessage_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateSecondaryTable("MockPreCreatePlugin", "Create", "account"));
        Assert.Contains("SecondaryTable", ex.Message);
        Assert.Contains("Create", ex.Message);
    }

    [Fact]
    public void ValidateSecondaryTable_OnAssociate_DoesNotThrow()
    {
        PluginTypeMetadataScanner.ValidateSecondaryTable("MockPreAssociatePlugin", "Associate", "account");
    }

    [Fact]
    public void ValidateSecondaryTable_Null_DoesNotThrow()
    {
        PluginTypeMetadataScanner.ValidateSecondaryTable("MockPreCreatePlugin", "Create", null);
    }

    [Fact]
    public void ValidateSecondaryTable_AssociateWithoutSecondaryTable_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateSecondaryTable("MockPreAssociatePlugin", "Associate", null));
        Assert.Contains("SecondaryTable", ex.Message);
        Assert.Contains("Associate", ex.Message);
        Assert.Contains("\"none\"", ex.Message);
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
            PluginTypeMetadataScanner.ValidateFilter("MockPreCreatePlugin", "Create", "name,telephone1"));
        Assert.Contains("[Filter]", ex.Message);
        Assert.Contains("Create", ex.Message);
    }

    [Fact]
    public void ValidateFilter_FilterOnDelete_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateFilter("MockPreDeletePlugin", "Delete", "name"));
        Assert.Contains("[Filter]", ex.Message);
        Assert.Contains("Delete", ex.Message);
    }

    [Fact]
    public void ValidateFilter_FilterOnUpdate_DoesNotThrow()
    {
        PluginTypeMetadataScanner.ValidateFilter("MockPreUpdatePlugin", "Update", "name,telephone1");
    }

    [Fact]
    public void ValidateFilter_NullFilter_DoesNotThrow()
    {
        PluginTypeMetadataScanner.ValidateFilter("MockPreCreatePlugin", "Create", null);
    }

    [Fact]
    public void ValidateImages_PreImageOnCreate_Throws()
    {
        var images = new List<PluginImageMetadata> { new("Pre Image", "preimage", (int)ImageType.PreImage, "name") };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateImages("MockPlugin", "Create", (int)ProcessingStage.PostOperation, images));
        Assert.Contains("[PreImage]", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateImages_PostImageOnDelete_Throws()
    {
        var images = new List<PluginImageMetadata> { new("Post Image", "postimage", (int)ImageType.PostImage, "name") };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateImages("MockPlugin", "Delete", (int)ProcessingStage.PostOperation, images));
        Assert.Contains("[PostImage]", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateImages_PostImageOnPreOperation_Throws()
    {
        var images = new List<PluginImageMetadata> { new("Post Image", "postimage", (int)ImageType.PostImage, "name") };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateImages("MockPlugin", "Update", (int)ProcessingStage.PreOperation, images));
        Assert.Contains("[PostImage]", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateImages_PostImageOnPreValidation_Throws()
    {
        var images = new List<PluginImageMetadata> { new("Post Image", "postimage", (int)ImageType.PostImage, "name") };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateImages("MockPlugin", "Update", (int)ProcessingStage.PreValidation, images));
        Assert.Contains("[PostImage]", ex.Message);
        Assert.Contains("https://learn.microsoft.com", ex.Message);
    }

    [Fact]
    public void ValidateImages_ImageOnUnsupportedMessage_Throws()
    {
        var images = new List<PluginImageMetadata> { new("Pre Image", "preimage", (int)ImageType.PreImage, "name") };
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateImages("MockPlugin", "Retrieve", (int)ProcessingStage.PostOperation, images));
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
            PluginTypeMetadataScanner.ParseStepClassNameOrThrow("AccountCreatePlugin"));

        Assert.Contains("[Step]", ex.Message);
        Assert.Contains("no stage", ex.Message);
        Assert.Contains("AccountPreCreatePlugin", ex.Message);
        Assert.Contains(nameof(Message), ex.Message);
    }

    [Fact]
    public void ParseStepClassNameOrThrow_NoMessageKeyword_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ParseStepClassNameOrThrow("AccountPostPlugin"));

        Assert.Contains("[Step]", ex.Message);
        Assert.Contains("no message", ex.Message);
        Assert.Contains("AccountPostUpdatePlugin", ex.Message);
    }

    [Fact]
    public void ParseStepClassNameOrThrow_NoStageOrMessageKeyword_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ParseStepClassNameOrThrow("AccountPlugin"));

        Assert.Contains("[Step]", ex.Message);
        Assert.Contains("stage or message", ex.Message);
    }

    [Fact]
    public void ValidateStepUsage_StepOnNonPlugin_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateStepUsage("NotAPlugin", hasStepAttribute: true, isPlugin: false));

        Assert.Contains("[Step]", ex.Message);
        Assert.Contains("IPlugin", ex.Message);
    }

    [Fact]
    public void ValidateStepUsage_StepOnPlugin_DoesNotThrow()
    {
        PluginTypeMetadataScanner.ValidateStepUsage("AccountPreCreatePlugin", hasStepAttribute: true, isPlugin: true);
    }

    [Fact]
    public void TryParseClassName_PreCreate_ParsesCorrectly()
    {
        Assert.True(PluginTypeMetadataScanner.TryParseClassName("AccountPreCreatePlugin", out var msg, out var stage, out var mode));
        Assert.Equal("Create", msg);
        Assert.Equal((int)ProcessingStage.PreOperation, stage);
        Assert.Equal((int)ProcessingMode.Synchronous, mode);
    }

    [Fact]
    public void TryParseClassName_PostUpdateAsync_ParsesCorrectly()
    {
        Assert.True(PluginTypeMetadataScanner.TryParseClassName("InvoicePostUpdateAsyncPlugin", out var msg, out var stage, out var mode));
        Assert.Equal("Update", msg);
        Assert.Equal((int)ProcessingStage.PostOperation, stage);
        Assert.Equal((int)ProcessingMode.Asynchronous, mode);
    }

    [Fact]
    public void TryParseClassName_ValidationCreate_ParsesPreValidationStage()
    {
        Assert.True(PluginTypeMetadataScanner.TryParseClassName("AccountValidationCreatePlugin", out _, out var stage, out _));
        Assert.Equal((int)ProcessingStage.PreValidation, stage);
    }

    [Fact]
    public void TryParseClassName_NoStageKeyword_ReturnsFalse()
    {
        Assert.False(PluginTypeMetadataScanner.TryParseClassName("AccountCreatePlugin", out _, out _, out _));
    }

    [Fact]
    public void TryParseClassName_NoMessageKeyword_ReturnsFalse()
    {
        Assert.False(PluginTypeMetadataScanner.TryParseClassName("AccountPostPlugin", out _, out _, out _));
    }

    [Fact]
    public void TryParseClassName_RetrieveMultiple_MatchesBeforeRetrieve()
    {
        Assert.True(PluginTypeMetadataScanner.TryParseClassName("AccountPreRetrieveMultiplePlugin", out var msg, out _, out _));
        Assert.Equal("RetrieveMultiple", msg);
    }

    // ---- Bulk operation message tests ----

    [Fact]
    public void ValidateFilter_FilterOnUpdateMultiple_DoesNotThrow()
    {
        PluginTypeMetadataScanner.ValidateFilter("MockPostUpdateMultiplePlugin", "UpdateMultiple", "name,telephone1");
    }

    [Fact]
    public void ValidateFilter_FilterOnDeleteMultiple_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateFilter("MockPreDeleteMultiplePlugin", "DeleteMultiple", "name"));
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
        PluginTypeMetadataScanner.ValidateImages("MockPostCreateMultiplePlugin", "CreateMultiple", (int)ProcessingStage.PostOperation, images);
    }

    [Fact]
    public void ValidateImages_ImageOnUpdateMultiple_DoesNotThrow()
    {
        var images = new List<PluginImageMetadata> { new("Pre Image", "preimage", (int)ImageType.PreImage, "name") };
        PluginTypeMetadataScanner.ValidateImages("MockPostUpdateMultiplePlugin", "UpdateMultiple", (int)ProcessingStage.PostOperation, images);
    }

    [Fact]
    public void TryParseClassName_PostCreateMultiple_MatchesBeforeCreate()
    {
        Assert.True(PluginTypeMetadataScanner.TryParseClassName("AccountPostCreateMultiplePlugin", out var msg, out _, out _));
        Assert.Equal("CreateMultiple", msg);
    }

    [Fact]
    public void TryParseClassName_PostUpdateMultiple_MatchesBeforeUpdate()
    {
        Assert.True(PluginTypeMetadataScanner.TryParseClassName("AccountPostUpdateMultiplePlugin", out var msg, out _, out _));
        Assert.Equal("UpdateMultiple", msg);
    }

    [Fact]
    public void TryParseClassName_PostUpsert_ParsesCorrectly()
    {
        Assert.True(PluginTypeMetadataScanner.TryParseClassName("AccountPostUpsertPlugin", out var msg, out var stage, out _));
        Assert.Equal("Upsert", msg);
        Assert.Equal((int)ProcessingStage.PostOperation, stage);
    }

    [Fact]
    public void TryParseClassName_PostUpsertMultiple_MatchesBeforeUpsert()
    {
        Assert.True(PluginTypeMetadataScanner.TryParseClassName("AccountPostUpsertMultiplePlugin", out var msg, out _, out _));
        Assert.Equal("UpsertMultiple", msg);
    }

    [Fact]
    public void TryParseClassName_PreDeleteMultiple_MatchesBeforeDelete()
    {
        Assert.True(PluginTypeMetadataScanner.TryParseClassName("AccountPreDeleteMultiplePlugin", out var msg, out _, out _));
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
    public void Analyze_MultiHandles_NudgeWarningPresentOnFirstStepOnly()
    {
        var plugin = GetPlugin(Analyze(), nameof(MockMultiHandlesCreateAndUpdatePlugin));

        Assert.Contains(plugin.Steps[0].Warnings, w => w.Contains("multiple [[Handles]] detected"));
        Assert.DoesNotContain(plugin.Steps[1].Warnings, w => w.Contains("multiple [[Handles]] detected"));
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
        PluginTypeMetadataScanner.ValidateMultiHandlesFilter(name, hasFilter, anyCompatible);
    }

    [Fact]
    public void ValidateMultiHandlesFilter_FilterPresentNoCompatible_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateMultiHandlesFilter("MyPlugin", hasFilter: true, anyFilterCompatible: false));
        Assert.Contains("[Filter]", ex.Message);
        Assert.Contains("Update", ex.Message);
    }

    [Theory]
    [InlineData("MyPlugin", false, false)]
    [InlineData("MyPlugin", true, true)]
    public void ValidateMultiHandlesPreImage_NoThrow(string name, bool hasPreImage, bool anyCompatible)
    {
        PluginTypeMetadataScanner.ValidateMultiHandlesPreImage(name, hasPreImage, anyCompatible);
    }

    [Fact]
    public void ValidateMultiHandlesPreImage_PreImagePresentNoCompatible_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateMultiHandlesPreImage("MyPlugin", hasPreImage: true, anyPreImageCompatible: false));
        Assert.Contains("[PreImage]", ex.Message);
    }

    [Theory]
    [InlineData("MyPlugin", false, false)]
    [InlineData("MyPlugin", true, true)]
    public void ValidateMultiHandlesPostImage_NoThrow(string name, bool hasPostImage, bool anyCompatible)
    {
        PluginTypeMetadataScanner.ValidateMultiHandlesPostImage(name, hasPostImage, anyCompatible);
    }

    [Fact]
    public void ValidateMultiHandlesPostImage_PostImagePresentNoCompatible_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.ValidateMultiHandlesPostImage("MyPlugin", hasPostImage: true, anyPostImageCompatible: false));
        Assert.Contains("[PostImage]", ex.Message);
    }

    [Fact]
    public void TryBuildSteps_MultiHandles_FilterNoCompatibleMessage_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.TryBuildSteps(typeof(MockErrMultiHandlesFilterNoCompatiblePlugin)).ToList());
        Assert.Contains("[Filter]", ex.Message);
    }

    [Fact]
    public void TryBuildSteps_MultiHandles_PreImageNoCompatibleMessage_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.TryBuildSteps(typeof(MockErrMultiHandlesPreImageNoCompatiblePlugin)).ToList());
        Assert.Contains("[PreImage]", ex.Message);
    }

    [Fact]
    public void TryBuildSteps_MultiHandles_PostImageNoCompatibleMessage_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.TryBuildSteps(typeof(MockErrMultiHandlesPostImageNoCompatiblePlugin)).ToList());
        Assert.Contains("[PostImage]", ex.Message);
    }

    [Fact]
    public void TryBuildSteps_MultiHandles_DuplicateHandles_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            PluginTypeMetadataScanner.TryBuildSteps(typeof(MockErrMultiHandlesDuplicatePlugin)).ToList());
        Assert.Contains("same step name", ex.Message);
    }

    // ---- AnalyzePackage tests ----

    [Fact]
    public void AnalyzePackage_OnePluginDllAndOneDependencyDll_ReturnsOnlyPluginBearing()
    {
        var dir = Directory.CreateTempSubdirectory("flowline-reader-test-").FullName;
        try
        {
            var pluginDll = BuildPluginDll(dir, "OnlyPlugin", "OnlyPackagePlugin");
            var dependencyDll = BuildDependencyDll(dir, "PureDependency", "SomeHelper");
            var nupkg = BuildNupkg(dir, pluginDll, dependencyDll);
            CopyXrmSdkDllNextTo(dir);

            var result = AnalyzePackage(nupkg);

            var meta = Assert.Single(result);
            Assert.Contains(meta.Plugins, p => p.Name == "OnlyPackagePlugin");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AnalyzePackage_XrmSdkOneLevelBelowNupkgDir_StillResolves()
    {
        var dir = Directory.CreateTempSubdirectory("flowline-reader-test-").FullName;
        try
        {
            var pluginDll = BuildPluginDll(dir, "NestedSdkPlugin", "NestedSdkPackagePlugin");
            var nupkg = BuildNupkg(dir, pluginDll);
            CopyXrmSdkDllOneLevelDeeper(dir);

            var result = AnalyzePackage(nupkg);

            var meta = Assert.Single(result);
            Assert.Contains(meta.Plugins, p => p.Name == "NestedSdkPackagePlugin");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AnalyzePackage_SameNamedSiblingDllsWithDifferentContent_ThrowsInvalidOperationException()
    {
        var dir = Directory.CreateTempSubdirectory("flowline-reader-test-").FullName;
        try
        {
            var pluginDll = BuildPluginDll(dir, "DriftPlugin", "DriftPackagePlugin");
            var nupkg = BuildNupkg(dir, pluginDll);

            // Two same-named siblings with genuinely different content — mimics a stale prior build
            // leaving net462/ and net462/publish/ out of sync after a dependency version bump.
            var tfmDir = Directory.CreateDirectory(Path.Combine(dir, "net462")).FullName;
            var publishDir = Directory.CreateDirectory(Path.Combine(tfmDir, "publish")).FullName;
            File.WriteAllBytes(Path.Combine(tfmDir, "Drifted.Dependency.dll"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(publishDir, "Drifted.Dependency.dll"), [4, 5, 6]);

            var ex = Assert.Throws<InvalidOperationException>(() => AnalyzePackage(nupkg));
            Assert.Contains("Drifted.Dependency.dll", ex.Message);
            Assert.Contains("different content", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AnalyzePackage_TwoIndependentPluginDlls_ReturnsTwoScopedToOwnTypes()
    {
        var dir = Directory.CreateTempSubdirectory("flowline-reader-test-").FullName;
        try
        {
            var firstPluginDll = BuildPluginDll(dir, "FirstPlugin", "FirstPackagePlugin");
            var secondPluginDll = BuildPluginDll(dir, "SecondPlugin", "SecondPackagePlugin");
            var dependencyDll = BuildDependencyDll(dir, "PureDependency", "SomeHelper");
            var nupkg = BuildNupkg(dir, firstPluginDll, secondPluginDll, dependencyDll);
            CopyXrmSdkDllNextTo(dir);

            var result = AnalyzePackage(nupkg);

            Assert.Equal(2, result.Count);

            var withFirst = result.Single(m => m.Plugins.Any(p => p.Name == "FirstPackagePlugin"));
            var withSecond = result.Single(m => m.Plugins.Any(p => p.Name == "SecondPackagePlugin"));

            Assert.DoesNotContain(withFirst.Plugins, p => p.Name == "SecondPackagePlugin");
            Assert.DoesNotContain(withSecond.Plugins, p => p.Name == "FirstPackagePlugin");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AnalyzePackage_NoPluginBearingDlls_ReturnsEmptyCollection()
    {
        var dir = Directory.CreateTempSubdirectory("flowline-reader-test-").FullName;
        try
        {
            var dependencyDll = BuildDependencyDll(dir, "PureDependency", "SomeHelper");
            var nupkg = BuildNupkg(dir, dependencyDll);

            var result = AnalyzePackage(nupkg);

            Assert.Empty(result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AnalyzePackage_CodeActivityInOneOfSeveralDlls_ThrowsNamingTypeAndDll()
    {
        var dir = Directory.CreateTempSubdirectory("flowline-reader-test-").FullName;
        try
        {
            var workflowPluginDll = BuildPluginDll(dir, "WorkflowPlugin", "PackagePlugin", workflowTypeName: "PackageWorkflowActivity");
            var secondPluginDll = BuildPluginDll(dir, "SecondPlugin", "SecondPackagePlugin");
            var nupkg = BuildNupkg(dir, workflowPluginDll, secondPluginDll);
            CopyXrmSdkDllNextTo(dir);

            var ex = Assert.Throws<InvalidOperationException>(() => AnalyzePackage(nupkg));

            Assert.Contains("PackageWorkflowActivity", ex.Message);
            Assert.Contains(Path.GetFileName(workflowPluginDll), ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AnalyzePackage_PluginReferencingDependencyDllType_ResolvesAcrossDlls()
    {
        var dir = Directory.CreateTempSubdirectory("flowline-reader-test-").FullName;
        try
        {
            var dependencyDll = BuildDependencyDll(dir, "SharedDependency", "SharedBase");
            var crossRefPluginDll = BuildCrossRefPluginDll(dir, "CrossRefPlugin", "CrossRefPlugin", dependencyDll, "SharedBase");
            var nupkg = BuildNupkg(dir, crossRefPluginDll, dependencyDll);
            CopyXrmSdkDllNextTo(dir);

            var result = AnalyzePackage(nupkg);

            var meta = Assert.Single(result);
            Assert.Contains(meta.Plugins, p => p.Name == "CrossRefPlugin");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
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

[CustomApi]
public class MockGetAccountRiskApi : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[CustomApi(UniqueName = "dev1_LegacyOrderApproval")]
public class MockApproveOrderCustomApiPlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[CustomApi(UniqueName = "dev1_MyCustomApi")]
public class MockUniqueNameDisplayDefaultApi : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[CustomApi(UniqueName = "dev1_MyCustomApi", DisplayName = "Explicit Label")]
public class MockUniqueNameExplicitDisplayApi : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}

[CustomApi(UniqueName = "dev1_BulkApprove")]
[Input("accountId", FieldType.EntityReference, Table = "account")]
[Output("riskScore", FieldType.Integer)]
public class MockUniqueNameWithParamsApi : IPlugin
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

[Step("account")]
[Handles(Message.Update, Stage.PostOperation)]
[Handles(Message.Update, Stage.PostOperation)]
internal class MockErrMultiHandlesDuplicatePlugin : IPlugin
{
    public void Execute(IServiceProvider serviceProvider) => throw new NotImplementedException();
}
