using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Flowline.Core.Services;
using Flowline.Core.Services.OrphanCleanup;
using Flowline.Core.Services.OrphanCleanup.Handlers;
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
    readonly List<string> _autoCreatedPackageFolders = [];

    public OrphanCleanupServiceTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _console.Profile.Width = 400; // avoid word-wrap splitting longer assertion substrings across lines

        // U9: mirrors Program.cs's IOrphanHandler registration — all eight R14 handlers (KTD2), same
        // instances the production DI container resolves, so this suite exercises the real dispatch
        // path rather than a hand-rolled stand-in.
        IReadOnlyList<IOrphanHandler> handlers =
        [
            new PluginAssemblyFamilyHandler(),
            new WebResourceHandler(),
            new WorkflowHandler(),
            new CustomApiFamilyHandler(_console),
            new BotHandler(_console),
            new ConnectionReferenceHandler(_console),
            new RoleHandler(),
            new EntityFamilyHandler(),
        ];
        _service = new OrphanCleanupService(_console, new FlowlineRuntimeOptions(), handlers);
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

        foreach (var packageFolder in _autoCreatedPackageFolders)
            if (Directory.Exists(packageFolder))
                Directory.Delete(packageFolder, true);
    }

    // PostDeployContext no longer carries LocalComponents/EntityLogicalNames/NamedComponents (KTD12) —
    // OrphanCleanupService.CompareAsync parses PackageSrcRoot itself now. Ctx() keeps the same test-facing
    // shape every existing call site already uses by writing a synthetic Solution.xml fixture that
    // round-trips back to the same (localComponents, entityLogicalNames, namedComponents) via
    // ComponentClassifier.ParseSolutionXmlComponents, rather than requiring 59 call sites to be rewritten
    // into hand-built fixtures. When packageSrcRoot isn't explicitly overridden, a fresh temp folder is
    // created per call and cleaned up in Dispose(); when a test passes its own real packageSrcRoot (e.g.
    // for CustomApi/Bot/annotation fixtures), the fixture is written into that existing folder instead.
    PostDeployContext Ctx(
        string solutionName,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> localComponents,
        RunMode mode = RunMode.Normal,
        IReadOnlyList<string>? entityLogicalNames = null,
        string? packageSrcRoot = null,
        IReadOnlyList<(int ComponentType, string SchemaName)>? namedComponents = null)
    {
        string srcRoot;
        if (packageSrcRoot != null)
        {
            srcRoot = packageSrcRoot;
        }
        else
        {
            var packageFolder = Path.Combine(Path.GetTempPath(), $"flowline-test-{Guid.NewGuid():N}");
            _autoCreatedPackageFolders.Add(packageFolder);
            srcRoot = Path.Combine(packageFolder, "src");
        }

        WriteSolutionXmlFixture(srcRoot, localComponents, entityLogicalNames ?? [], namedComponents ?? []);

        return new(_serviceMock, solutionName, mode, "solution.zip", "https://example.crm.dynamics.com", srcRoot);
    }

    static void WriteSolutionXmlFixture(
        string packageSrcRoot,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> components,
        IReadOnlyList<string> entityLogicalNames,
        IReadOnlyList<(int ComponentType, string SchemaName)> namedComponents)
    {
        var otherDir = Path.Combine(packageSrcRoot, "Other");
        Directory.CreateDirectory(otherDir);

        var rootComponents = new List<string>();
        foreach (var (id, type) in components)
            rootComponents.Add($"""<RootComponent type="{type}" id="{id}" />""");
        foreach (var name in entityLogicalNames)
            rootComponents.Add($"""<RootComponent type="1" schemaName="{name}" />""");
        foreach (var (type, name) in namedComponents)
            rootComponents.Add($"""<RootComponent type="{type}" schemaName="{name}" />""");

        File.WriteAllText(Path.Combine(otherDir, "Solution.xml"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <ImportExportXml>
              <SolutionManifest>
                <UniqueName>TestSolution</UniqueName>
                <Version>1.0.0.0</Version>
                <RootComponents>
                  {string.Join("\n                  ", rootComponents)}
                </RootComponents>
              </SolutionManifest>
            </ImportExportXml>
            """);
    }

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

    void SetupOptionSetMetadataId(string schemaName, Guid metadataId)
    {
        var metadata = new Microsoft.Xrm.Sdk.Metadata.OptionSetMetadata { Name = schemaName };
        typeof(Microsoft.Xrm.Sdk.Metadata.OptionSetMetadataBase).GetProperty("MetadataId")!.SetValue(metadata, metadataId);
        var response = new Microsoft.Xrm.Sdk.Messages.RetrieveOptionSetResponse
        {
            Results = new Microsoft.Xrm.Sdk.ParameterCollection { ["OptionSetMetadata"] = metadata }
        };

        _serviceMock.ExecuteAsync(
                Arg.Is<OrganizationRequest>(r => r.RequestName == "RetrieveOptionSet" && (string)r.Parameters["Name"] == schemaName),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(response));
    }

    [Fact]
    public async Task RunPreImportAsync_OptionSetNamedComponent_ResolvesLiveMetadataId_NotReportedAsOrphan()
    {
        // AE4: OptionSet RootComponents in Solution.xml are recorded by schemaName, not id — same
        // schemaName-declared shape as WebResource/Entity, but OptionSet has no backing data table, so
        // ResolveNamedComponentIdsAsync's QueryExpression can't resolve it (NameResolvableTypes has no
        // entry for componenttype 9). It needs its own metadata-request path (RetrieveOptionSetRequest),
        // resolved before the orphan diff runs, per KTD1.
        var liveId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (liveId, 9)); // 9 = OptionSet
        SetupOptionSetMetadataId("av_globalchoice", liveId);

        await _service.RunPreImportAsync(
            Ctx("MySolution", [(Guid.NewGuid(), 0)], namedComponents: [(9, "av_globalchoice")]), default);

        Assert.DoesNotContain(liveId.ToString(), _console.Output);
        Assert.Contains("No orphan components", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_OptionSetGenuinelyRemoved_FallsThroughToUnsupportedVerbosePath()
    {
        // OptionSet's schemaName no longer exists in the org's metadata — RetrieveOptionSetRequest fails
        // for it, so it isn't folded into sNewIds and surfaces as a genuine orphan candidate. OptionSet
        // (9) is not in SupportedManualTypes (KTD1 — this unit doesn't promote it), so it falls through
        // to the unsupported/verbose-only path, same as before this fix, rather than the actionable report.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 9)); // 9 = OptionSet
        // No SetupOptionSetMetadataId call — RetrieveOptionSet is unconfigured, ExecuteAsync returns
        // NSubstitute's default null, so the response cast throws and the name resolves to nothing.

        await _service.RunPreImportAsync(
            Ctx("MySolution", [(Guid.NewGuid(), 0)], namedComponents: [(9, "av_deletedchoice")]), default);

        Assert.DoesNotContain("can't be removed automatically", _console.Output);
        Assert.Contains(orphanId.ToString(), _console.Output);
        Assert.Contains("would have proposed: remove manually via maker portal", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_OptionSetMetadataRequestFailsForOne_OthersStillResolve()
    {
        // One schemaName's metadata request fails (e.g. a deleted global choice) — the failure must not
        // block resolution of the others in the same batch. The unconfigured mock throws a plain
        // NullReferenceException, not a FaultException<OrganizationServiceFault> — an unexpected failure
        // shape (network/auth/etc.), not a genuine "record not found" fault, so it must warn rather than
        // silently resolve to null (code-review finding: distinguish real failures from "not found").
        var stillPresentId = Guid.NewGuid();
        var deletedId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (stillPresentId, 9), (deletedId, 9));
        SetupOptionSetMetadataId("av_stillpresent", stillPresentId);
        // "av_deletedchoice" left unconfigured — simulates a failed/missing metadata lookup.

        await _service.RunPreImportAsync(
            Ctx("MySolution", [(Guid.NewGuid(), 0)],
                namedComponents: [(9, "av_stillpresent"), (9, "av_deletedchoice")]), default);

        Assert.DoesNotContain(stillPresentId.ToString(), _console.Output);
        Assert.Contains(deletedId.ToString(), _console.Output);
        Assert.Contains("OptionSet metadata lookup for 'av_deletedchoice' failed", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_OptionSetGenuinelyDeletedFault_NoWarningLogged()
    {
        // A genuine "record not found" faults at the organization-service level — this is the expected,
        // safe-to-treat-as-null shape and must not be logged as a failure warning.
        var deletedId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (deletedId, 9));

        _serviceMock.ExecuteAsync(
                Arg.Is<OrganizationRequest>(r => r.RequestName == "RetrieveOptionSet"),
                Arg.Any<CancellationToken>())
            .Returns<OrganizationResponse>(_ => throw new System.ServiceModel.FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault()));

        await _service.RunPreImportAsync(
            Ctx("MySolution", [(Guid.NewGuid(), 0)], namedComponents: [(9, "av_deletedchoice")]), default);

        Assert.Contains(deletedId.ToString(), _console.Output);
        Assert.DoesNotContain("OptionSet metadata lookup", _console.Output);
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
    public async Task RunPreImportAsync_BotEntityQueryFails_CustomApiDetectionStillSucceeds()
    {
        // Code-review finding: the entity-detection query used to share one failure domain across all
        // five backing tables (Task.WhenAll under one try/catch) — a single failing table (e.g. "bot"
        // unavailable in an org without Copilot Studio provisioned) blanked out CustomApi detection too.
        // Each table is now queried and caught independently.
        var customApiId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (customApiId, 10036)); // env-specific CustomApi componenttype

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "bot"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new InvalidOperationException("bot table unavailable")));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "customapi"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("customapi", customApiId) { ["name"] = "av_GenuinelyRemovedApi" }
            ])));

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains("bot table unavailable", _console.Output);
        Assert.Contains($"CustomApi 'av_GenuinelyRemovedApi' ({customApiId})", _console.Output);
    }

    // -- Bot orphan detection: entity-side query (KTD2/R3), schemaname-keyed folder verification (KTD3) --

    [Fact]
    public async Task RunPreImportAsync_BotStillInLocalSource_NotReportedAsOrphan()
    {
        // AE3: Bot's live schemaname matches a bots/<schemaname>/bot.xml folder still present locally.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10082)); // env-specific Bot componenttype

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "bot"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("bot", orphanId) { ["schemaname"] = "msdyn_salesCopilot" }
            ])));

        var packageSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(packageSrcRoot, "bots", "msdyn_salesCopilot"));

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], packageSrcRoot: packageSrcRoot), default);

            // Like CustomApi, Bot's componenttype isn't AutoDelete-classified, so this doesn't hit the
            // early "No orphan components" return — it clears entity-side detection and gets filtered
            // out of botOrphans, leaving an empty report instead.
            Assert.DoesNotContain(orphanId.ToString(), _console.Output);
            Assert.Contains("0 to delete, 0 to remove from solution, 0 manual", _console.Output);
        }
        finally
        {
            Directory.Delete(packageSrcRoot, true);
        }
    }

    [Fact]
    public async Task RunPreImportAsync_BotNoMatchingLocalFolder_ReportedAsManualWithResolvedSchemaName()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10082));

        // KTD3: schemaname is the identity attribute, not name (a separate, unrelated display string
        // in real orgs — e.g. schemaname="msdyn_salesCopilot" vs name="Sales Copilot Power Virtual
        // Agents Bot"). The report must show the resolved schemaname, not name.
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "bot"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("bot", orphanId) { ["schemaname"] = "msdyn_salesCopilot", ["name"] = "Sales Copilot Power Virtual Agents Bot" }
            ])));

        var packageSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(packageSrcRoot, "bots", "av_SomeOtherBot")); // no match

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], packageSrcRoot: packageSrcRoot), default);

            Assert.Contains($"Bot 'msdyn_salesCopilot' ({orphanId})", _console.Output);
            Assert.DoesNotContain("Sales Copilot Power Virtual Agents Bot", _console.Output);
            Assert.Contains("remove manually via maker portal", _console.Output);
        }
        finally
        {
            Directory.Delete(packageSrcRoot, true);
        }
    }

    [Fact]
    public async Task RunPreImportAsync_BotsFolderAbsent_NoFalseSuppressionAllBotOrphansReported()
    {
        // No Package/src/bots dir at all (default nonexistent packageSrcRoot) — ScanBotSchemaNames
        // returns an empty scan result, so the Bot orphan is still reported rather than suppressed.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10082));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "bot"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("bot", orphanId) { ["schemaname"] = "msdyn_salesCopilot" }
            ])));

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains($"Bot 'msdyn_salesCopilot' ({orphanId})", _console.Output);
        Assert.Contains("remove manually via maker portal", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_BotSchemaNameUnresolvable_NotReportedAsOrphan()
    {
        // Code-review finding: local-source verification never actually runs when the live record's
        // identity attribute fails to resolve (e.g. a data anomaly clears schemaname while the bot
        // still exists) — GetEntityNamesAsync filters out entities with no schemaname value before
        // ResolveEntityDetectedManualEntriesAsync's suppression check ever sees them. Defaulting to
        // "orphaned" here would be the same false-positive shape the evidence-gated trust bar exists
        // to prevent (KTD2) — an unresolvable identity is inconclusive, not evidence of removal.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10082));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "bot"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("bot", orphanId) // detected, but schemaname never populated
            ])));

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.DoesNotContain(orphanId.ToString(), _console.Output);
        Assert.Contains("0 to delete, 0 to remove from solution, 0 manual", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_BotOrphan_NoLongerLoggedInUnsupportedVerbosePath()
    {
        // Regression: before entity-side detection, Bot fell through to LogUnsupportedOrphansAsync's
        // verbose-only "not tracked yet" preview (see the connectionreference/bot false-positive
        // incident, 2026-07-05). It must now reach the actionable report instead, and SupportedManualTypes
        // itself must be untouched — Bot's env-specific componenttype (10082 here) is never added to it,
        // detection happens purely via the entity-side bot-table query.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10082));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "bot"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("bot", orphanId) { ["schemaname"] = "msdyn_salesCopilot" }
            ])));

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.DoesNotContain("not tracked yet", _console.Output);
        Assert.Contains("can't be removed automatically", _console.Output);
    }

    // -- ConnectionReference orphan detection: entity-side query (KTD2/R2), inline Customizations.xml
    // <connectionreferences> section verification (not deploymentSettings.json — optional, can go stale) --

    static void WriteConnectionReferencesXml(string packageSrcRoot, params string[] logicalNames)
    {
        var otherDir = Path.Combine(packageSrcRoot, "Other");
        Directory.CreateDirectory(otherDir);
        var refsXml = string.Concat(logicalNames.Select(n => $"<connectionreference connectionreferencelogicalname=\"{n}\"><connectorid>/providers/Microsoft.PowerApps/apis/shared_x</connectorid></connectionreference>"));
        File.WriteAllText(Path.Combine(otherDir, "Customizations.xml"),
            $"<ImportExportXml><connectionreferences>{refsXml}</connectionreferences></ImportExportXml>");
    }

    [Fact]
    public async Task RunPreImportAsync_ConnectionReferenceStillInCustomizationsXml_NotReportedAsOrphan()
    {
        // Happy path: connectionreferencelogicalname still present in Other/Customizations.xml.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10064)); // env-specific ConnectionReference componenttype

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "connectionreference"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("connectionreference", orphanId) { ["connectionreferencelogicalname"] = "av_sharepoint" }
            ])));

        var packageSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        WriteConnectionReferencesXml(packageSrcRoot, "av_sharepoint");

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], packageSrcRoot: packageSrcRoot), default);

            Assert.DoesNotContain(orphanId.ToString(), _console.Output);
            Assert.Contains("0 to delete, 0 to remove from solution, 0 manual", _console.Output);
        }
        finally
        {
            Directory.Delete(packageSrcRoot, true);
        }
    }

    [Fact]
    public async Task RunPreImportAsync_ConnectionReferenceNoLongerInCustomizationsXml_ReportedAsManualWithResolvedLogicalName()
    {
        // AE2: connectionreferencelogicalname no longer present in Customizations.xml → actionable Manual.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10064));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "connectionreference"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("connectionreference", orphanId) { ["connectionreferencelogicalname"] = "av_sharepoint" }
            ])));

        var packageSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        WriteConnectionReferencesXml(packageSrcRoot, "av_dataverse"); // different logical name — no match

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], packageSrcRoot: packageSrcRoot), default);

            Assert.Contains($"ConnectionReference 'av_sharepoint' ({orphanId})", _console.Output);
            Assert.Contains("remove manually via maker portal", _console.Output);
        }
        finally
        {
            Directory.Delete(packageSrcRoot, true);
        }
    }

    [Fact]
    public async Task RunPreImportAsync_ConnectionReferencesSectionEmpty_NoFalseSuppressionAllOrphansReported()
    {
        // Edge case: <connectionreferences /> empty or absent → no false suppression.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10064));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "connectionreference"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("connectionreference", orphanId) { ["connectionreferencelogicalname"] = "av_sharepoint" }
            ])));

        var packageSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var otherDir = Path.Combine(packageSrcRoot, "Other");
        Directory.CreateDirectory(otherDir);
        File.WriteAllText(Path.Combine(otherDir, "Customizations.xml"), "<ImportExportXml><connectionreferences /></ImportExportXml>");

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], packageSrcRoot: packageSrcRoot), default);

            Assert.Contains($"ConnectionReference 'av_sharepoint' ({orphanId})", _console.Output);
            Assert.Contains("remove manually via maker portal", _console.Output);
        }
        finally
        {
            Directory.Delete(packageSrcRoot, true);
        }
    }

    [Fact]
    public async Task RunPreImportAsync_CustomizationsXmlMissing_NoFalseSuppressionAllOrphansReported()
    {
        // Edge case: Other/Customizations.xml itself missing → scanner returns empty, no exception.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10064));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "connectionreference"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("connectionreference", orphanId) { ["connectionreferencelogicalname"] = "av_sharepoint" }
            ])));

        // No packageSrcRoot given — defaults to a nonexistent directory, so Other/Customizations.xml is absent.
        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains($"ConnectionReference 'av_sharepoint' ({orphanId})", _console.Output);
        Assert.Contains("remove manually via maker portal", _console.Output);
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

    [Fact]
    public async Task RunPreImportAsync_RoleStillInLocalComponents_NotReportedAsOrphan()
    {
        // AE1: Role's id is declared directly in Solution.xml's RootComponent and mirrored in the
        // unpacked Roles/<name>.xml file — the existing plain id-match already suppresses it, no new
        // scanner needed for the promotion to SupportedManualTypes.
        var roleId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (roleId, 20)); // 20 = Role

        await _service.RunPreImportAsync(Ctx("MySolution", [(roleId, 20)]), default);

        Assert.DoesNotContain(roleId.ToString(), _console.Output);
        Assert.Contains("No orphan components", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_RoleGenuinelyRemoved_ReportedAsManualRoleWithResolvedName()
    {
        // Role (20) is now a SupportedManualTypes member (R1) — a genuinely removed Role (absent from
        // LocalComponents) must surface in the actionable report, using NameResolvableTypes[20]
        // ("role", "roleid", "name") to resolve its display name instead of a bare GUID.
        var roleId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (roleId, 20)); // 20 = Role

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "role"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("role", roleId) { ["name"] = "Custom Sales Role" }
            ])));

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains($"Role 'Custom Sales Role' ({roleId})", _console.Output);
        Assert.Contains("remove manually via maker portal", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_RoleOrphan_NoLongerLoggedInUnsupportedVerbosePath()
    {
        // Regression: before promotion, Role (20) fell through to LogUnsupportedOrphansAsync's
        // verbose-only "not tracked yet" preview. Now that it's a SupportedManualTypes member, it must
        // reach the actionable report instead — not the unsupported-type verbose log line.
        var roleId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (roleId, 20)); // 20 = Role

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.DoesNotContain("not tracked yet", _console.Output);
        Assert.Contains("can't be removed automatically", _console.Output);
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
    public async Task RunPreImportAsync_ConnectionReferenceRecordExists_ReportedInActionableManualNotVerbosePreview()
    {
        // Superseded by ConnectionReference's own entity-side detection (R2/U4): a live connectionreference
        // record used to only surface via solutioncomponentdefinition name resolution — informational,
        // not verification — in the verbose "not tracked yet" preview (see the connectionreference/bot
        // false-positive incident, 2026-07-05). Now that ConnectionReference is entity-detected and cross-
        // checked against Other/Customizations.xml, a genuinely-orphaned record reaches the actionable
        // report instead, with a real name — not a bare GUID behind a verbose-only line.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10064));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "connectionreference"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("connectionreference", orphanId) { ["connectionreferencelogicalname"] = "av_sharedcalendlyv2_bffc3" }
            ])));

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.DoesNotContain("not tracked yet", _console.Output);
        Assert.Contains($"ConnectionReference 'av_sharedcalendlyv2_bffc3' ({orphanId})", _console.Output);
        Assert.Contains("can't be removed automatically", _console.Output);
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

    // -- R6/R7: "possible match found locally" signal for unsupported-type verbose preview (KTD5) --

    [Fact]
    public async Task RunPreImportAsync_UnsupportedOrphanNameMatchesLocalIdentifier_VerboseNotesPossibleLocalMatch()
    {
        // AE5: unsupported type (View, 26 — not in SupportedManualTypes) whose resolved name matches an
        // identifier already harvested from a known local-source shape (here: context.NamedComponents'
        // schemaNames, one of the KTD5 sources) — verbose preview notes a possible local match.
        var viewId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (viewId, 26));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "savedquery"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("savedquery", viewId) { ["name"] = "av_ActiveAccounts" }
            ])));

        var ctx = Ctx("MySolution", [(Guid.NewGuid(), 0)],
            namedComponents: [(61, "av_ActiveAccounts")]);

        await _service.RunPreImportAsync(ctx, default);

        Assert.Contains("26 (View) 'av_ActiveAccounts'", _console.Output);
        Assert.Contains("Possible match found locally.", _console.Output);

        // Load-bearing invariant: the match is informational only — it never promotes the orphan into
        // the actionable report or the manual count (2026-07-05 connectionreference/bot incident).
        Assert.DoesNotContain("can't be removed automatically", _console.Output);
        Assert.Contains("0 to delete, 0 to remove from solution, 0 manual", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_UnsupportedOrphanNameMatchesNothingLocal_VerboseWordingUnchanged()
    {
        // Same unsupported type/name as above, but the harvested identifier set has no overlap — the
        // verbose message keeps today's exact wording, with no local-match note appended.
        var viewId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (viewId, 26));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "savedquery"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("savedquery", viewId) { ["name"] = "av_ActiveAccounts" }
            ])));

        var ctx = Ctx("MySolution", [(Guid.NewGuid(), 0)],
            namedComponents: [(61, "av_SomethingElse")]);

        await _service.RunPreImportAsync(ctx, default);

        Assert.Contains(
            $"Solution component type 26 (View) 'av_ActiveAccounts' ({viewId}) — not tracked yet, no action taken. Out-of-the-box logic would have proposed: remove manually via maker portal.",
            _console.Output);
        Assert.DoesNotContain("Possible match found locally.", _console.Output);

        Assert.DoesNotContain("can't be removed automatically", _console.Output);
        Assert.Contains("0 to delete, 0 to remove from solution, 0 manual", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_UnsupportedOrphanNoResolvableName_NoMatchAttemptedNoException()
    {
        // Edge case: an unlabeled type with no NameResolvableTypes entry resolves no name at all — the
        // local-match check must be skipped rather than throw on a null name.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 9999));

        var ctx = Ctx("MySolution", [(Guid.NewGuid(), 0)],
            namedComponents: [(61, "whatever")]);

        var exception = await Record.ExceptionAsync(() => _service.RunPreImportAsync(ctx, default));

        Assert.Null(exception);
        Assert.Contains(
            $"Solution component type 9999 ({orphanId}) — not tracked yet, no action taken. Out-of-the-box logic would have proposed: remove manually via maker portal.",
            _console.Output);
        Assert.DoesNotContain("Possible match found locally.", _console.Output);
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

    // -- U11: declared PostImportOnly timing (R12) — synthetic handler, no real handler declares this
    // yet (KTD2 defers the motivating use case, Attribute-Auto) --

    sealed class FakePostImportOnlyHandler(int componentType, string displayName, string entityName) : IOrphanHandler
    {
        public HandlerStatus Status => HandlerStatus.Active;

        public Task<HandlerDetectionResult> DetectAsync(
            DetectionContext context,
            IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
            CancellationToken ct)
        {
            var claimed = candidates.Where(c => c.ComponentType == componentType).ToList();
            var findings = claimed
                .Select(c => new HandlerFinding(c.ObjectId, c.ComponentType, displayName, OrphanAction.Delete, OrphanPriority.Prio3, SequenceHint: 0, OrphanTiming.PostImportOnly, EntityName: entityName))
                .ToList();
            return Task.FromResult(new HandlerDetectionResult(findings, claimed.Select(c => c.ObjectId).ToHashSet()));
        }
    }

    [Fact]
    public async Task RunPreImportAsync_PostImportOnlyEntry_NeverAttemptedPreImport()
    {
        var orphanId = Guid.NewGuid();
        var postImportOnlyService = new OrphanCleanupService(_console, new FlowlineRuntimeOptions(),
            [new FakePostImportOnlyHandler(9999, "Widget 'thing'", "widgettable")]);
        SetupSolutionComponents("MySolution", (orphanId, 9999));

        await postImportOnlyService.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        // R12 is purely about execution timing, not visibility — the entry still reaches the printed
        // report same as any other entry (CompareAsync's Entries carries every finding regardless of
        // Timing), it just must never be attempted this pass.
        Assert.Contains("Widget 'thing'", _console.Output);
        await _serviceMock.DidNotReceive().DeleteAsync("widgettable", orphanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPostImportAsync_PostImportOnlyEntry_AttemptedUnconditionallyAfterImport()
    {
        var orphanId = Guid.NewGuid();
        var postImportOnlyService = new OrphanCleanupService(_console, new FlowlineRuntimeOptions(),
            [new FakePostImportOnlyHandler(9999, "Widget 'thing'", "widgettable")]);
        SetupSolutionComponents("MySolution", (orphanId, 9999));

        await postImportOnlyService.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);
        await _serviceMock.DidNotReceive().DeleteAsync("widgettable", orphanId, Arg.Any<CancellationToken>());

        var failures = await postImportOnlyService.RunPostImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Equal(0, failures);
        await _serviceMock.Received(1).DeleteAsync("widgettable", orphanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPostImportAsync_PostImportOnlyEntry_AttemptedRegardlessOfConcurrentDependencyDeferral()
    {
        // A PostImportOnly entry (never attempted pre-import, R12) and a reactively-deferred entry
        // (attempted pre-import, faulted, deferred, R13) converge on the same RunPostImportAsync call —
        // the two mechanisms are independent, so both must execute regardless of the other occurring.
        var deferredId = Guid.NewGuid();       // 91 = PluginAssembly, attempted pre-import, faults, deferred
        var postImportOnlyId = Guid.NewGuid(); // 9999 = synthetic PostImportOnly, never attempted pre-import

        SetupSolutionComponents("MySolution", (deferredId, 91), (postImportOnlyId, 9999));
        _serviceMock.DeleteAsync("pluginassembly", deferredId, Arg.Any<CancellationToken>())
            .Returns(
                _ => throw new System.ServiceModel.FaultException<OrganizationServiceFault>(
                    new OrganizationServiceFault { ErrorCode = unchecked((int)0x80047002) }),
                _ => Task.CompletedTask); // second call (post-import retry) succeeds

        IReadOnlyList<IOrphanHandler> handlers =
        [
            new PluginAssemblyFamilyHandler(),
            new FakePostImportOnlyHandler(9999, "Widget 'thing'", "widgettable"),
        ];
        var mixedService = new OrphanCleanupService(_console, new FlowlineRuntimeOptions(), handlers);

        await mixedService.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);
        Assert.Contains("Deferred", _console.Output);
        await _serviceMock.DidNotReceive().DeleteAsync("widgettable", postImportOnlyId, Arg.Any<CancellationToken>());

        var failures = await mixedService.RunPostImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Equal(0, failures);
        await _serviceMock.Received(2).DeleteAsync("pluginassembly", deferredId, Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).DeleteAsync("widgettable", postImportOnlyId, Arg.Any<CancellationToken>());
    }

    // -- U2: CompareAsync extraction — comparison-only, read-only half of RunPreImportAsync (KTD5) --

    [Fact]
    public async Task CompareAsync_MixedFixture_ReturnsSameClassifiedEntriesRunPreImportAsyncWouldProduce()
    {
        // Happy path: CompareAsync, called directly, classifies an orphan the same way the
        // RunPreImportAsync path does for the same fixture (91 = PluginAssembly, AutoDelete).
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 91));

        var result = await _service.CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.False(result.Skipped);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(orphanId, entry.ObjectId);
        Assert.Equal(91, entry.ComponentType);
        Assert.Equal(OrphanAction.Delete, entry.Action);
    }

    [Fact]
    public async Task CompareAsync_LiveMatchesEveryLocalIdentity_ReturnsNoEntries()
    {
        // AE1: every live solutioncomponent is matched by a locally-declared identity — no drift.
        var componentId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (componentId, 91)); // 91 = PluginAssembly

        var result = await _service.CompareAsync(
            Ctx("MySolution", [(componentId, 91)]), default);

        Assert.False(result.Skipped);
        Assert.Empty(result.Entries);
        Assert.Contains("No orphan components", _console.Output);
    }

    [Fact]
    public async Task CompareAsync_LiveHasComponentNotInLocalFixture_ReturnsUnexpectedlyPresentEntry()
    {
        // AE3: live has a component (e.g. added since the last sync) that local source never
        // declared — CompareAsync's one computed direction (live-minus-source) reports it.
        var unexpectedId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (unexpectedId, 91)); // 91 = PluginAssembly

        var result = await _service.CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.False(result.Skipped);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(unexpectedId, entry.ObjectId);
    }

    [Fact]
    public async Task CompareAsync_LocalFixtureDeclaresComponentAbsentFromLive_NoEntryForIt()
    {
        // Characterization, not AE2 coverage: CompareAsync computes only sOld-minus-sNew (live
        // components unmatched by local source) — see the single `.Where(c => !sNewIds.Contains(...))`
        // read in the method. It never walks source-minus-live, so a component declared locally but
        // absent from the live set (e.g. deleted directly in a target environment) produces no entry
        // at all, not a "missing" entry — OrphanAction has no such case. Reporting that direction
        // (AE2's "declared in committed source was deleted in PROD" scenario) would need new
        // source-minus-live logic that this extraction deliberately does not add (KTD6 — relocate
        // existing logic verbatim, don't extend it).
        var liveOnlyId = Guid.NewGuid();
        var localOnlyId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (liveOnlyId, 91)); // 91 = PluginAssembly, AutoDelete

        var result = await _service.CompareAsync(
            Ctx("MySolution", [(localOnlyId, 91)]), default);

        // Only the live-but-undeclared component is reported; the locally-declared-but-absent-from-live
        // one (localOnlyId) never appears anywhere in the result.
        var entry = Assert.Single(result.Entries);
        Assert.Equal(liveOnlyId, entry.ObjectId);
        Assert.DoesNotContain(result.Entries, e => e.ObjectId == localOnlyId);
    }

    [Fact]
    public async Task CompareAsync_EmptyLiveComponentSet_ReturnsEmptyWithSkipMessage_NoException()
    {
        // Edge case: no solutioncomponent rows at all for the solution — existing skip short-circuit.
        SetupSolutionComponents("MySolution"); // no components configured — empty EntityCollection

        var exception = await Record.ExceptionAsync(() =>
            _service.CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default));

        Assert.Null(exception);
        var result = await _service.CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);
        Assert.True(result.Skipped);
        Assert.Empty(result.Entries);
        Assert.Contains("No solution components in Dataverse — skipping orphan check.", _console.Output);
    }

    [Fact]
    public async Task CompareAsync_EmptyLocalComponentSet_ReturnsSkippedTrue_NoException()
    {
        // Edge case: Solution.xml has no RootComponents at all — existing "prevent mass deletion"
        // short-circuit. This must also surface as Skipped: true, not a verified-zero-drift result.
        SetupSolutionComponents("MySolution", (Guid.NewGuid(), 91));

        var result = await _service.CompareAsync(Ctx("MySolution", []), default);

        Assert.True(result.Skipped);
        Assert.Empty(result.Entries);
        Assert.Contains("No components in Solution.xml — orphan check skipped to prevent mass deletion.", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_NoDeleteMode_ReportBuiltButExecuteInOrderAsyncNotCalled()
    {
        // Edge case: RunMode.NoDelete — behavior must be identical to today: CompareAsync's report is
        // still built and printed, but the mutating step never runs.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 91)); // 91 = PluginAssembly

        await _service.RunPreImportAsync(
            Ctx("MySolution", [(Guid.NewGuid(), 0)], mode: RunMode.NoDelete), default);

        Assert.Contains("would delete", _console.Output);
        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", orphanId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Is<OrganizationRequest>(r => r.RequestName == "RemoveSolutionComponent"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompareAsync_NeverCallsExecuteInOrderAsync_RegardlessOfInput()
    {
        // AE4/Regression: CompareAsync itself never mutates — only RunPreImportAsync does, and only
        // when not RunMode.NoDelete. A deletable orphan (91 = PluginAssembly, AutoDelete) is the
        // clearest observable proxy: if CompareAsync ever reached ExecuteInOrderAsync, this delete
        // call would fire.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 91));

        var result = await _service.CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)], mode: RunMode.Normal), default);

        Assert.False(result.Skipped);
        Assert.Single(result.Entries);
        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", orphanId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Is<OrganizationRequest>(r => r.RequestName == "RemoveSolutionComponent"), Arg.Any<CancellationToken>());
    }

    // -- U10: PrintReport groups automated entries by Prio (R1/R6), Prio1 first --

    [Fact]
    public async Task RunPreImportAsync_MixedPrio1Prio2Prio3_ReportGroupsPrio1First()
    {
        // Happy path: three Active-handler findings, one per Prio tier, drives PrintReport's grouping —
        // Prio1 must render before Prio2, which must render before Prio3, each under a visible label.
        var pluginAssemblyId = Guid.NewGuid(); // 91, RunMode.NoDelete active -> Prio1 (KTD8)
        var workflowId = Guid.NewGuid();       // 29, Activated -> Prio2 (KTD8)
        var webResourceId = Guid.NewGuid();    // 61, always -> Prio3 (KTD8)

        SetupSolutionComponents("MySolution", (pluginAssemblyId, 91), (workflowId, 29), (webResourceId, 61));
        SetupWebResourceNames((webResourceId, "av_ext/old.js"));
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "workflow"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("workflow", workflowId) { ["name"] = "MyFlow", ["statecode"] = new OptionSetValue(1) }
            ])));

        await _service.RunPreImportAsync(
            Ctx("MySolution", [(Guid.NewGuid(), 0)], mode: RunMode.NoDelete), default);

        Assert.Contains("Prio1 — blocks deployment", _console.Output);
        Assert.Contains("Prio2 — still running deleted logic", _console.Output);
        Assert.Contains("Prio3 — safe to clean up", _console.Output);

        var prio1Index = _console.Output.IndexOf("Prio1 — blocks deployment", StringComparison.Ordinal);
        var prio2Index = _console.Output.IndexOf("Prio2 — still running deleted logic", StringComparison.Ordinal);
        var prio3Index = _console.Output.IndexOf("Prio3 — safe to clean up", StringComparison.Ordinal);
        Assert.True(prio1Index < prio2Index, "Prio1 group must render before Prio2");
        Assert.True(prio2Index < prio3Index, "Prio2 group must render before Prio3");

        // Not just label order — each entry must render inside its own group, not merely after its
        // label (pigeonhole alone can't distinguish this with one entry per tier).
        var pluginAssemblyIndex = _console.Output.IndexOf(pluginAssemblyId.ToString(), StringComparison.Ordinal);
        var workflowIndex = _console.Output.IndexOf("MyFlow", StringComparison.Ordinal);
        var webResourceIndex = _console.Output.IndexOf("av_ext/old.js", StringComparison.Ordinal);
        Assert.InRange(pluginAssemblyIndex, prio1Index, prio2Index);
        Assert.InRange(workflowIndex, prio2Index, prio3Index);
        Assert.True(webResourceIndex > prio3Index, "WebResource entry must render after the Prio3 label");
    }

    [Fact]
    public async Task RunPreImportAsync_OnlyPrio3Entries_ShowsSinglePrio3GroupNoOtherLabels()
    {
        // Regression guard: the pre-Prio-grouping common case (a single Prio tier, no Prio1/2 present)
        // must still render correctly — the new grouping is additive, not a behavior change for this shape.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61)); // WebResource -> always Prio3
        SetupWebResourceNames((orphanId, "av_ext/old.js"));

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains("Prio3 — safe to clean up", _console.Output);
        Assert.DoesNotContain("Prio1 — blocks deployment", _console.Output);
        Assert.DoesNotContain("Prio2 — still running deleted logic", _console.Output);
    }

    // Synthetic handler exercising the KTD3 Preview-marker rendering path directly — no handler ships
    // Preview this round (KTD2), so no real handler can drive this scenario.
    sealed class FakePreviewHandler(int componentType, string displayName) : IOrphanHandler
    {
        public HandlerStatus Status => HandlerStatus.Preview;

        public Task<HandlerDetectionResult> DetectAsync(
            DetectionContext context,
            IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
            CancellationToken ct)
        {
            var claimed = candidates.Where(c => c.ComponentType == componentType).ToList();
            var findings = claimed
                .Select(c => new HandlerFinding(c.ObjectId, c.ComponentType, displayName, OrphanAction.Delete, OrphanPriority.Prio3, SequenceHint: 0, OrphanTiming.PreImportEligible))
                .ToList();
            return Task.FromResult(new HandlerDetectionResult(findings, claimed.Select(c => c.ObjectId).ToHashSet()));
        }
    }

    [Fact]
    public async Task CompareAsync_PreviewHandlerFinding_PrintsPreviewMarker_ExcludedFromReport()
    {
        // KTD3/R7: a Preview handler's findings print a "[Preview: HandlerName]" verbose marker and
        // never enter the actionable report (Entries stays empty) — U9 already wires this branch in
        // DispatchToHandlersAsync; this confirms the marker's exact rendering format from PrintReport's
        // perspective (nothing else in the report references it).
        var orphanId = Guid.NewGuid();
        var previewOnlyService = new OrphanCleanupService(_console, new FlowlineRuntimeOptions(), [new FakePreviewHandler(9999, "Widget 'thing'")]);
        SetupSolutionComponents("MySolution", (orphanId, 9999));

        var result = await previewOnlyService.CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains("[Preview: FakePreviewHandler] Widget 'thing'", _console.Output);
        Assert.Empty(result.Entries);
    }

    // -- CompareAsync(packageFolder, ...) convenience overload — parses committed source itself,
    // for callers (DriftCommand) with no packed/mutating context of their own --

    [Fact]
    public async Task CompareAsync_PackageFolderOverload_ParsesLocalSourceAndDelegatesToContextOverload()
    {
        var declaredId = Guid.NewGuid();
        var unexpectedId = Guid.NewGuid();
        var packageFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var otherDir = Path.Combine(packageFolder, "src", "Other");
        Directory.CreateDirectory(otherDir);
        File.WriteAllText(Path.Combine(otherDir, "Solution.xml"), $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <ImportExportXml>
              <SolutionManifest>
                <UniqueName>MySolution</UniqueName>
                <Version>1.0.0.0</Version>
                <RootComponents>
                  <RootComponent type="91" id="{{{declaredId}}}" />
                </RootComponents>
              </SolutionManifest>
            </ImportExportXml>
            """);

        try
        {
            SetupSolutionComponents("MySolution", (declaredId, 91), (unexpectedId, 91));

            var result = await _service.CompareAsync(packageFolder, _serviceMock, "MySolution", "https://example.crm.dynamics.com", default);

            // Parses the fixture's Solution.xml itself (no pre-parsed LocalComponents passed in) and
            // reaches the same classification the context-based overload would: declaredId matched,
            // unexpectedId reported.
            Assert.False(result.Skipped);
            var entry = Assert.Single(result.Entries);
            Assert.Equal(unexpectedId, entry.ObjectId);
        }
        finally
        {
            Directory.Delete(packageFolder, true);
        }
    }
}
