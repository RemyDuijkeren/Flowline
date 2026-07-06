using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Flowline.Core.Services;
using Flowline.Core;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests;

public class OrphanCleanupServiceTests : IDisposable
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly TestConsole _console;
    readonly OrphanCleanupService _service;
    readonly string _packageSrcRoot;
    readonly string _webResourcesDir;

    public OrphanCleanupServiceTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _console.Profile.Width = 400; // avoid word-wrap splitting longer assertion substrings across lines
        _service = new OrphanCleanupService(_console, new FlowlineRuntimeOptions());
        _packageSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _webResourcesDir = Path.Combine(_packageSrcRoot, "WebResources");
        Directory.CreateDirectory(_webResourcesDir);

        // Default: any unconfigured RetrieveMultipleAsync (e.g. bulk name-resolution queries) returns
        // empty rather than NSubstitute's null default — real Dataverse never returns a null EntityCollection.
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

        // Default: no cross-solution membership
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "solutioncomponent" && q.LinkEntities.Count > 0),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_packageSrcRoot))
            Directory.Delete(_packageSrcRoot, true);
    }

    PostDeployContext Ctx(
        string solutionName,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> localComponents,
        RunMode mode = RunMode.Normal,
        IReadOnlyList<string>? entityLogicalNames = null,
        string? packageSrcRoot = null,
        IReadOnlyList<(int ComponentType, string SchemaName)>? namedComponents = null) =>
        new(_serviceMock, solutionName, localComponents, mode, "solution.zip", "https://example.crm.dynamics.com",
            entityLogicalNames ?? [], packageSrcRoot ?? Path.Combine(Path.GetTempPath(), $"flowline-test-nonexistent-{Guid.NewGuid():N}"),
            namedComponents ?? []);

    void SetupSolutionComponents(string solutionName, params (Guid Id, int ComponentType)[] components)
    {
        var entities = components.Select(c => new Entity("solutioncomponent")
        {
            ["objectid"] = c.Id,
            ["componenttype"] = new OptionSetValue(c.ComponentType)
        }).ToList();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q =>
                    q.EntityName == "solutioncomponent" &&
                    q.LinkEntities.Any(le => le.LinkToEntityName == "solution")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }

    void SetupWebResourceNames(params (Guid Id, string Name)[] webResources)
    {
        var entities = webResources.Select(wr => new Entity("webresource", wr.Id)
        {
            ["name"] = wr.Name
        }).ToList();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "webresource"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }

    [Fact]
    public async Task RunPreImportAsync_AnnotationReferencedWebResource_NotDeleted()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61));
        SetupWebResourceNames((orphanId, "av_ext/shared.js"));
        File.WriteAllText(Path.Combine(_webResourcesDir, "form.js"),
            "// flowline:depends av_ext/shared.js\nconsole.log('hi');");

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)], packageSrcRoot: _packageSrcRoot), default);

        await _serviceMock.DidNotReceive().DeleteAsync("webresource", orphanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPreImportAsync_AnnotationReferencedWebResource_SkipMessageEmitted()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61));
        SetupWebResourceNames((orphanId, "av_ext/shared.js"));
        File.WriteAllText(Path.Combine(_webResourcesDir, "form.js"),
            "// flowline:depends av_ext/shared.js\ncode();");

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)], packageSrcRoot: _packageSrcRoot), default);

        Assert.Contains("av_ext/shared.js", _console.Output);
        Assert.Contains("preserved", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_NotAnnotationReferenced_NormalOrphanHandling()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61));
        SetupWebResourceNames((orphanId, "av_ext/unref.js"));
        // No annotations referencing unref.js
        File.WriteAllText(Path.Combine(_webResourcesDir, "form.js"), "// no deps\ncode();");

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)], packageSrcRoot: _packageSrcRoot), default);

        await _serviceMock.Received(1).DeleteAsync("webresource", orphanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPreImportAsync_NoAnnotations_NoExemptions()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61));
        SetupWebResourceNames((orphanId, "av_ext/lib.js"));
        File.WriteAllText(Path.Combine(_webResourcesDir, "form.js"), "code(); // no annotations");

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)], packageSrcRoot: _packageSrcRoot), default);

        await _serviceMock.Received(1).DeleteAsync("webresource", orphanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPreImportAsync_SameRefInMultipleFiles_DedupedSingleExemption()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61));
        SetupWebResourceNames((orphanId, "av_ext/shared.js"));
        File.WriteAllText(Path.Combine(_webResourcesDir, "a.js"),
            "// flowline:depends av_ext/shared.js\ncode();");
        File.WriteAllText(Path.Combine(_webResourcesDir, "b.js"),
            "// flowline:depends av_ext/shared.js\ncode();");

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)], packageSrcRoot: _packageSrcRoot), default);

        await _serviceMock.DidNotReceive().DeleteAsync("webresource", orphanId, Arg.Any<CancellationToken>());
    }

    // -- Default-solution membership must not block a real delete --

    void SetupCrossSolutionMembership(Guid orphanId, params string[] solutions)
    {
        var entities = solutions.Select(s => new Entity("solutioncomponent")
        {
            ["objectid"] = orphanId,
            ["sol.uniquename"] = new AliasedValue("solution", "uniquename", s)
        }).ToList();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "solutioncomponent" && q.Criteria.Conditions.Any(c => c.AttributeName == "objectid")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }

    [Fact]
    public async Task RunPreImportAsync_OrphanOnlyInDefaultSolution_DeletesInsteadOfRemoving()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("Cr07982", (orphanId, 91)); // 91 = PluginAssembly
        SetupCrossSolutionMembership(orphanId, "Default");

        await _service.RunPreImportAsync(Ctx("Cr07982", [(Guid.NewGuid(), 0)]), default);

        await _serviceMock.Received(1).DeleteAsync("pluginassembly", orphanId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Is<OrganizationRequest>(r => r.RequestName == "RemoveSolutionComponent"), Arg.Any<CancellationToken>());
    }

    // -- Auto-delete/CustomApi naming: show what's actually being deleted, not just a GUID --

    [Fact]
    public async Task RunPreImportAsync_WebResourceOrphan_DeleteEntryShowsResolvedName()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61)); // 61 = WebResource
        SetupWebResourceNames((orphanId, "av_ext/old.js"));

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains($"WebResource 'av_ext/old.js' ({orphanId})", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_CustomApiOrphan_DeleteEntryShowsResolvedName()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10036)); // env-specific CustomApi componenttype

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "customapi"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("customapi", orphanId) { ["name"] = "av_OldCustomApi" }
            ])));

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains($"CustomApi 'av_OldCustomApi' ({orphanId})", _console.Output);
    }

    // -- Cross-environment id drift: schemaName-recorded RootComponents and CustomApi's GUID-less source --

    [Fact]
    public async Task RunPreImportAsync_WebResourceNamedComponent_ResolvesLiveIdByName_NotReportedAsOrphan()
    {
        // WebResource RootComponents in Solution.xml are recorded by schemaName, not id (pac never emits
        // an id for them — see ComponentClassifier.ParseSolutionXmlComponents). Previously this meant the
        // webresource's identity was never captured at all, so it always looked orphaned regardless of
        // whether the live id matched — reproduces the reported false positive for every webresource.
        var liveId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (liveId, 61)); // 61 = WebResource
        SetupWebResourceNames((liveId, "av_Cr07982/example1.js"));

        await _service.RunPreImportAsync(
            Ctx("MySolution", [(Guid.NewGuid(), 0)], namedComponents: [(61, "av_Cr07982/example1.js")]), default);

        Assert.DoesNotContain(liveId.ToString(), _console.Output);
        Assert.Contains("No orphan components", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_CustomApiStillInLocalSource_NotReportedAsOrphan()
    {
        // CustomApi source (Package/src/customapis/<uniquename>/customapi.xml) has no GUID at all —
        // uniquename is the only local identity. A CustomApi recreated with a new customapiid still
        // has the same uniquename in source, so it must not be reported as an orphan.
        var liveId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (liveId, 10036)); // env-specific CustomApi componenttype

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "customapi"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("customapi", liveId) { ["name"] = "av_AatYourService" }
            ])));

        var packageSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(packageSrcRoot, "customapis", "av_AatYourService"));

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], packageSrcRoot: packageSrcRoot), default);

            // Unlike WebResource, CustomApi's componenttype isn't AutoDelete-classified, so this doesn't
            // hit the early "No orphan components" return — it clears the entity-side detection and gets
            // filtered out of customApiOrphans, leaving an empty report instead.
            Assert.DoesNotContain(liveId.ToString(), _console.Output);
            Assert.Contains("0 to delete, 0 to remove from solution, 0 manual", _console.Output);
        }
        finally
        {
            Directory.Delete(packageSrcRoot, true);
        }
    }

    [Fact]
    public async Task RunPreImportAsync_OrphanInAnotherRealSolution_RemovesFromSolutionOnly()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("Cr07982", (orphanId, 91));
        SetupCrossSolutionMembership(orphanId, "Default", "SharedSolution");

        await _service.RunPreImportAsync(Ctx("Cr07982", [(Guid.NewGuid(), 0)]), default);

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<OrganizationRequest>(r => r.RequestName == "RemoveSolutionComponent"), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", orphanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPreImportAsync_NoWebResourcesDirUnderPackageSrc_NoExemptionCheck()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61));
        // No Package/src/WebResources dir at the default (nonexistent) packageSrcRoot — if exemption
        // ran, it would try to query and the mock would return an empty collection, potentially still
        // deleting. The point is: no WebResources dir → no name query, normal orphan flow.

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        // With no name query setup, it falls through to delete the orphan
        await _serviceMock.Received(1).DeleteAsync("webresource", orphanId, Arg.Any<CancellationToken>());
    }

    // -- False-positive guards: system components and schemaName-only entity roots --

    [Fact]
    public async Task RunPreImportAsync_WellKnownSystemComponent_NeverReportedAsOrphan()
    {
        var systemViewId = Guid.Parse("00000000-0000-0000-00aa-000010001001");
        SetupSolutionComponents("MySolution", (systemViewId, 26)); // 26 = SavedQuery (View)

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.DoesNotContain(systemViewId.ToString(), _console.Output);
        Assert.Contains("No orphan components", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_EntityLogicalNameResolvesToPresentMetadataId_NotReportedAsOrphan()
    {
        var entityMetadataId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (entityMetadataId, 1)); // 1 = Entity

        var metadata = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata { LogicalName = "account" };
        typeof(Microsoft.Xrm.Sdk.Metadata.EntityMetadata).GetProperty("MetadataId")!.SetValue(metadata, entityMetadataId);
        var response = new Microsoft.Xrm.Sdk.Messages.RetrieveEntityResponse
        {
            Results = new Microsoft.Xrm.Sdk.ParameterCollection { ["EntityMetadata"] = metadata }
        };

        _serviceMock.ExecuteAsync(
                Arg.Is<OrganizationRequest>(r => r.RequestName == "RetrieveEntity" && (string)r.Parameters["LogicalName"] == "account"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(response));

        await _service.RunPreImportAsync(
            Ctx("MySolution", [(Guid.NewGuid(), 0)], entityLogicalNames: ["account"]), default);

        Assert.DoesNotContain(entityMetadataId.ToString(), _console.Output);
        Assert.Contains("No orphan components", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_EntityGenuinelyRemoved_ReportedAsManualEntity()
    {
        // No EntityLogicalNames given at all — nothing to resolve live, so this entity is genuinely
        // gone from Solution.xml, not a schemaName-resolution gap. Entity(1) is a SupportedManualTypes
        // member, so it must still surface in the report (unlike connectionreference/bot).
        var entityMetadataId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (entityMetadataId, 1)); // 1 = Entity

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains($"Entity {entityMetadataId}", _console.Output);
        Assert.Contains("remove manually via maker portal", _console.Output);
    }

    // -- Manual orphan reporting: recognized vs unrecognized types, maker-portal pointer --

    [Fact]
    public async Task RunPreImportAsync_RecognizedManualType_ShowsFriendlyLabelAndPortalPointer()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 2)); // 2 = Attribute

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains($"Attribute {orphanId}", _console.Output);
        Assert.Contains("remove manually via maker portal", _console.Output);
        Assert.Contains("tools/Solution/home_solution.aspx?etn=solution", _console.Output);
        Assert.Contains("MySolution", _console.Output);
    }

    // -- Opt-in gate: only SupportedManualTypes get a removal recommendation. Unsupported types are
    // logged verbose-only — with the type name, instance name, and what the pre-opt-in logic would
    // have proposed, so a type can be evaluated with real data before opting it in — but never reach
    // the actionable "can't be removed automatically" report. See the connectionreference/bot
    // false-positive incident (2026-07-05): a name resolving via solutioncomponentdefinition is not
    // verification against local source, so it must never drive an actual recommendation.

    [Fact]
    public async Task RunPreImportAsync_UnsupportedManualType_NotReportedOnlyLoggedVerbose()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10064)); // outside SupportedManualTypes

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.DoesNotContain("can't be removed automatically", _console.Output);
        Assert.Contains(orphanId.ToString(), _console.Output); // still visible in verbose, just not recommended
        Assert.Contains("would have proposed: remove manually via maker portal", _console.Output);
        Assert.Contains("0 manual", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_UnsupportedType_ShowsResolvedNameInVerbosePreviewOnly()
    {
        // Resolving a display name via solutioncomponentdefinition is informational, not verification —
        // it must appear in the verbose preview (so the user can evaluate the type for opt-in) but never
        // promote the component into the actionable "can't be removed automatically" report.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10064));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "solutioncomponentdefinition"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("solutioncomponentdefinition") { ["name"] = "connectionreference", ["solutioncomponenttype"] = 10064 }
            ])));
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "connectionreference"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("connectionreference", orphanId) { ["connectionreferencelogicalname"] = "av_sharedcalendlyv2_bffc3" }
            ])));

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.DoesNotContain("can't be removed automatically", _console.Output);
        Assert.Contains("10064 (connectionreference) 'av_sharedcalendlyv2_bffc3'", _console.Output);
        Assert.Contains("would have proposed: remove manually via maker portal", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_NoManualOrphans_NoPortalPointerPrinted()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 91)); // 91 = PluginAssembly, auto-delete

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.DoesNotContain("can't be removed automatically", _console.Output);
        Assert.DoesNotContain("tools/Solution/home_solution.aspx", _console.Output);
    }

    // -- Attribute orphan resolution: suppress false positives, name genuine leftovers --

    void SetupEntityMetadataId(string logicalName, Guid metadataId)
    {
        var metadata = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata { LogicalName = logicalName };
        typeof(Microsoft.Xrm.Sdk.Metadata.EntityMetadata).GetProperty("MetadataId")!.SetValue(metadata, metadataId);
        var response = new Microsoft.Xrm.Sdk.Messages.RetrieveEntityResponse
        {
            Results = new Microsoft.Xrm.Sdk.ParameterCollection { ["EntityMetadata"] = metadata }
        };

        _serviceMock.ExecuteAsync(
                Arg.Is<OrganizationRequest>(r => r.RequestName == "RetrieveEntity" && (string)r.Parameters["LogicalName"] == logicalName),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(response));
    }

    static void WriteEntityXml(string packageSrcRoot, string folderName, params string[] attributeLogicalNames)
    {
        var entityDir = Path.Combine(packageSrcRoot, "Entities", folderName);
        Directory.CreateDirectory(entityDir);
        var attributesXml = string.Concat(attributeLogicalNames.Select(n => $"<attribute PhysicalName=\"{n}\"><LogicalName>{n}</LogicalName></attribute>"));
        File.WriteAllText(Path.Combine(entityDir, "Entity.xml"),
            $"<Entity><EntityInfo><entity Name=\"{folderName}\"><attributes>{attributesXml}</attributes></entity></EntityInfo></Entity>");
    }

    void SetupAttributeMetadata(string entityLogicalName, params (Guid Id, string LogicalName)[] attributes)
    {
        var attrMetas = attributes.Select(a =>
        {
            var attr = new Microsoft.Xrm.Sdk.Metadata.StringAttributeMetadata { LogicalName = a.LogicalName };
            typeof(Microsoft.Xrm.Sdk.Metadata.AttributeMetadata).GetProperty("MetadataId")!.SetValue(attr, a.Id);
            return (Microsoft.Xrm.Sdk.Metadata.AttributeMetadata)attr;
        }).ToArray();

        var entityMeta = new Microsoft.Xrm.Sdk.Metadata.EntityMetadata { LogicalName = entityLogicalName };
        typeof(Microsoft.Xrm.Sdk.Metadata.EntityMetadata).GetProperty("Attributes")!.SetValue(entityMeta, attrMetas);

        var collection = new Microsoft.Xrm.Sdk.Metadata.EntityMetadataCollection();
        collection.Add(entityMeta);

        var response = new Microsoft.Xrm.Sdk.Messages.RetrieveMetadataChangesResponse
        {
            Results = new Microsoft.Xrm.Sdk.ParameterCollection { ["EntityMetadata"] = collection }
        };

        _serviceMock.ExecuteAsync(
                Arg.Is<OrganizationRequest>(r => r.RequestName == "RetrieveMetadataChanges"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(response));
    }

    [Fact]
    public async Task RunPreImportAsync_AttributeStillInEntityXml_SuppressedNotReported()
    {
        var attributeId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (attributeId, 2)); // 2 = Attribute
        var packageSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        WriteEntityXml(packageSrcRoot, "Account", "av_taxid");
        SetupAttributeMetadata("account", (attributeId, "av_taxid"));
        SetupEntityMetadataId("account", Guid.NewGuid());

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], entityLogicalNames: ["account"], packageSrcRoot: packageSrcRoot), default);

            Assert.DoesNotContain(attributeId.ToString(), _console.Output);
            Assert.DoesNotContain("can't be removed automatically", _console.Output);
        }
        finally
        {
            Directory.Delete(packageSrcRoot, true);
        }
    }

    [Fact]
    public async Task RunPreImportAsync_AttributeNotInEntityXml_ReportedWithResolvedName()
    {
        var attributeId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (attributeId, 2)); // 2 = Attribute
        var packageSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        WriteEntityXml(packageSrcRoot, "Account"); // no attributes declared locally
        SetupAttributeMetadata("account", (attributeId, "av_removedfield"));
        SetupEntityMetadataId("account", Guid.NewGuid());

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], entityLogicalNames: ["account"], packageSrcRoot: packageSrcRoot), default);

            Assert.Contains("Attribute 'account.av_removedfield'", _console.Output);
        }
        finally
        {
            Directory.Delete(packageSrcRoot, true);
        }
    }

    [Fact]
    public async Task RunPreImportAsync_AttributeMetadataQuery_UsesStronglyTypedGuidArray()
    {
        // Regression guard: MetadataConditionExpression is strictly typed server-side — an object[]
        // (even one boxing only Guids) throws OrganizationServiceFault 0x80044183 at runtime. A mock
        // can't catch that on its own, so assert the constructed request carries a real Guid[].
        var attributeId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (attributeId, 2)); // 2 = Attribute
        var packageSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        WriteEntityXml(packageSrcRoot, "Account");
        SetupAttributeMetadata("account", (attributeId, "av_removedfield"));
        SetupEntityMetadataId("account", Guid.NewGuid());

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], entityLogicalNames: ["account"], packageSrcRoot: packageSrcRoot), default);

            var call = _serviceMock.ReceivedCalls()
                .Select(c => c.GetArguments()[0])
                .OfType<Microsoft.Xrm.Sdk.Messages.RetrieveMetadataChangesRequest>()
                .Single();

            var condition = call.Query.AttributeQuery.Criteria.Conditions.Single(c => c.PropertyName == "MetadataId");
            Assert.IsType<Guid[]>(condition.Value);
        }
        finally
        {
            Directory.Delete(packageSrcRoot, true);
        }
    }

    [Fact]
    public async Task RunPreImportAsync_FormOrphan_NotReportedFormIsNotInSupportedManualTypes()
    {
        // Form (60) is deliberately NOT in SupportedManualTypes: ScanEntitySubcomponents only finds a
        // form's FormXml file for entities unpacked under Entities/<name>/ — a form on an entity this
        // solution doesn't include at all (e.g. a standard Microsoft form like "Sales Insights" on an
        // entity outside the solution's Entities/ folder) has nothing for the scan to find, so it would
        // always false-positive. See the Sales Insights incident (2026-07-05).
        var formId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (formId, 60)); // 60 = SystemForm

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.DoesNotContain("can't be removed automatically", _console.Output);
        Assert.Contains($"60 (Form) ({formId})", _console.Output); // visible in verbose preview, just not recommended
        Assert.Contains("would have proposed: remove manually via maker portal", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_FormStillInLocalComponents_NotReportedAsOrphan()
    {
        // DeployCommand.ParseSolutionXml merges ComponentClassifier.ScanEntitySubcomponents' Form GUIDs
        // into LocalComponents before RunPreImportAsync ever runs — simulated here directly to confirm
        // OrphanCleanupService respects that merged set (the file-scan itself is unit-tested separately
        // in ComponentClassifierTests).
        var formId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (formId, 60)); // 60 = SystemForm

        await _service.RunPreImportAsync(Ctx("MySolution", [(formId, 60)]), default);

        Assert.DoesNotContain(formId.ToString(), _console.Output);
        Assert.Contains("No orphan components", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_ViewOrphan_NotReportedViewIsNotInSupportedManualTypes()
    {
        // View (26) shares Form's untested gap — not opted in yet (see SupportedManualTypes comment).
        var viewId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (viewId, 26)); // 26 = SavedQuery (View)

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.DoesNotContain("can't be removed automatically", _console.Output);
        Assert.Contains($"26 (View) ({viewId})", _console.Output);
        Assert.Contains("would have proposed: remove manually via maker portal", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_ViewStillInLocalComponents_NotReportedAsOrphan()
    {
        var viewId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (viewId, 26));

        await _service.RunPreImportAsync(Ctx("MySolution", [(viewId, 26)]), default);

        Assert.DoesNotContain(viewId.ToString(), _console.Output);
        Assert.Contains("No orphan components", _console.Output);
    }

    // -- Pre-import → post-import deferred-entry round trip (instance-field state threading) --

    [Fact]
    public async Task RunPostImportAsync_DeferredEntryFromPreImport_RetriedAndResolved()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 91)); // 91 = PluginAssembly
        // First delete attempt (pre-import) hits a dependency fault and is deferred.
        _serviceMock.DeleteAsync("pluginassembly", orphanId, Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new System.ServiceModel.FaultException<OrganizationServiceFault>(
                    new OrganizationServiceFault { ErrorCode = unchecked((int)0x80047002) }),
                _ => Task.CompletedTask); // second call (post-import retry) succeeds

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);
        Assert.Contains("Deferred", _console.Output);

        var failures = await _service.RunPostImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Equal(0, failures);
        await _serviceMock.Received(2).DeleteAsync("pluginassembly", orphanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPostImportAsync_NoPriorPreImportCall_ReturnsZeroNoOp()
    {
        var failures = await _service.RunPostImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Equal(0, failures);
        await _serviceMock.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default, default);
    }
}
