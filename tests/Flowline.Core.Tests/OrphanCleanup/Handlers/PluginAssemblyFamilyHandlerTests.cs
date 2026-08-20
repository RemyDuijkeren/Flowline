using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.ServiceModel;
using Flowline.Core.Services;
using Flowline.Core.OrphanCleanup;
using Flowline.Core.OrphanCleanup.Handlers;
using Flowline.Core.Models;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests.OrphanCleanup.Handlers;

// Characterization + Prio coverage for U2: PluginAssemblyFamilyHandler migrates PluginAssembly (91) /
// PluginType (90) / Step (92) / StepImage (93) detection from OrphanCleanupService's NameResolvableTypes
// and its old ExecutionOrder array (removed during U9's orchestrator rewrite), preserving today's exact
// Auto/Delete output and ordering, plus the new Prio1/Prio2/Prio3 axis (KTD8). Tests call DetectAsync
// directly — execution (DeleteAsync, dependency-fault deferral) stays in the orchestrator (U9), so this
// suite asserts only on the returned HandlerFinding, never on service-mutation calls.
public class PluginAssemblyFamilyHandlerTests
{
    readonly IOrganizationServiceAsync2 _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
    readonly TestConsole _console = new();
    readonly PluginAssemblyFamilyHandler _handler;

    public PluginAssemblyFamilyHandlerTests()
    {
        // Fix 5: PluginPackageContentReader memoizes reflection per package directory for the whole
        // process — see PluginPackageAssemblyCheckServiceTests' constructor for why this is cleared.
        Flowline.Core.Plugins.PluginPackageContentReader.ClearCache();

        _console.Profile.Width = 400; // avoid word-wrap splitting longer assertion substrings across lines
        _handler = new PluginAssemblyFamilyHandler(_console);
        // Default: any unconfigured RetrieveMultipleAsync returns empty rather than NSubstitute's null
        // default — real Dataverse never returns a null EntityCollection. Matches OrphanCleanupServiceTests'
        // own convention.
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));
    }

    DetectionContext Ctx(RunMode mode = RunMode.Normal) =>
        new("unused-package-src-root", _serviceMock, "MySolution", mode, []);

    DetectionContext Ctx(string dataverseSolutionSrcRoot, RunMode mode = RunMode.Normal) =>
        new(dataverseSolutionSrcRoot, _serviceMock, "MySolution", mode, []);

    // Creates a customapis/<name>/ directory (the shape ComponentClassifier.ScanCustomApiNames scans) so
    // a test can simulate a CustomApi still declared in local source.
    static string CreateLocalCustomApiSource(string apiName)
    {
        var root = Directory.CreateTempSubdirectory("flowline-customapi-src-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "customapis", apiName));
        return root;
    }

    void SetupName(string entityLogicalName, string nameAttribute, Guid id, string name)
    {
        var entity = new Entity(entityLogicalName, id) { [nameAttribute] = name };
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == entityLogicalName && q.ColumnSet.Columns.Contains(nameAttribute))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([entity])));
    }

    void SetupPackageIds(params (Guid AssemblyId, Guid PackageId)[] assemblies)
    {
        var entities = assemblies.Select(a => new Entity("pluginassembly", a.AssemblyId)
        {
            ["packageid"] = new EntityReference("pluginpackage", a.PackageId),
        }).ToList();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly" && q.ColumnSet.Columns.Contains("packageid"))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }

    void SetupStepStates(params (Guid StepId, Guid PluginTypeId, bool Enabled)[] steps)
    {
        var entities = steps.Select(s => new Entity("sdkmessageprocessingstep", s.StepId)
        {
            ["plugintypeid"] = new EntityReference("plugintype", s.PluginTypeId),
            ["statecode"]    = new OptionSetValue(s.Enabled ? 0 : 1),
        }).ToList();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep" && q.ColumnSet.Columns.Contains("statecode"))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }

    // Id-only lookups (ColumnSet(false)) used by ResolvePackageChildCleanupFindingsAsync — matched by
    // entity + filter attribute rather than by a resolved column, since none is requested.
    void SetupChildIds(string entityLogicalName, string filterAttribute, params Guid[] ids)
    {
        var entities = ids.Select(id => new Entity(entityLogicalName, id)).ToList();
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == entityLogicalName
                    && q.Criteria.Conditions.Any(c => c.AttributeName == filterAttribute))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }

    void SetupPackageUniqueName(Guid packageId, string uniqueName)
    {
        var entity = new Entity("pluginpackage", packageId) { ["uniquename"] = uniqueName };
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginpackage" && q.ColumnSet.Columns.Contains("uniquename"))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([entity])));
    }

    void SetupPackageUniqueNames(params (Guid PackageId, string UniqueName)[] packages)
    {
        var entities = packages.Select(p => new Entity("pluginpackage", p.PackageId) { ["uniquename"] = p.UniqueName }).ToList();
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginpackage" && q.ColumnSet.Columns.Contains("uniquename"))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }

    void SetupNames(string entityLogicalName, string nameAttribute, params (Guid Id, string Name)[] rows)
    {
        var entities = rows.Select(r => new Entity(entityLogicalName, r.Id) { [nameAttribute] = r.Name }).ToList();
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == entityLogicalName && q.ColumnSet.Columns.Contains(nameAttribute))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }

    // Fix 3: matches only the id-only plugintype query filtered by THIS assembly, so a test can give a
    // content-carried assembly its own plugin types without also handing them to a redirected sibling's
    // cascade lookup (both queries hit plugintype/pluginassemblyid).
    void SetupPluginTypesForAssembly(Guid assemblyId, params Guid[] typeIds)
    {
        var entities = typeIds.Select(id => new Entity("plugintype", id)).ToList();
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "plugintype"
                    && q.Criteria.Conditions.Any(c => c.AttributeName == "pluginassemblyid" && c.Values.Contains((object)assemblyId)))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }

    void FailPluginTypesForAssembly(Guid assemblyId)
    {
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "plugintype"
                    && q.Criteria.Conditions.Any(c => c.AttributeName == "pluginassemblyid" && c.Values.Contains((object)assemblyId)))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new InvalidOperationException("plugintype lookup timeout")));
    }

    // ---- U5/KTD7: real-DLL package-content fixtures (mirrors PluginPackageAssemblyCheckServiceTests —
    // PluginPackageContentReader.ScanReflectedAssemblyNamesByPackage reflects genuine DLLs on disk via
    // MetadataLoadContext, so an in-memory mock type won't do) ----

    static string BuildPluginDll(string dir, string assemblyName, string pluginTypeName)
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

        var path = Path.Combine(dir, $"{assemblyName}.dll");
        ab.Save(path);
        return path;
    }

    static string BuildNupkg(string dir, params string[] dllPaths)
    {
        var nupkgPath = Path.Combine(dir, $"{Guid.NewGuid():N}.nupkg");
        using var archive = ZipFile.Open(nupkgPath, ZipArchiveMode.Create);
        foreach (var dllPath in dllPaths)
            archive.CreateEntryFromFile(dllPath, $"lib/net10.0/{Path.GetFileName(dllPath)}");
        return nupkgPath;
    }

    // Lays out pluginpackages/<uniqueName>/pluginpackage.xml + package/<nupkg>.nupkg, mirroring the pac
    // solution unpack shape both PluginPackageAssemblyCheckService and PluginPackageContentReader read.
    static string BuildPackageSrcTree(string uniqueName, string nupkgPath)
    {
        var root = Directory.CreateTempSubdirectory("flowline-pkgcontent-src-").FullName;
        AddPackageToSrcTree(root, uniqueName, nupkgPath);
        return root;
    }

    static void AddPackageToSrcTree(string root, string uniqueName, string nupkgPath)
    {
        var packageDir = Directory.CreateDirectory(Path.Combine(root, "pluginpackages", uniqueName)).FullName;
        File.WriteAllText(Path.Combine(packageDir, "pluginpackage.xml"),
            $"""<pluginpackage uniquename="{uniqueName}"><name>{uniqueName}</name></pluginpackage>""");
        var packageContentDir = Directory.CreateDirectory(Path.Combine(packageDir, "package")).FullName;
        File.Copy(nupkgPath, Path.Combine(packageContentDir, Path.GetFileName(nupkgPath)));
    }

    // A package directory the imported solution carries that this can't read at all — no
    // pluginpackage.xml, so ReadPackageUniqueName throws where the old bare catch swallowed it.
    static string BuildUnreadablePackageSrcTree(string dirName)
    {
        var root = Directory.CreateTempSubdirectory("flowline-pkgcontent-src-").FullName;
        Directory.CreateDirectory(Path.Combine(root, "pluginpackages", dirName));
        return root;
    }

    [Fact]
    public void Status_IsAuto()
    {
        // Cross-environment id-drift false positive is fixed upstream (assemblies resolved by portable
        // name), so only a genuinely-removed assembly reaches this handler — safe for unattended auto-delete.
        Assert.Equal(HandlerStatus.Auto, _handler.Status);
    }

    [Fact]
    public async Task DetectAsync_OrphanedPluginAssembly_ReturnsDeleteWithResolvedName()
    {
        var id = Guid.NewGuid();
        SetupName("pluginassembly", "name", id, "MyPlugins.dll");

        var findings = (await _handler.DetectAsync(Ctx(), [(id, 91)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanAction.Delete, finding.Action);
        // Exact match, not Contains: this is the characterization gate — the display format must stay
        // byte-identical to OrphanCleanupService's old TypeName helper's "{label} '{detail}' ({id})" shape
        // (that helper was removed during U9's orchestrator rewrite).
        Assert.Equal($"PluginAssembly 'MyPlugins.dll' ({id})", finding.DisplayName);
    }

    [Fact]
    public async Task DetectAsync_OrphanedPluginAssemblyWithUnresolvableName_FallsBackToBareIdFormat()
    {
        // No SetupName call — the live name query returns empty, matching the unresolved-name fallback
        // branch OrphanCleanupService's old TypeName helper used ("{label} {id}", no quoted detail).
        var id = Guid.NewGuid();

        var findings = (await _handler.DetectAsync(Ctx(), [(id, 91)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal($"PluginAssembly {id}", finding.DisplayName);
    }

    [Fact]
    public async Task DetectAsync_OrphanedPluginType_ReturnsDeleteWithResolvedName()
    {
        var id = Guid.NewGuid();
        SetupName("plugintype", "typename", id, "MyNamespace.MyPlugin");

        var findings = (await _handler.DetectAsync(Ctx(), [(id, 90)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanAction.Delete, finding.Action);
        Assert.Contains("MyNamespace.MyPlugin", finding.DisplayName);
    }

    [Fact]
    public async Task DetectAsync_OrphanedStep_ReturnsDeleteWithResolvedName()
    {
        var id = Guid.NewGuid();
        SetupName("sdkmessageprocessingstep", "name", id, "MyPlugin: Create of account");

        var findings = (await _handler.DetectAsync(Ctx(), [(id, 92)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanAction.Delete, finding.Action);
        Assert.Contains("MyPlugin: Create of account", finding.DisplayName);
    }

    [Fact]
    public async Task DetectAsync_OrphanedStepImage_ReturnsDeleteWithResolvedName()
    {
        var id = Guid.NewGuid();
        SetupName("sdkmessageprocessingstepimage", "name", id, "PreImage");

        var findings = (await _handler.DetectAsync(Ctx(), [(id, 93)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanAction.Delete, finding.Action);
        Assert.Contains("PreImage", finding.DisplayName);
    }

    [Fact]
    public async Task DetectAsync_CandidateOutsideFamily_IsNotClaimed()
    {
        var findings = (await _handler.DetectAsync(Ctx(), [(Guid.NewGuid(), 61)], default)).Findings; // 61 = WebResource

        Assert.Empty(findings);
    }

    [Fact]
    public async Task DetectAsync_NoDeleteModeActive_ReturnsPrio1NotPrio2Or3()
    {
        var assemblyId = Guid.NewGuid();
        var typeId     = Guid.NewGuid();
        var stepId     = Guid.NewGuid();
        var imageId    = Guid.NewGuid();

        // Even a live-Enabled step must still classify Prio1 under NoDelete (KTD8: NoDelete is the
        // only signal knowable at classify time — takes precedence over the Enabled check).
        SetupStepStates((stepId, typeId, true));

        var findings = (await _handler.DetectAsync(
            Ctx(RunMode.NoDelete),
            [(assemblyId, 91), (typeId, 90), (stepId, 92), (imageId, 93)],
            default)).Findings;

        Assert.Equal(4, findings.Count);
        Assert.All(findings, f => Assert.Equal(OrphanPriority.Prio1, f.Priority));
    }

    [Fact]
    public async Task DetectAsync_DryRunModeActive_ReturnsPrio1NotPrio2Or3()
    {
        // U5: RunMode.DryRun gets the exact same forced-Prio1, no-live-query treatment as
        // RunMode.NoDelete — mirrors DetectAsync_NoDeleteModeActive_ReturnsPrio1NotPrio2Or3 above.
        var assemblyId = Guid.NewGuid();
        var typeId     = Guid.NewGuid();
        var stepId     = Guid.NewGuid();
        var imageId    = Guid.NewGuid();

        // Even a live-Enabled step must still classify Prio1 under DryRun (KTD2: DryRun is widened
        // alongside NoDelete — takes precedence over the Enabled check).
        SetupStepStates((stepId, typeId, true));

        var findings = (await _handler.DetectAsync(
            Ctx(RunMode.DryRun),
            [(assemblyId, 91), (typeId, 90), (stepId, 92), (imageId, 93)],
            default)).Findings;

        Assert.Equal(4, findings.Count);
        Assert.All(findings, f => Assert.Equal(OrphanPriority.Prio1, f.Priority));
    }

    [Fact]
    public async Task DetectAsync_LivePluginTypeHasOnlyDisabledSteps_ReturnsPrio3NotPrio2()
    {
        var typeId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        SetupStepStates((stepId, typeId, false));

        var findings = (await _handler.DetectAsync(Ctx(), [(typeId, 90)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanPriority.Prio3, finding.Priority);
    }

    [Fact]
    public async Task DetectAsync_LivePluginTypeHasEnabledStep_ReturnsPrio2()
    {
        var typeId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        SetupStepStates((stepId, typeId, true));

        var findings = (await _handler.DetectAsync(Ctx(), [(typeId, 90)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanPriority.Prio2, finding.Priority);
    }

    [Fact]
    public async Task DetectAsync_LiveStepIsEnabled_ReturnsPrio2()
    {
        var stepId = Guid.NewGuid();
        SetupStepStates((stepId, Guid.NewGuid(), true));

        var findings = (await _handler.DetectAsync(Ctx(), [(stepId, 92)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanPriority.Prio2, finding.Priority);
    }

    [Fact]
    public async Task DetectAsync_LiveStepIsDisabled_ReturnsPrio3()
    {
        var stepId = Guid.NewGuid();
        SetupStepStates((stepId, Guid.NewGuid(), false));

        var findings = (await _handler.DetectAsync(Ctx(), [(stepId, 92)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanPriority.Prio3, finding.Priority);
    }

    [Fact]
    public async Task DetectAsync_StepImageAndPluginAssembly_DefaultToPrio3()
    {
        // KTD8's Prio2 rule names only PluginType/Step — StepImage and PluginAssembly have no Enabled
        // concept of their own and must default to Prio3 regardless of any live step state.
        var imageId    = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();

        var findings = (await _handler.DetectAsync(Ctx(), [(imageId, 93), (assemblyId, 91)], default)).Findings;

        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.Equal(OrphanPriority.Prio3, f.Priority));
    }

    [Fact]
    public async Task DetectAsync_MixedFamilyBatch_SequenceHintOrdersStepImageBeforeStepBeforePluginTypeBeforePluginAssembly()
    {
        var assemblyId = Guid.NewGuid();
        var typeId     = Guid.NewGuid();
        var stepId     = Guid.NewGuid();
        var imageId    = Guid.NewGuid();

        var findings = (await _handler.DetectAsync(
            Ctx(),
            [(assemblyId, 91), (typeId, 90), (stepId, 92), (imageId, 93)],
            default)).Findings;

        var byType = findings.ToDictionary(f => f.ComponentType, f => f.SequenceHint);
        Assert.Equal(0, byType[93]); // StepImage
        Assert.Equal(1, byType[92]); // Step
        Assert.Equal(2, byType[90]); // PluginType
        Assert.Equal(3, byType[91]); // PluginAssembly
    }

    [Fact]
    public async Task DetectAsync_ClaimedIds_EqualsFullComponentTypeMatchedSet()
    {
        // No suppression path exists in this handler — ClaimedIds always equals every candidate that
        // survived the componenttype gate, which is exactly the set of returned Findings.
        var assemblyId = Guid.NewGuid();
        var webResourceId = Guid.NewGuid(); // 61 = WebResource, outside this family

        var result = await _handler.DetectAsync(Ctx(), [(assemblyId, 91), (webResourceId, 61)], default);

        Assert.Equal(result.Findings.Select(f => f.ObjectId).ToHashSet(), result.ClaimedIds);
        Assert.Contains(assemblyId, result.ClaimedIds);
        Assert.DoesNotContain(webResourceId, result.ClaimedIds);
    }

    // -- R10/KD3/KTD10: package-owned assembly redirects to a pluginpackage-delete finding --

    [Fact]
    public async Task DetectAsync_OrphanedPluginAssemblyWithNoPackageId_BehavesUnchanged()
    {
        // Regression guard: no packageid resolved (default RetrieveMultipleAsync stub returns empty) —
        // the finding still targets the assembly itself, exactly as before this fix.
        var id = Guid.NewGuid();
        SetupName("pluginassembly", "name", id, "MyPlugins.dll");

        var findings = (await _handler.DetectAsync(Ctx(), [(id, 91)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(id, finding.ObjectId);
        Assert.Null(finding.EntityName);
        Assert.Equal($"PluginAssembly 'MyPlugins.dll' ({id})", finding.DisplayName);
    }

    [Fact]
    public async Task DetectAsync_OrphanedPluginAssemblyWithPackageId_RedirectsToPluginPackageFinding()
    {
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        SetupPackageIds((assemblyId, packageId));

        var findings = (await _handler.DetectAsync(Ctx(), [(assemblyId, 91)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(packageId, finding.ObjectId);
        Assert.Equal("pluginpackage", finding.EntityName);
        Assert.Equal(OrphanAction.Delete, finding.Action);
        Assert.Equal(SequenceHints91, finding.SequenceHint);
    }

    [Fact]
    public async Task DetectAsync_TwoOrphanedAssembliesSamePackage_EmitsOnlyOnePackageFinding()
    {
        var assemblyId1 = Guid.NewGuid();
        var assemblyId2 = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        SetupPackageIds((assemblyId1, packageId), (assemblyId2, packageId));

        var findings = (await _handler.DetectAsync(Ctx(), [(assemblyId1, 91), (assemblyId2, 91)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(packageId, finding.ObjectId);
        Assert.Equal("pluginpackage", finding.EntityName);
    }

    [Fact]
    public async Task DetectAsync_PackageOwnedAssemblyPluginTypeHasEnabledStep_StepStillPrio2AndOrderedBeforePackageFinding()
    {
        // A package-owned assembly's plugin type still has a live, enabled step: the step keeps its
        // existing Prio2 classification (unchanged), and the family's existing SequenceHint ordering
        // still places it (Step=1) before the redirected package-delete finding (PluginAssembly slot=3).
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        SetupPackageIds((assemblyId, packageId));
        SetupStepStates((stepId, typeId, true));

        var findings = (await _handler.DetectAsync(
            Ctx(),
            [(assemblyId, 91), (typeId, 90), (stepId, 92)],
            default)).Findings;

        Assert.Equal(3, findings.Count);

        var stepFinding = Assert.Single(findings, f => f.ComponentType == 92);
        Assert.Equal(OrphanPriority.Prio2, stepFinding.Priority);

        var packageFinding = Assert.Single(findings, f => f.EntityName == "pluginpackage");
        Assert.Equal(packageId, packageFinding.ObjectId);
        Assert.True(stepFinding.SequenceHint < packageFinding.SequenceHint);
    }

    [Fact]
    public async Task DetectAsync_PackageIdLookupFaultException_DegradesToUnredirectedAssemblyFinding()
    {
        // A transient live-query fault degrades to today's un-redirected assembly-delete finding for
        // that candidate, rather than aborting detection for the whole family.
        var assemblyId = Guid.NewGuid();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly" && q.ColumnSet.Columns.Contains("packageid"))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new FaultException<OrganizationServiceFault>(new OrganizationServiceFault())));

        var findings = (await _handler.DetectAsync(Ctx(), [(assemblyId, 91)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(assemblyId, finding.ObjectId);
        Assert.Null(finding.EntityName);
        Assert.DoesNotContain("Warning", _console.Output);
    }

    [Fact]
    public async Task DetectAsync_PackageIdLookupInfrastructureException_WarnsAndDegradesToUnredirectedFinding()
    {
        var assemblyId = Guid.NewGuid();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly" && q.ColumnSet.Columns.Contains("packageid"))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new InvalidOperationException("network timeout")));

        var findings = (await _handler.DetectAsync(Ctx(), [(assemblyId, 91)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(assemblyId, finding.ObjectId);
        Assert.Null(finding.EntityName);
        Assert.Contains("network timeout", _console.Output);
        Assert.Contains("! ", _console.Output);
    }

    const int SequenceHints91 = 3; // PluginAssembly's SequenceHint slot — the redirected package finding keeps it.

    // -- Gap fix: CustomApi (and its own children) bound to a redirected assembly's plugin types must be
    // pulled into this family's own ordering, ahead of the package delete — see
    // ResolvePackageChildCleanupFindingsAsync's class-level comment. --

    [Fact]
    public async Task DetectAsync_PackageOwnedAssemblyPluginTypeHasBoundCustomApi_CustomApiFindingOrderedBeforePackage()
    {
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        const int customApiComponentType = 10101; // env-specific — arbitrary stand-in, value itself is never inspected

        SetupPackageIds((assemblyId, packageId));
        SetupChildIds("plugintype", "pluginassemblyid", typeId);
        SetupChildIds("customapi", "plugintypeid", apiId);

        var findings = (await _handler.DetectAsync(
            Ctx(),
            [(assemblyId, 91), (apiId, customApiComponentType)],
            default)).Findings;

        var apiFinding = Assert.Single(findings, f => f.EntityName == "customapi");
        Assert.Equal(apiId, apiFinding.ObjectId);
        Assert.Equal(customApiComponentType, apiFinding.ComponentType);
        Assert.Equal(OrphanAction.Delete, apiFinding.Action);
        Assert.Equal(OrphanPriority.Prio2, apiFinding.Priority);

        var packageFinding = Assert.Single(findings, f => f.EntityName == "pluginpackage");
        Assert.True(apiFinding.SequenceHint < packageFinding.SequenceHint);
    }

    [Fact]
    public async Task DetectAsync_BoundCustomApiHasRequestParamAndResponseProp_ChildrenOrderedBeforeCustomApi()
    {
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        var paramId = Guid.NewGuid();
        var propId = Guid.NewGuid();

        SetupPackageIds((assemblyId, packageId));
        SetupChildIds("plugintype", "pluginassemblyid", typeId);
        SetupChildIds("customapi", "plugintypeid", apiId);
        SetupChildIds("customapirequestparameter", "customapiid", paramId);
        SetupChildIds("customapiresponseproperty", "customapiid", propId);

        var findings = (await _handler.DetectAsync(
            Ctx(),
            [(assemblyId, 91), (apiId, 10101), (paramId, 10102), (propId, 10103)],
            default)).Findings;

        var apiFinding = Assert.Single(findings, f => f.EntityName == "customapi");
        var paramFinding = Assert.Single(findings, f => f.EntityName == "customapirequestparameter");
        var propFinding = Assert.Single(findings, f => f.EntityName == "customapiresponseproperty");

        Assert.True(paramFinding.SequenceHint < apiFinding.SequenceHint);
        Assert.True(propFinding.SequenceHint < apiFinding.SequenceHint);
    }

    [Fact]
    public async Task DetectAsync_BoundCustomApiNotInCandidateBatch_LeftAloneNotDeleted()
    {
        // The live lookup resolves a CustomApi for the redirected assembly's plugin type, but that
        // CustomApi id was never handed in as an orphan candidate this run — it's still validly declared
        // locally, so this handler must not propose deleting it.
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var apiId = Guid.NewGuid();

        SetupPackageIds((assemblyId, packageId));
        SetupChildIds("plugintype", "pluginassemblyid", typeId);
        SetupChildIds("customapi", "plugintypeid", apiId);

        var result = await _handler.DetectAsync(Ctx(), [(assemblyId, 91)], default);

        Assert.DoesNotContain(result.Findings, f => f.EntityName == "customapi");
        Assert.DoesNotContain(apiId, result.ClaimedIds);
    }

    [Fact]
    public async Task DetectAsync_NoDeleteModeActive_CustomApiCleanupReportsPrio1()
    {
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var apiId = Guid.NewGuid();

        SetupPackageIds((assemblyId, packageId));
        SetupChildIds("plugintype", "pluginassemblyid", typeId);
        SetupChildIds("customapi", "plugintypeid", apiId);

        var findings = (await _handler.DetectAsync(
            Ctx(RunMode.NoDelete),
            [(assemblyId, 91), (apiId, 10101)],
            default)).Findings;

        var apiFinding = Assert.Single(findings, f => f.EntityName == "customapi");
        Assert.Equal(OrphanPriority.Prio1, apiFinding.Priority);
    }

    // -- Code-review fix: redirect-path CustomApi claiming must honor the same uniquename-recreation
    // safety check CustomApiFamilyHandler already enforces, since this handler claims the id first and
    // CustomApiFamilyHandler's own pass never gets a chance to apply its check to it. --

    [Fact]
    public async Task DetectAsync_BoundCustomApiStillLocallyDeclaredUnderNewId_LeftAloneNotDeleted()
    {
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        const string apiName = "EchoValue";

        var srcRoot = CreateLocalCustomApiSource(apiName);
        try
        {
            SetupPackageIds((assemblyId, packageId));
            SetupChildIds("plugintype", "pluginassemblyid", typeId);
            SetupChildIds("customapi", "plugintypeid", apiId);
            SetupName("customapi", "name", apiId, apiName);

            var result = await _handler.DetectAsync(Ctx(srcRoot), [(assemblyId, 91), (apiId, 10101)], default);

            Assert.DoesNotContain(result.Findings, f => f.EntityName == "customapi");
            Assert.DoesNotContain(apiId, result.ClaimedIds);
        }
        finally
        {
            Directory.Delete(srcRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DetectAsync_BoundCustomApiRecreatedUnderNewIdNotLocallyDeclared_StillDeleted()
    {
        // Regression guard: the local-declaration check must not become an unconditional skip — a
        // CustomApi that's genuinely gone from local source is still correctly claimed and deleted.
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var apiId = Guid.NewGuid();

        var srcRoot = CreateLocalCustomApiSource("SomeOtherApi");
        try
        {
            SetupPackageIds((assemblyId, packageId));
            SetupChildIds("plugintype", "pluginassemblyid", typeId);
            SetupChildIds("customapi", "plugintypeid", apiId);
            SetupName("customapi", "name", apiId, "EchoValue");

            var result = await _handler.DetectAsync(Ctx(srcRoot), [(assemblyId, 91), (apiId, 10101)], default);

            Assert.Contains(result.Findings, f => f.EntityName == "customapi" && f.ObjectId == apiId);
            Assert.Contains(apiId, result.ClaimedIds);
        }
        finally
        {
            Directory.Delete(srcRoot, recursive: true);
        }
    }

    // -- Code-review fix: a degraded child-cleanup lookup must not leave the redirected package-delete
    // finding as if cleanup were confirmed complete. --

    [Fact]
    public async Task DetectAsync_ChildCleanupQueryFaults_SkipsRedirectedPackageFindingThisRun()
    {
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        SetupPackageIds((assemblyId, packageId));
        SetupChildIds("plugintype", "pluginassemblyid", typeId);
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "customapi" && q.Criteria.Conditions.Any(c => c.AttributeName == "plugintypeid"))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new FaultException<OrganizationServiceFault>(new OrganizationServiceFault())));

        var result = await _handler.DetectAsync(Ctx(), [(assemblyId, 91)], default);

        // No finding at all for this assembly this run — neither the redirected package-delete finding
        // nor the un-redirected fallback (which KD3 confirms always fails for a package-owned assembly).
        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task DetectAsync_ChildCleanupQueryFaults_NoDeleteModeAlsoSkipsRedirectedFinding()
    {
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        SetupPackageIds((assemblyId, packageId));
        SetupChildIds("plugintype", "pluginassemblyid", typeId);
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "customapi" && q.Criteria.Conditions.Any(c => c.AttributeName == "plugintypeid"))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new FaultException<OrganizationServiceFault>(new OrganizationServiceFault())));

        var result = await _handler.DetectAsync(Ctx(RunMode.NoDelete), [(assemblyId, 91)], default);

        Assert.Empty(result.Findings);
    }

    [Fact]
    public async Task DetectAsync_ChildCleanupQueryFaultIsGenericException_AlsoSkipsRedirectedFinding()
    {
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        SetupPackageIds((assemblyId, packageId));
        SetupChildIds("plugintype", "pluginassemblyid", typeId);
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "customapi" && q.Criteria.Conditions.Any(c => c.AttributeName == "plugintypeid"))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new InvalidOperationException("network timeout")));

        var result = await _handler.DetectAsync(Ctx(), [(assemblyId, 91)], default);

        Assert.Empty(result.Findings);
        Assert.Contains("network timeout", _console.Output);
        Assert.Contains("! ", _console.Output);
    }

    [Fact]
    public async Task DetectAsync_ChildCleanupSucceedsNormally_RedirectedFindingStillFires()
    {
        // Regression guard: the degradation tracking above must not become an unconditional skip — a
        // clean, non-degraded run still produces the redirected package-delete finding as before.
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var typeId = Guid.NewGuid();

        SetupPackageIds((assemblyId, packageId));
        SetupChildIds("plugintype", "pluginassemblyid", typeId);

        var result = await _handler.DetectAsync(Ctx(), [(assemblyId, 91)], default);

        Assert.Contains(result.Findings, f => f.EntityName == "pluginpackage" && f.ObjectId == packageId);
    }

    // -- Code-review fix: QueryChildIdsAsync's ConditionOperator.In batch must guard the same 2000-id
    // ceiling EntityNameLookup already centralizes, rather than letting an oversized batch surface as an
    // unhandled query fault. --

    [Fact]
    public async Task DetectAsync_MoreThan2000RedirectedAssemblies_DegradesGracefullyInsteadOfThrowing()
    {
        var assemblies = Enumerable.Range(0, 2001)
            .Select(_ => (AssemblyId: Guid.NewGuid(), PackageId: Guid.NewGuid()))
            .ToArray();
        SetupPackageIds(assemblies);

        var candidates = assemblies.Select(a => (a.AssemblyId, 91)).ToList();

        var result = await _handler.DetectAsync(Ctx(), candidates, default);

        // Degrades the same way any other plugintype-lookup fault does — no unhandled exception, and no
        // redirected package-delete finding built on unconfirmed cleanup.
        Assert.DoesNotContain(result.Findings, f => f.EntityName == "pluginpackage");
    }

    // -- U5/R11/R12/KTD7: package content, not the manifest, decides orphan candidacy for a package-owned
    // assembly. A live pluginassembly whose packageid resolves to a package the imported solution still
    // carries, and whose name is among that package's reflected content, is excluded before any finding
    // is built — see PluginAssemblyFamilyHandler.ResolvePackageContentExclusionsAsync. --

    [Fact]
    public async Task DetectAsync_PackageOwnedAssemblyContentPresentButManifestSilent_NotOrphanNoFinding()
    {
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        const string uniqueName = "abc_TestPackage";
        const string assemblyName = "Cr07982.Backend";

        var buildDir = Directory.CreateTempSubdirectory("flowline-pkgcontent-build-").FullName;
        var dll = BuildPluginDll(buildDir, assemblyName, "Cr07982.Backend.MyPlugin");
        var nupkg = BuildNupkg(buildDir, dll);
        var srcRoot = BuildPackageSrcTree(uniqueName, nupkg);

        try
        {
            SetupPackageIds((assemblyId, packageId));
            SetupName("pluginassembly", "name", assemblyId, assemblyName);
            SetupPackageUniqueName(packageId, uniqueName);

            var result = await _handler.DetectAsync(Ctx(srcRoot), [(assemblyId, 91)], default);

            Assert.Empty(result.Findings);
            // Recognized-but-clean, not unclaimed — DispatchToHandlersAsync must not route this to the
            // generic-fallback preview as if no handler had evaluated it.
            Assert.Contains(assemblyId, result.ClaimedIds);
        }
        finally
        {
            Directory.Delete(buildDir, recursive: true);
            Directory.Delete(srcRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DetectAsync_PackageOwnedAssemblyNotInPackageContent_StillOrphanRedirectIntact()
    {
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        const string uniqueName = "abc_TestPackage";

        var buildDir = Directory.CreateTempSubdirectory("flowline-pkgcontent-build-").FullName;
        // Package content reflects a different assembly than the live candidate — content doesn't carry
        // the candidate's name, so the exclusion must not apply.
        var dll = BuildPluginDll(buildDir, "SomeOtherAssembly", "SomeOtherAssembly.MyPlugin");
        var nupkg = BuildNupkg(buildDir, dll);
        var srcRoot = BuildPackageSrcTree(uniqueName, nupkg);

        try
        {
            SetupPackageIds((assemblyId, packageId));
            SetupName("pluginassembly", "name", assemblyId, "Cr07982.Backend");
            SetupPackageUniqueName(packageId, uniqueName);

            var findings = (await _handler.DetectAsync(Ctx(srcRoot), [(assemblyId, 91)], default)).Findings;

            var finding = Assert.Single(findings);
            Assert.Equal(packageId, finding.ObjectId);
            Assert.Equal("pluginpackage", finding.EntityName);
        }
        finally
        {
            Directory.Delete(buildDir, recursive: true);
            Directory.Delete(srcRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DetectAsync_ImportedSolutionCarriesNoPackageDirectoryAtAll_PackageDeleteStillFiresUnchanged()
    {
        // R12: the imported solution no longer carries this package's directory at all (Ctx()'s
        // "unused-package-src-root" has no pluginpackages/ tree) — no reflected content exists for it, so
        // the content-based exclusion never applies and the existing redirect-to-package-delete path runs
        // exactly as it did before this fix.
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();

        SetupPackageIds((assemblyId, packageId));
        SetupName("pluginassembly", "name", assemblyId, "Cr07982.Backend");
        SetupPackageUniqueName(packageId, "abc_TestPackage");

        var findings = (await _handler.DetectAsync(Ctx(), [(assemblyId, 91)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(packageId, finding.ObjectId);
        Assert.Equal("pluginpackage", finding.EntityName);
    }

    // -- Fix 1: the exclusion guard must fail CLOSED. An empty exclusion set reads identically to
    // "nothing to exclude", and un-excluded means the package-scope delete proceeds — on state this
    // handler never verified. Each of the three inputs that can silently empty the set gets its own
    // case below. --

    [Fact]
    public async Task DetectAsync_PackageUniqueNameLookupFails_PackageDeleteSkippedThisRun()
    {
        // A transient fault resolving packageid -> uniquename leaves the candidate unmatchable against
        // the content this package still carries. Emitting the package delete anyway would destroy a
        // package whose content was never checked, so the whole redirect is deferred to a later run.
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        const string uniqueName = "abc_TestPackage";

        var buildDir = Directory.CreateTempSubdirectory("flowline-pkgcontent-build-").FullName;
        var dll = BuildPluginDll(buildDir, "Cr07982.Backend", "Cr07982.Backend.MyPlugin");
        var srcRoot = BuildPackageSrcTree(uniqueName, BuildNupkg(buildDir, dll));

        try
        {
            SetupPackageIds((assemblyId, packageId));
            SetupName("pluginassembly", "name", assemblyId, "Cr07982.Backend");

            _serviceMock.RetrieveMultipleAsync(
                    Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginpackage" && q.ColumnSet.Columns.Contains("uniquename"))),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromException<EntityCollection>(new InvalidOperationException("network timeout")));

            var findings = (await _handler.DetectAsync(Ctx(srcRoot), [(assemblyId, 91)], default)).Findings;

            Assert.DoesNotContain(findings, f => f.EntityName == "pluginpackage");
            Assert.Contains("network timeout", _console.Output);
        }
        finally
        {
            Directory.Delete(buildDir, recursive: true);
            Directory.Delete(srcRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DetectAsync_PackageDirectoryUnreadable_PackageDeleteSkippedThisRunAndWarns()
    {
        // The bare catch this replaces made "couldn't reflect this package" indistinguishable from
        // "this package carries nothing" — and the second one deletes.
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        const string uniqueName = "abc_TestPackage";

        var srcRoot = BuildUnreadablePackageSrcTree(uniqueName);
        try
        {
            SetupPackageIds((assemblyId, packageId));
            SetupName("pluginassembly", "name", assemblyId, "Cr07982.Backend");
            SetupPackageUniqueName(packageId, uniqueName);

            var findings = (await _handler.DetectAsync(Ctx(srcRoot), [(assemblyId, 91)], default)).Findings;

            Assert.DoesNotContain(findings, f => f.EntityName == "pluginpackage");
            Assert.Contains(uniqueName, _console.Output);
            Assert.Contains("couldn't be read", _console.Output);
        }
        finally
        {
            Directory.Delete(srcRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DetectAsync_AssemblyNameUnresolved_PackageDeleteSkippedThisRun()
    {
        // No live name means no key to test against the package's reflected content. Dropping the
        // candidate silently used to leave the package delete standing on an unchecked assembly.
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        const string uniqueName = "abc_TestPackage";

        var buildDir = Directory.CreateTempSubdirectory("flowline-pkgcontent-build-").FullName;
        var dll = BuildPluginDll(buildDir, "Cr07982.Backend", "Cr07982.Backend.MyPlugin");
        var srcRoot = BuildPackageSrcTree(uniqueName, BuildNupkg(buildDir, dll));

        try
        {
            SetupPackageIds((assemblyId, packageId));
            SetupPackageUniqueName(packageId, uniqueName);
            // Deliberately no SetupName — the pluginassembly name query returns empty.

            var findings = (await _handler.DetectAsync(Ctx(srcRoot), [(assemblyId, 91)], default)).Findings;

            Assert.DoesNotContain(findings, f => f.EntityName == "pluginpackage");
        }
        finally
        {
            Directory.Delete(buildDir, recursive: true);
            Directory.Delete(srcRoot, recursive: true);
        }
    }

    // -- Fix 2: one package-scope delete per parent package means multiple orphaned assemblies collapse
    // into it, so a package holding one content-carried assembly and one genuinely orphaned sibling used
    // to be deleted whole. An orphaned assembly is harmless if nothing calls it — steps and Custom APIs
    // are the execution surface, so the sibling's surface is cleared and the package survives. --

    [Fact]
    public async Task DetectAsync_MixedPackage_PackageSurvivesWhileOrphanedSiblingSurfaceIsCleared()
    {
        var carriedAssemblyId = Guid.NewGuid();   // A — package content still carries its DLL
        var orphanedAssemblyId = Guid.NewGuid();  // B — content doesn't carry it
        var packageId = Guid.NewGuid();
        var orphanedTypeId = Guid.NewGuid();
        var orphanedStepId = Guid.NewGuid();
        const string uniqueName = "abc_TestPackage";

        var buildDir = Directory.CreateTempSubdirectory("flowline-pkgcontent-build-").FullName;
        var dll = BuildPluginDll(buildDir, "Alpha", "Alpha.MyPlugin");
        var srcRoot = BuildPackageSrcTree(uniqueName, BuildNupkg(buildDir, dll));

        try
        {
            SetupPackageIds((carriedAssemblyId, packageId), (orphanedAssemblyId, packageId));
            SetupNames("pluginassembly", "name", (carriedAssemblyId, "Alpha"), (orphanedAssemblyId, "Beta"));
            SetupPackageUniqueName(packageId, uniqueName);

            var findings = (await _handler.DetectAsync(Ctx(srcRoot),
                [(carriedAssemblyId, 91), (orphanedAssemblyId, 91), (orphanedTypeId, 90), (orphanedStepId, 92)],
                default)).Findings;

            // The package, and with it A, survives.
            Assert.DoesNotContain(findings, f => f.EntityName == "pluginpackage");
            Assert.DoesNotContain(findings, f => f.ObjectId == carriedAssemblyId);
            // B keeps no delete of its own either — a package-owned pluginassembly can't be deleted
            // directly, which is what the redirect existed for. It stays as inert storage.
            Assert.DoesNotContain(findings, f => f.ObjectId == orphanedAssemblyId);
            // ...but nothing in it runs any more: its execution surface is still deleted.
            Assert.Contains(findings, f => f.ObjectId == orphanedTypeId);
            Assert.Contains(findings, f => f.ObjectId == orphanedStepId);
        }
        finally
        {
            Directory.Delete(buildDir, recursive: true);
            Directory.Delete(srcRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DetectAsync_ProtectedPackageAlongsideFullyGonePackage_OnlyTheGoneOneIsDeleted()
    {
        // R12 regression guard against an over-broad Fix 2: suppression is per package, so a package the
        // imported solution no longer carries at all is still deleted in the same run. This run must not
        // degrade — a degraded run suppresses both, and would pass for the wrong reason.
        var carriedAssemblyId = Guid.NewGuid();
        var goneAssemblyId = Guid.NewGuid();
        var carriedPackageId = Guid.NewGuid();
        var gonePackageId = Guid.NewGuid();

        var buildDir = Directory.CreateTempSubdirectory("flowline-pkgcontent-build-").FullName;
        var dll = BuildPluginDll(buildDir, "Alpha", "Alpha.MyPlugin");
        var srcRoot = BuildPackageSrcTree("abc_CarriedPackage", BuildNupkg(buildDir, dll));

        try
        {
            SetupPackageIds((carriedAssemblyId, carriedPackageId), (goneAssemblyId, gonePackageId));
            SetupNames("pluginassembly", "name", (carriedAssemblyId, "Alpha"), (goneAssemblyId, "Gamma"));
            SetupPackageUniqueNames((carriedPackageId, "abc_CarriedPackage"), (gonePackageId, "abc_GonePackage"));

            var findings = (await _handler.DetectAsync(Ctx(srcRoot),
                [(carriedAssemblyId, 91), (goneAssemblyId, 91)], default)).Findings;

            var packageFinding = Assert.Single(findings, f => f.EntityName == "pluginpackage");
            Assert.Equal(gonePackageId, packageFinding.ObjectId);
        }
        finally
        {
            Directory.Delete(buildDir, recursive: true);
            Directory.Delete(srcRoot, recursive: true);
        }
    }

    // -- Fix 3: the exclusion has to cover the excluded assembly's plugin types, or the delete strips the
    // only thing that makes the assembly run. Steps stay deletable by explicit decision: a step absent
    // from source was removed on purpose, and registered-but-inert is the intended end state. --

    [Fact]
    public async Task DetectAsync_ContentCarriedAssembly_PluginTypesProtectedButStepsStillDeleted()
    {
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        const string uniqueName = "abc_TestPackage";

        var buildDir = Directory.CreateTempSubdirectory("flowline-pkgcontent-build-").FullName;
        var dll = BuildPluginDll(buildDir, "Alpha", "Alpha.MyPlugin");
        var srcRoot = BuildPackageSrcTree(uniqueName, BuildNupkg(buildDir, dll));

        try
        {
            SetupPackageIds((assemblyId, packageId));
            SetupName("pluginassembly", "name", assemblyId, "Alpha");
            SetupPackageUniqueName(packageId, uniqueName);
            SetupPluginTypesForAssembly(assemblyId, typeId);

            var result = await _handler.DetectAsync(Ctx(srcRoot),
                [(assemblyId, 91), (typeId, 90), (stepId, 92)], default);

            Assert.DoesNotContain(result.Findings, f => f.ObjectId == typeId);
            Assert.Contains(result.Findings, f => f.ObjectId == stepId);
            Assert.DoesNotContain(result.Findings, f => f.EntityName == "pluginpackage");
            // Recognized-but-clean, so DispatchToHandlersAsync's fallback doesn't re-flag the type.
            Assert.Contains(typeId, result.ClaimedIds);
        }
        finally
        {
            Directory.Delete(buildDir, recursive: true);
            Directory.Delete(srcRoot, recursive: true);
        }
    }

    [Fact]
    public async Task DetectAsync_ProtectedPluginTypeLookupFails_NoPluginTypeIsDeletedThisRun()
    {
        // Without the type list there's no way to tell a protected assembly's types from any other
        // candidate's, so the batch's plugin type deletes wait for a run that can.
        var assemblyId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        const string uniqueName = "abc_TestPackage";

        var buildDir = Directory.CreateTempSubdirectory("flowline-pkgcontent-build-").FullName;
        var dll = BuildPluginDll(buildDir, "Alpha", "Alpha.MyPlugin");
        var srcRoot = BuildPackageSrcTree(uniqueName, BuildNupkg(buildDir, dll));

        try
        {
            SetupPackageIds((assemblyId, packageId));
            SetupName("pluginassembly", "name", assemblyId, "Alpha");
            SetupPackageUniqueName(packageId, uniqueName);
            FailPluginTypesForAssembly(assemblyId);

            var findings = (await _handler.DetectAsync(Ctx(srcRoot),
                [(assemblyId, 91), (typeId, 90), (stepId, 92)], default)).Findings;

            Assert.DoesNotContain(findings, f => f.ObjectId == typeId);
            Assert.Contains(findings, f => f.ObjectId == stepId);
            Assert.Contains("plugintype lookup timeout", _console.Output);
        }
        finally
        {
            Directory.Delete(buildDir, recursive: true);
            Directory.Delete(srcRoot, recursive: true);
        }
    }
}
