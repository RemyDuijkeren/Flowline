using System.Text.RegularExpressions;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Flowline.Core.Services;
using Flowline.Core.OrphanCleanup;
using Flowline.Core.OrphanCleanup.Handlers;
using Flowline.Core;
using Flowline.Core.Models;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests;

public class OrphanCleanupServiceTests : IDisposable
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly TestConsole _console;
    readonly OrphanCleanupService _service;
    readonly string _dataverseSolutionSrcRoot;
    readonly string _webResourcesDir;
    readonly List<string> _autoCreatedDataverseSolutionFolders = [];

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
            new PluginAssemblyFamilyHandler(_console),
            new WebResourceHandler(_console),
            new WorkflowHandler(_console),
            new CustomApiFamilyHandler(_console),
            new BotHandler(_console),
            new ConnectionReferenceHandler(_console),
            new RoleHandler(_console),
            new EntityFamilyHandler(_console),
        ];
        _service = new OrphanCleanupService(_console, handlers);
        _dataverseSolutionSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _webResourcesDir = Path.Combine(_dataverseSolutionSrcRoot, "WebResources");
        Directory.CreateDirectory(_webResourcesDir);

        // Default: any unconfigured RetrieveMultipleAsync (e.g. bulk name-resolution queries) returns
        // empty rather than NSubstitute's null default — real Dataverse never returns a null EntityCollection.
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

        // Default: no cross-solution membership
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "solutioncomponent" && q.LinkEntities.Count > 0)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataverseSolutionSrcRoot))
            Directory.Delete(_dataverseSolutionSrcRoot, true);

        foreach (var dataverseSolutionFolder in _autoCreatedDataverseSolutionFolders)
            if (Directory.Exists(dataverseSolutionFolder))
                Directory.Delete(dataverseSolutionFolder, true);
    }

    [Theory]
    [InlineData(false, false, "(--no-delete active)")]
    [InlineData(false, true, "(--no-delete active)")]
    // Managed + already installed carries no hint: PrintReport's managed-upgrade wording says who removes
    // the components, so a second reason phrase on the same line would only repeat it.
    [InlineData(true, true, "")]
    [InlineData(true, false, "(managed — first install, cleanup runs on a later upgrade deploy)")]
    public void BuildReportOnlyHint_ReturnsExpected(bool includeManaged, bool existsInTarget, string expected)
    {
        var solution = new DeploySolutionInfo("MySolution", "https://example.crm.dynamics.com", includeManaged, existsInTarget);
        Assert.Equal(expected, OrphanCleanupService.BuildReportOnlyHint(solution));
    }

    // U5: DryRun only dominates where the managed status has nothing better to say. A managed solution's
    // reason holds under --dry-run too (the upgrade is what removes the components either way), so it
    // outranks the preview marker instead of being hidden by it.
    [Theory]
    [InlineData(false, false, "(--dry-run preview)")]
    [InlineData(false, true, "(--dry-run preview)")]
    [InlineData(true, true, "")]
    [InlineData(true, false, "(managed — first install, cleanup runs on a later upgrade deploy)")]
    public void BuildReportOnlyHint_DryRun_ManagedStatusOutranksPreviewMarker(bool includeManaged, bool existsInTarget, string expected)
    {
        var solution = new DeploySolutionInfo("MySolution", "https://example.crm.dynamics.com", includeManaged, existsInTarget);
        Assert.Equal(expected, OrphanCleanupService.BuildReportOnlyHint(solution, RunMode.DryRun));
    }

    // PostDeployContext no longer carries LocalComponents/EntityLogicalNames/NamedComponents (KTD12) —
    // OrphanCleanupService.CompareAsync parses DataverseSolutionSrcRoot itself now. Ctx() keeps the same test-facing
    // shape every existing call site already uses by writing a synthetic Solution.xml fixture that
    // round-trips back to the same (localComponents, entityLogicalNames, namedComponents) via
    // ComponentClassifier.ParseSolutionXmlComponents, rather than requiring 59 call sites to be rewritten
    // into hand-built fixtures. When dataverseSolutionSrcRoot isn't explicitly overridden, a fresh temp folder is
    // created per call and cleaned up in Dispose(); when a test passes its own real dataverseSolutionSrcRoot (e.g.
    // for CustomApi/Bot/annotation fixtures), the fixture is written into that existing folder instead.
    PostDeployContext Ctx(
        string solutionName,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> localComponents,
        RunMode mode = RunMode.Normal,
        IReadOnlyList<string>? entityLogicalNames = null,
        string? dataverseSolutionSrcRoot = null,
        IReadOnlyList<(int ComponentType, string SchemaName)>? namedComponents = null,
        bool deleteOrphansConsent = false,
        bool includeManaged = false)
    {
        string srcRoot;
        if (dataverseSolutionSrcRoot != null)
        {
            srcRoot = dataverseSolutionSrcRoot;
        }
        else
        {
            var dataverseSolutionFolder = Path.Combine(Path.GetTempPath(), $"flowline-test-{Guid.NewGuid():N}");
            _autoCreatedDataverseSolutionFolders.Add(dataverseSolutionFolder);
            srcRoot = Path.Combine(dataverseSolutionFolder, "src");
        }

        WriteSolutionXmlFixture(srcRoot, localComponents, entityLogicalNames ?? [], namedComponents ?? []);

        var solution = new DeploySolutionInfo(solutionName, "https://example.crm.dynamics.com", includeManaged, ExistsInTarget: true);
        return new(_serviceMock, solution, mode, "solution.zip", srcRoot, deleteOrphansConsent);
    }

    static void WriteSolutionXmlFixture(
        string dataverseSolutionSrcRoot,
        IReadOnlyList<(Guid ObjectId, int ComponentType)> components,
        IReadOnlyList<string> entityLogicalNames,
        IReadOnlyList<(int ComponentType, string SchemaName)> namedComponents)
    {
        var otherDir = Path.Combine(dataverseSolutionSrcRoot, "Other");
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
                Arg.Is(Matching<QueryExpression>(q =>
                    q.EntityName == "solutioncomponent" &&
                    q.LinkEntities.Any(le => le.LinkToEntityName == "solution"))),
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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "webresource")),
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

        // WebResource is Guarded — pass consent so this asserts the exemption decision (not the gate).
        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)], dataverseSolutionSrcRoot: _dataverseSolutionSrcRoot, deleteOrphansConsent: true), default);

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

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)], dataverseSolutionSrcRoot: _dataverseSolutionSrcRoot), default);

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

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)], dataverseSolutionSrcRoot: _dataverseSolutionSrcRoot, deleteOrphansConsent: true), default);

        await _serviceMock.Received(1).DeleteAsync("webresource", orphanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPreImportAsync_NoAnnotations_NoExemptions()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61));
        SetupWebResourceNames((orphanId, "av_ext/lib.js"));
        File.WriteAllText(Path.Combine(_webResourcesDir, "form.js"), "code(); // no annotations");

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)], dataverseSolutionSrcRoot: _dataverseSolutionSrcRoot, deleteOrphansConsent: true), default);

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

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)], dataverseSolutionSrcRoot: _dataverseSolutionSrcRoot, deleteOrphansConsent: true), default);

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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "solutioncomponent" && q.Criteria.Conditions.Any(c => c.AttributeName == "objectid"))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }

    [Fact]
    public async Task RunPreImportAsync_OrphanOnlyInDefaultSolution_DeletesInsteadOfRemoving()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("Cr07982", (orphanId, 91)); // 91 = PluginAssembly
        SetupCrossSolutionMembership(orphanId, "Default");

        await AutoService(91, "pluginassembly").RunPreImportAsync(Ctx("Cr07982", [(Guid.NewGuid(), 0)]), default);

        await _serviceMock.Received(1).DeleteAsync("pluginassembly", orphanId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "RemoveSolutionComponent")), Arg.Any<CancellationToken>());
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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "customapi")),
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

    // Writes a raw Solution.xml (parent-of-src folder returned) for cases WriteSolutionXmlFixture can't
    // express — notably a RootComponent carrying BOTH an id and a schemaName, as plugin assemblies do.
    string CreateRawSolutionFixture(string rootComponentsXml)
    {
        var dataverseSolutionFolder = Path.Combine(Path.GetTempPath(), $"flowline-test-{Guid.NewGuid():N}");
        _autoCreatedDataverseSolutionFolders.Add(dataverseSolutionFolder);
        var otherDir = Path.Combine(dataverseSolutionFolder, "src", "Other");
        Directory.CreateDirectory(otherDir);
        File.WriteAllText(Path.Combine(otherDir, "Solution.xml"), $"""
            <?xml version="1.0" encoding="utf-8"?>
            <ImportExportXml>
              <SolutionManifest>
                <UniqueName>MySolution</UniqueName>
                <Version>1.0.0.0</Version>
                <RootComponents>
                  {rootComponentsXml}
                </RootComponents>
              </SolutionManifest>
            </ImportExportXml>
            """);
        return dataverseSolutionFolder;
    }

    void SetupPluginAssemblyNames(params (Guid Id, string Name)[] assemblies)
    {
        var entities = assemblies.Select(a => new Entity("pluginassembly", a.Id) { ["name"] = a.Name }).ToList();
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly" && q.Criteria.Conditions.Any(c => c.AttributeName == "name"))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }

    [Fact]
    public async Task CompareAsync_PluginAssemblyIdDriftsButNameMatches_NotReportedAsOrphan()
    {
        // The core false positive: Solution.xml records a plugin assembly with a GUID that push/import
        // re-mint per environment, so the committed id never matches the live one. Matching by GUID alone
        // false-flagged the live, in-solution assembly as a deletable orphan. Resolving it live by its
        // portable simple name (harvested from schemaName) fixes it. See
        // deploy-false-positive-orphan-package-assembly-guid-not-portable.md.
        var onDiskId = Guid.NewGuid();  // committed in Solution.xml
        var liveId   = Guid.NewGuid();  // re-minted in the target environment
        var folder = CreateRawSolutionFixture(
            $"""<RootComponent type="91" id="{onDiskId}" schemaName="Cr07982.Backend, Version=0.0.0.0, Culture=neutral, PublicKeyToken=48c2f23af73ee643" />""");
        SetupSolutionComponents("MySolution", (liveId, 91));
        SetupPluginAssemblyNames((liveId, "Cr07982.Backend"));

        var result = await _service.CompareAsync(folder, _serviceMock, "MySolution", "https://example.crm.dynamics.com", default);

        Assert.False(result.Skipped);
        Assert.Empty(result.Entries);
        Assert.DoesNotContain(liveId.ToString(), _console.Output);
        Assert.Contains("No orphan components", _console.Output);
    }

    [Fact]
    public async Task CompareAsync_PluginAssemblyRenamedAway_StillReportedAsOrphan()
    {
        // Guards against the fix being too loose: a live, in-solution assembly whose simple name is NOT
        // declared in source (renamed away, or a genuine leftover) must still be detected. Its name never
        // matches the committed name, so it isn't folded into the in-solution set and stays an orphan.
        var liveId = Guid.NewGuid();
        var onDiskId = Guid.NewGuid();
        var folder = CreateRawSolutionFixture(
            $"""<RootComponent type="91" id="{onDiskId}" schemaName="Cr07982.RenamedAway, Version=0.0.0.0, Culture=neutral, PublicKeyToken=48c2f23af73ee643" />""");
        SetupSolutionComponents("MySolution", (liveId, 91));
        // Live assembly's name differs from the committed "Cr07982.RenamedAway", so the by-name query
        // returns nothing for it (default empty EntityCollection) — no rescue.

        var result = await _service.CompareAsync(folder, _serviceMock, "MySolution", "https://example.crm.dynamics.com", default);

        Assert.False(result.Skipped);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(liveId, entry.ObjectId);
        Assert.Equal(91, entry.ComponentType);
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
                Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "RetrieveOptionSet" && (string)r.Parameters["Name"] == schemaName)),
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
        // (9) has no handler in the roster claiming it (KTD1 — this unit doesn't promote it), so it falls
        // through to the unsupported/verbose-only path, same as before this fix, rather than the actionable report.
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
                Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "RetrieveOptionSet")),
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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "customapi")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("customapi", liveId) { ["name"] = "av_AatYourService" }
            ])));

        var dataverseSolutionSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(dataverseSolutionSrcRoot, "customapis", "av_AatYourService"));

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], dataverseSolutionSrcRoot: dataverseSolutionSrcRoot), default);

            // Unlike WebResource, CustomApi's componenttype isn't AutoDelete-classified, so this doesn't
            // hit the early "No orphan components" return — it's claimed and resolved by
            // CustomApiFamilyHandler, then suppressed because its uniquename is still declared locally,
            // leaving an empty report instead.
            Assert.DoesNotContain(liveId.ToString(), _console.Output);
            Assert.Contains("0 to delete, 0 to remove from solution, 0 manual", _console.Output);
        }
        finally
        {
            Directory.Delete(dataverseSolutionSrcRoot, true);
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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "bot")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new InvalidOperationException("bot table unavailable")));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "customapi")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("customapi", customApiId) { ["name"] = "av_GenuinelyRemovedApi" }
            ])));

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains("bot table unavailable", _console.Output);
        Assert.Contains($"CustomApi 'av_GenuinelyRemovedApi' ({customApiId})", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_PluginAssemblyQueryFails_OtherFamiliesStillSucceed()
    {
        // Fix1 (code-review): Pass-1 (componenttype-gated) handlers previously had zero try/catch
        // anywhere — a transient fault on ANY Pass-1 handler's live query (here,
        // PluginAssemblyFamilyHandler's name resolution) propagated uncaught through
        // DispatchToHandlersAsync, aborting the whole deploy before another Pass-1 family (or Pass 2)
        // ever ran. Each Pass-1 handler now catches and degrades independently, so a PluginAssembly
        // query fault must not prevent RoleHandler's own detection (a different Pass-1 family) from
        // completing and being reported.
        var assemblyId = Guid.NewGuid();
        var roleId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (assemblyId, 91), (roleId, 20));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new InvalidOperationException("pluginassembly table unavailable")));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "role")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("role", roleId) { ["name"] = "Custom Sales Role" }
            ])));

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains("pluginassembly table unavailable", _console.Output);
        Assert.Contains("Custom Sales Role", _console.Output);
    }

    // -- Bot orphan detection: entity-side query (KTD2/R3), schemaname-keyed folder verification (KTD3) --

    [Fact]
    public async Task RunPreImportAsync_BotStillInLocalSource_NotReportedAsOrphan()
    {
        // AE3: Bot's live schemaname matches a bots/<schemaname>/bot.xml folder still present locally.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10082)); // env-specific Bot componenttype

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "bot")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("bot", orphanId) { ["schemaname"] = "msdyn_salesCopilot" }
            ])));

        var dataverseSolutionSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(dataverseSolutionSrcRoot, "bots", "msdyn_salesCopilot"));

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], dataverseSolutionSrcRoot: dataverseSolutionSrcRoot), default);

            // Like CustomApi, Bot's componenttype isn't AutoDelete-classified, so this doesn't hit the
            // early "No orphan components" return — it clears entity-side detection and gets filtered
            // out of botOrphans, leaving an empty report instead.
            Assert.DoesNotContain(orphanId.ToString(), _console.Output);
            Assert.Contains("0 to delete, 0 to remove from solution, 0 manual", _console.Output);
        }
        finally
        {
            Directory.Delete(dataverseSolutionSrcRoot, true);
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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "bot")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("bot", orphanId) { ["schemaname"] = "msdyn_salesCopilot", ["name"] = "Sales Copilot Power Virtual Agents Bot" }
            ])));

        var dataverseSolutionSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(dataverseSolutionSrcRoot, "bots", "av_SomeOtherBot")); // no match

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], dataverseSolutionSrcRoot: dataverseSolutionSrcRoot), default);

            Assert.Contains($"Bot 'msdyn_salesCopilot' ({orphanId})", _console.Output);
            Assert.DoesNotContain("Sales Copilot Power Virtual Agents Bot", _console.Output);
            Assert.Contains("remove manually via maker portal", _console.Output);
        }
        finally
        {
            Directory.Delete(dataverseSolutionSrcRoot, true);
        }
    }

    [Fact]
    public async Task RunPreImportAsync_BotsFolderAbsent_NoFalseSuppressionAllBotOrphansReported()
    {
        // No Package/src/bots dir at all (default nonexistent dataverseSolutionSrcRoot) — ScanBotSchemaNames
        // returns an empty scan result, so the Bot orphan is still reported rather than suppressed.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10082));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "bot")),
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
        // still exists) — BotHandler's own KTD5 check skips a row with no resolved schemaname before its
        // local-declaration suppression check ever sees it. Defaulting to "orphaned" here would be the
        // same false-positive shape the evidence-gated trust bar exists to prevent (KTD2) — an
        // unresolvable identity is inconclusive, not evidence of removal.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10082));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "bot")),
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
        // incident, 2026-07-05). It must now reach the actionable report instead, and no componenttype-
        // gated handler's own gate is touched to make that happen — Bot's env-specific componenttype
        // (10082 here) is never added to any of them, detection happens purely via BotHandler's own
        // entity-side bot-table query.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10082));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "bot")),
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

    static void WriteConnectionReferencesXml(string dataverseSolutionSrcRoot, params string[] logicalNames)
    {
        var otherDir = Path.Combine(dataverseSolutionSrcRoot, "Other");
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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "connectionreference")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("connectionreference", orphanId) { ["connectionreferencelogicalname"] = "av_sharepoint" }
            ])));

        var dataverseSolutionSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        WriteConnectionReferencesXml(dataverseSolutionSrcRoot, "av_sharepoint");

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], dataverseSolutionSrcRoot: dataverseSolutionSrcRoot), default);

            Assert.DoesNotContain(orphanId.ToString(), _console.Output);
            Assert.Contains("0 to delete, 0 to remove from solution, 0 manual", _console.Output);
        }
        finally
        {
            Directory.Delete(dataverseSolutionSrcRoot, true);
        }
    }

    [Fact]
    public async Task RunPreImportAsync_ConnectionReferenceNoLongerInCustomizationsXml_ReportedAsManualWithResolvedLogicalName()
    {
        // AE2: connectionreferencelogicalname no longer present in Customizations.xml → actionable Manual.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10064));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "connectionreference")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("connectionreference", orphanId) { ["connectionreferencelogicalname"] = "av_sharepoint" }
            ])));

        var dataverseSolutionSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        WriteConnectionReferencesXml(dataverseSolutionSrcRoot, "av_dataverse"); // different logical name — no match

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], dataverseSolutionSrcRoot: dataverseSolutionSrcRoot), default);

            Assert.Contains($"ConnectionReference 'av_sharepoint' ({orphanId})", _console.Output);
            Assert.Contains("remove manually via maker portal", _console.Output);
        }
        finally
        {
            Directory.Delete(dataverseSolutionSrcRoot, true);
        }
    }

    [Fact]
    public async Task RunPreImportAsync_ConnectionReferencesSectionEmpty_NoFalseSuppressionAllOrphansReported()
    {
        // Edge case: <connectionreferences /> empty or absent → no false suppression.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10064));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "connectionreference")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("connectionreference", orphanId) { ["connectionreferencelogicalname"] = "av_sharepoint" }
            ])));

        var dataverseSolutionSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var otherDir = Path.Combine(dataverseSolutionSrcRoot, "Other");
        Directory.CreateDirectory(otherDir);
        File.WriteAllText(Path.Combine(otherDir, "Customizations.xml"), "<ImportExportXml><connectionreferences /></ImportExportXml>");

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], dataverseSolutionSrcRoot: dataverseSolutionSrcRoot), default);

            Assert.Contains($"ConnectionReference 'av_sharepoint' ({orphanId})", _console.Output);
            Assert.Contains("remove manually via maker portal", _console.Output);
        }
        finally
        {
            Directory.Delete(dataverseSolutionSrcRoot, true);
        }
    }

    [Fact]
    public async Task RunPreImportAsync_CustomizationsXmlMissing_NoFalseSuppressionAllOrphansReported()
    {
        // Edge case: Other/Customizations.xml itself missing → scanner returns empty, no exception.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10064));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "connectionreference")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("connectionreference", orphanId) { ["connectionreferencelogicalname"] = "av_sharepoint" }
            ])));

        // No dataverseSolutionSrcRoot given — defaults to a nonexistent directory, so Other/Customizations.xml is absent.
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

        await AutoService(91, "pluginassembly").RunPreImportAsync(Ctx("Cr07982", [(Guid.NewGuid(), 0)]), default);

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "RemoveSolutionComponent")), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", orphanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPreImportAsync_NoWebResourcesDirUnderPackageSrc_NoExemptionCheck()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61));
        // No Package/src/WebResources dir at the default (nonexistent) dataverseSolutionSrcRoot — if exemption
        // ran, it would try to query and the mock would return an empty collection, potentially still
        // deleting. The point is: no WebResources dir → no name query, normal orphan flow.

        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)], deleteOrphansConsent: true), default);

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
                Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "RetrieveEntity" && (string)r.Parameters["LogicalName"] == "account")),
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
        // gone from Solution.xml, not a schemaName-resolution gap. Entity (1) is claimed by
        // EntityFamilyHandler, so it must still surface in the report (unlike connectionreference/bot).
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
        // scanner needed for RoleHandler to recognize it as still-declared.
        var roleId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (roleId, 20)); // 20 = Role

        await _service.RunPreImportAsync(Ctx("MySolution", [(roleId, 20)]), default);

        Assert.DoesNotContain(roleId.ToString(), _console.Output);
        Assert.Contains("No orphan components", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_RoleReconciledToDifferentLiveId_ResolvedByLocalName_NotReportedAsOrphan()
    {
        // Cross-environment id drift: Dataverse reconciles security roles by name on import when a role
        // of that name already exists in the target, so a role synced from one org can carry a different
        // live id in another. The raw id-match alone would misclassify the reconciled live role as
        // orphaned; ComponentClassifier.ScanRoleNames + the by-name resolution now also resolve it via
        // the local Roles/<name>.xml file name, in addition to the stale raw id.
        var staleLocalId = Guid.NewGuid(); // id recorded in Solution.xml, captured from a different org
        var liveId = Guid.NewGuid();       // reconciled id actually present in this target org

        SetupSolutionComponents("MySolution", (liveId, 20)); // 20 = Role
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "role")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("role", liveId) { ["name"] = "Custom Sales Role" }
            ])));

        var rolesDir = Path.Combine(_dataverseSolutionSrcRoot, "Roles");
        Directory.CreateDirectory(rolesDir);
        File.WriteAllText(Path.Combine(rolesDir, "Custom Sales Role.xml"), "<Role />");

        await _service.RunPreImportAsync(
            Ctx("MySolution", [(staleLocalId, 20)], dataverseSolutionSrcRoot: _dataverseSolutionSrcRoot), default);

        Assert.DoesNotContain(liveId.ToString(), _console.Output);
        Assert.Contains("No orphan components", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_RoleGenuinelyRemoved_ReportedAsManualRoleWithResolvedName()
    {
        // Role (20) is claimed by RoleHandler (R1) — a genuinely removed Role (absent from
        // LocalComponents) must surface in the actionable report, using the ("role", "roleid", "name")
        // lookup triple to resolve its display name instead of a bare GUID.
        var roleId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (roleId, 20)); // 20 = Role

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "role")),
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
        // verbose-only "not tracked yet" preview. Now that RoleHandler claims it, it must reach the
        // actionable report instead — not the unsupported-type verbose log line.
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

    // -- Opt-in gate: only a componenttype claimed by some handler in the roster gets a removal
    // recommendation. Unclaimed types are logged verbose-only — with the type name, instance name, and
    // what the pre-opt-in logic would have proposed, so a type can be evaluated with real data before a
    // handler opts it in — but never reach the actionable "can't be removed automatically" report. See
    // the connectionreference/bot false-positive incident (2026-07-05): a name resolving via
    // solutioncomponentdefinition is not verification against local source, so it must never drive an
    // actual recommendation.

    [Fact]
    public async Task RunPreImportAsync_UnsupportedManualType_NotReportedOnlyLoggedVerbose()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 10064)); // outside every handler's componenttype gate

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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "connectionreference")),
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
                Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "RetrieveEntity" && (string)r.Parameters["LogicalName"] == logicalName)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(response));
    }

    static void WriteEntityXml(string dataverseSolutionSrcRoot, string folderName, params string[] attributeLogicalNames)
    {
        var entityDir = Path.Combine(dataverseSolutionSrcRoot, "Entities", folderName);
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
                Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "RetrieveMetadataChanges")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(response));
    }

    [Fact]
    public async Task RunPreImportAsync_AttributeStillInEntityXml_SuppressedNotReported()
    {
        var attributeId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (attributeId, 2)); // 2 = Attribute
        var dataverseSolutionSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        WriteEntityXml(dataverseSolutionSrcRoot, "Account", "av_taxid");
        SetupAttributeMetadata("account", (attributeId, "av_taxid"));
        SetupEntityMetadataId("account", Guid.NewGuid());

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], entityLogicalNames: ["account"], dataverseSolutionSrcRoot: dataverseSolutionSrcRoot), default);

            Assert.DoesNotContain(attributeId.ToString(), _console.Output);
            Assert.DoesNotContain("can't be removed automatically", _console.Output);
        }
        finally
        {
            Directory.Delete(dataverseSolutionSrcRoot, true);
        }
    }

    [Fact]
    public async Task RunPreImportAsync_AttributeNotInEntityXml_ReportedWithResolvedName()
    {
        var attributeId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (attributeId, 2)); // 2 = Attribute
        var dataverseSolutionSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        WriteEntityXml(dataverseSolutionSrcRoot, "Account"); // no attributes declared locally
        SetupAttributeMetadata("account", (attributeId, "av_removedfield"));
        SetupEntityMetadataId("account", Guid.NewGuid());

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], entityLogicalNames: ["account"], dataverseSolutionSrcRoot: dataverseSolutionSrcRoot), default);

            Assert.Contains("Attribute 'account.av_removedfield'", _console.Output);
        }
        finally
        {
            Directory.Delete(dataverseSolutionSrcRoot, true);
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
        var dataverseSolutionSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        WriteEntityXml(dataverseSolutionSrcRoot, "Account");
        SetupAttributeMetadata("account", (attributeId, "av_removedfield"));
        SetupEntityMetadataId("account", Guid.NewGuid());

        try
        {
            await _service.RunPreImportAsync(
                Ctx("MySolution", [(Guid.NewGuid(), 0)], entityLogicalNames: ["account"], dataverseSolutionSrcRoot: dataverseSolutionSrcRoot), default);

            var call = _serviceMock.ReceivedCalls()
                .Select(c => c.GetArguments()[0])
                .OfType<Microsoft.Xrm.Sdk.Messages.RetrieveMetadataChangesRequest>()
                .Single();

            var condition = call.Query.AttributeQuery.Criteria.Conditions.Single(c => c.PropertyName == "MetadataId");
            Assert.IsType<Guid[]>(condition.Value);
        }
        finally
        {
            Directory.Delete(dataverseSolutionSrcRoot, true);
        }
    }

    [Fact]
    public async Task RunPreImportAsync_FormOrphan_NotReportedNoHandlerClaimsFormType()
    {
        // Form (60) is deliberately claimed by no handler in the roster: ScanEntitySubcomponents only
        // finds a form's FormXml file for entities unpacked under Entities/<name>/ — a form on an entity
        // this solution doesn't include at all (e.g. a standard Microsoft form like "Sales Insights" on
        // an entity outside the solution's Entities/ folder) has nothing for the scan to find, so it
        // would always false-positive. See the Sales Insights incident (2026-07-05).
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
    public async Task RunPreImportAsync_ViewOrphan_NotReportedNoHandlerClaimsViewType()
    {
        // View (26) shares Form's untested gap — no handler in the roster claims it yet (see the Form test above).
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
        // AE5: unsupported type (View, 26 — claimed by no handler in the roster) whose resolved name matches an
        // identifier already harvested from a known local-source shape (here: context.NamedComponents'
        // schemaNames, one of the KTD5 sources) — verbose preview notes a possible local match.
        var viewId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (viewId, 26));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "savedquery")),
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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "savedquery")),
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

        var svc = AutoService(91, "pluginassembly");
        await svc.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);
        Assert.Contains("Deferred", _console.Output);

        var failures = await svc.RunPostImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

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

    // Reusable actionable (Auto) handler for the orchestrator-machinery tests below — claims one
    // componenttype and emits a PreImportEligible Delete, so the execute/report path runs independent of
    // any production handler's status. Each real handler's own status is asserted in its per-handler test;
    // these tests exercise the shared machinery (delete, cross-solution remove, deferral, report-only
    // skip) and must not couple to a specific handler's status policy.
    sealed class FakeAutoHandler(int componentType, string displayName, string entityName) : IOrphanHandler
    {
        public HandlerStatus Status => HandlerStatus.Auto;

        public Task<HandlerDetectionResult> DetectAsync(
            DetectionContext context,
            IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
            CancellationToken ct)
        {
            var claimed = candidates.Where(c => c.ComponentType == componentType).ToList();
            var findings = claimed
                .Select(c => new HandlerFinding(c.ObjectId, c.ComponentType, displayName, OrphanAction.Delete, OrphanPriority.Prio3, SequenceHint: 0, OrphanTiming.PreImportEligible, EntityName: entityName))
                .ToList();
            return Task.FromResult(new HandlerDetectionResult(findings, claimed.Select(c => c.ObjectId).ToHashSet()));
        }
    }

    // Service wired with a single FakeAutoHandler for `componentType` — for machinery tests that need an
    // actionable delete regardless of the production status mapping.
    OrphanCleanupService AutoService(int componentType, string entityName, string displayName = "Thing") =>
        new(_console, [new FakeAutoHandler(componentType, displayName, entityName)]);

    // Configurable-status handler for the status-behavior tests below (Report/Guarded/Silent/Auto).
    sealed class FakeStatusHandler(int componentType, string displayName, string entityName, HandlerStatus status) : IOrphanHandler
    {
        public HandlerStatus Status => status;

        public Task<HandlerDetectionResult> DetectAsync(
            DetectionContext context,
            IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
            CancellationToken ct)
        {
            var claimed = candidates.Where(c => c.ComponentType == componentType).ToList();
            var findings = claimed
                .Select(c => new HandlerFinding(c.ObjectId, c.ComponentType, displayName, OrphanAction.Delete, OrphanPriority.Prio3, SequenceHint: 0, OrphanTiming.PreImportEligible, EntityName: entityName))
                .ToList();
            return Task.FromResult(new HandlerDetectionResult(findings, claimed.Select(c => c.ObjectId).ToHashSet()));
        }
    }

    OrphanCleanupService StatusService(HandlerStatus status, int componentType = 91, string entityName = "pluginassembly", string displayName = "PluginAssembly 'thing'") =>
        new(_console, [new FakeStatusHandler(componentType, displayName, entityName, status)]);

    [Fact]
    public async Task RunPreImportAsync_ReportHandler_Normal_SurfacesButNeverDeletes()
    {
        // A Report handler surfaces its orphan in the report even in Normal mode, but is never executed.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 91));

        await StatusService(HandlerStatus.Report).RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains("PluginAssembly 'thing'", _console.Output);
        Assert.Contains("detected, not auto-removed", _console.Output);
        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", orphanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPreImportAsync_GuardedHandler_NoConsent_SurfacesButNeverDeletes()
    {
        // Guarded without --force delete-orphans behaves exactly like Report: surfaced, never executed.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 91));

        await StatusService(HandlerStatus.Guarded).RunPreImportAsync(
            Ctx("MySolution", [(Guid.NewGuid(), 0)], deleteOrphansConsent: false), default);

        Assert.Contains("detected, not auto-removed", _console.Output);
        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", orphanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPreImportAsync_GuardedHandler_WithConsent_Deletes()
    {
        // --force delete-orphans consent promotes a Guarded handler to actionable — it deletes like Auto.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 91));

        await StatusService(HandlerStatus.Guarded).RunPreImportAsync(
            Ctx("MySolution", [(Guid.NewGuid(), 0)], deleteOrphansConsent: true), default);

        await _serviceMock.Received(1).DeleteAsync("pluginassembly", orphanId, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(HandlerStatus.Report, false, true)]
    [InlineData(HandlerStatus.Report, true, true)]   // consent never promotes Report
    [InlineData(HandlerStatus.Guarded, false, true)]
    [InlineData(HandlerStatus.Guarded, true, false)] // consent promotes Guarded to actionable
    [InlineData(HandlerStatus.Auto, false, false)]
    [InlineData(HandlerStatus.Auto, true, false)]
    [InlineData(HandlerStatus.Silent, false, false)] // Silent never reaches this; defensive default
    public void IsReportOnly_MapsStatusAndConsent(HandlerStatus status, bool consent, bool expected) =>
        Assert.Equal(expected, OrphanCleanupService.IsReportOnly(status, consent));

    // -- U11: declared PostImportOnly timing (R12) — synthetic handler, no real handler declares this
    // yet (KTD2 defers the motivating use case, Attribute-Auto) --

    sealed class FakePostImportOnlyHandler(int componentType, string displayName, string entityName) : IOrphanHandler
    {
        public HandlerStatus Status => HandlerStatus.Auto;

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
        var postImportOnlyService = new OrphanCleanupService(_console,
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
        var postImportOnlyService = new OrphanCleanupService(_console,
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
            new FakeAutoHandler(91, "PluginAssembly 'thing'", "pluginassembly"),
            new FakePostImportOnlyHandler(9999, "Widget 'thing'", "widgettable"),
        ];
        var mixedService = new OrphanCleanupService(_console, handlers);

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

        await AutoService(91, "pluginassembly").RunPreImportAsync(
            Ctx("MySolution", [(Guid.NewGuid(), 0)], mode: RunMode.NoDelete), default);

        Assert.Contains("would delete", _console.Output);
        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", orphanId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "RemoveSolutionComponent")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPreImportAsync_DryRunMode_ReportBuiltButExecuteInOrderAsyncNotCalled()
    {
        // U5: RunMode.DryRun gets the exact same report-only treatment as RunMode.NoDelete — mirrors
        // RunPreImportAsync_NoDeleteMode_ReportBuiltButExecuteInOrderAsyncNotCalled above.
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 91)); // 91 = PluginAssembly

        await AutoService(91, "pluginassembly").RunPreImportAsync(
            Ctx("MySolution", [(Guid.NewGuid(), 0)], mode: RunMode.DryRun), default);

        Assert.Contains("would delete", _console.Output);
        Assert.Contains("(--dry-run preview)", _console.Output);
        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", orphanId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "RemoveSolutionComponent")), Arg.Any<CancellationToken>());
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
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "RemoveSolutionComponent")), Arg.Any<CancellationToken>());
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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "workflow")),
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

    // -- Managed upgrade: Dataverse removes the orphans, so the report says so instead of assigning work --

    [Fact]
    public async Task RunPreImportAsync_ManagedUpgrade_FramesEveryEntryAsRemovedByTheUpgrade()
    {
        // A managed solution already installed in the target imports as an Upgrade, which removes every
        // component the new version drops — including the Manual-action ones Flowline can't touch via the
        // SDK. So no maker-portal instruction, no "would delete" (Flowline deletes nothing here), and the
        // Manual entries join the Prio groups rather than a separate "can't be removed automatically" block.
        var webResourceId = Guid.NewGuid(); // 61 -> Prio3, Action.Delete
        var entityId = Guid.NewGuid();      // 1  -> Prio3, Action.Manual

        SetupSolutionComponents("MySolution", (webResourceId, 61), (entityId, 1));
        SetupWebResourceNames((webResourceId, "av_ext/old.js"));

        await _service.RunPreImportAsync(
            Ctx("MySolution", [(Guid.NewGuid(), 0)], mode: RunMode.NoDelete, includeManaged: true), default);

        // Substrings only — TestConsole wraps at 80 columns, so a whole rendered line isn't assertable.
        Assert.Contains($"WebResource 'av_ext/old.js' ({webResourceId})", _console.Output);
        Assert.Contains($"Entity {entityId}", _console.Output);
        Assert.Equal(2, Regex.Matches(_console.Output, "removed by the managed upgrade").Count);
        Assert.DoesNotContain("remove manually via maker portal", _console.Output);
        Assert.DoesNotContain("would delete", _console.Output);
        Assert.DoesNotContain("can't be removed automatically", _console.Output);

        // Prio triage survives — it's what tells the operator which orphans are about to break their deploy.
        Assert.Contains("Prio3 — safe to clean up", _console.Output);

        Assert.Contains("only lose membership", _console.Output);
        Assert.Contains("2 components — the upgrade import removes them.", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_Unmanaged_KeepsManualBlockAndMakerPortalPointer()
    {
        // Guard on the other side of the branch: the unmanaged path still assigns the work, because there
        // is no upgrade import to do it.
        var entityId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (entityId, 1));

        await _service.RunPreImportAsync(
            Ctx("MySolution", [(Guid.NewGuid(), 0)], mode: RunMode.NoDelete), default);

        Assert.Contains("can't be removed automatically", _console.Output);
        Assert.Contains("remove manually via maker portal", _console.Output);
        Assert.DoesNotContain("removed by the managed upgrade", _console.Output);
    }

    // -- U7/R10: orphan cleanup reports WebResourceDependencyChecker dependents on the entry itself --

    void SetupDependenciesForDelete(Guid webResourceId, params Entity[] dependents) =>
        _serviceMock.ExecuteAsync(
                Arg.Is(Matching<Microsoft.Crm.Sdk.Messages.RetrieveDependenciesForDeleteRequest>(r => r!.ObjectId == webResourceId)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(new Microsoft.Crm.Sdk.Messages.RetrieveDependenciesForDeleteResponse
            {
                Results = { ["EntityCollection"] = new EntityCollection(dependents.ToList()) }
            }));

    static Entity DependencyRecord(int type, Guid objectId) => new("dependency")
    {
        ["dependentcomponenttype"] = new OptionSetValue(type),
        ["dependentcomponentobjectid"] = objectId
    };

    [Fact]
    public async Task RunPreImportAsync_WebResourceConvertedToRemoveFromSolution_ReportsDependentsAgainstRemoval()
    {
        var orphanId = Guid.NewGuid();
        var formId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61)); // 61 = WebResource
        SetupWebResourceNames((orphanId, "av_ext/shared.js"));
        SetupCrossSolutionMembership(orphanId, "OtherSolution"); // forces Delete -> RemoveFromSolution
        SetupDependenciesForDelete(orphanId, DependencyRecord(60, formId)); // 60 = Form
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "systemform")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("systemform", formId) { ["name"] = "Account Main Form" }
            ])));

        // WebResource is Guarded — consent makes this an actionable removal, not a report-only surface.
        await _service.RunPreImportAsync(
            Ctx("MySolution", [(Guid.NewGuid(), 0)], deleteOrphansConsent: true), default);

        Assert.Contains("remove from solution", _console.Output);
        Assert.Contains("Account Main Form", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_ReportOnlyWebResource_ReportsDependents()
    {
        var orphanId = Guid.NewGuid();
        var formId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61));
        SetupWebResourceNames((orphanId, "av_ext/reportonly.js"));
        SetupDependenciesForDelete(orphanId, DependencyRecord(60, formId));
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "systemform")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("systemform", formId) { ["name"] = "Account Main Form" }
            ])));

        // No deleteOrphansConsent — WebResourceHandler (Guarded) surfaces this report-only, never executes.
        await _service.RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains("detected, not auto-removed", _console.Output);
        Assert.Contains("Account Main Form", _console.Output);
        await _serviceMock.DidNotReceive().DeleteAsync("webresource", orphanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPreImportAsync_NonWebResourceOrphan_NoDependencyRequestIssued()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 91)); // 91 = PluginAssembly, not WebResource

        await AutoService(91, "pluginassembly").RunPreImportAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Any<Microsoft.Crm.Sdk.Messages.RetrieveDependenciesForDeleteRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPreImportAsync_WebResourceDependencyLookupFaults_ReportsUncheckedAndOthersStillProcess()
    {
        var faultingId = Guid.NewGuid();
        var okId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (faultingId, 61), (okId, 61));
        SetupWebResourceNames((faultingId, "av_ext/faulting.js"), (okId, "av_ext/ok.js"));

        _serviceMock.ExecuteAsync(
                Arg.Is(Matching<Microsoft.Crm.Sdk.Messages.RetrieveDependenciesForDeleteRequest>(r => r!.ObjectId == faultingId)),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<OrganizationResponse>(
                new System.ServiceModel.FaultException<OrganizationServiceFault>(new OrganizationServiceFault(), "dependency fault")));
        SetupDependenciesForDelete(okId); // no dependents — Dataverse answered, found nothing

        await _service.RunPreImportAsync(
            Ctx("MySolution", [(Guid.NewGuid(), 0)], deleteOrphansConsent: true), default);

        Assert.Contains("Couldn't check for dependents.", _console.Output);
        // The fault degrades only the faulting resource's own check (KTD3/R11) — the rest of cleanup
        // still runs, including the delete for both web resources.
        await _serviceMock.Received(1).DeleteAsync("webresource", faultingId, Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).DeleteAsync("webresource", okId, Arg.Any<CancellationToken>());
        // The non-faulting resource has zero dependents (Dataverse checked, found nothing) — it must
        // render with no dependents line at all, not as unchecked too. A single occurrence of "Couldn't
        // check for dependents." (only for faultingId) catches a bug that degraded the whole batch.
        var uncheckedCount = _console.Output.Split("Couldn't check for dependents.").Length - 1;
        Assert.Equal(1, uncheckedCount);
    }

    // Synthetic handler exercising the Silent-marker rendering path directly — no real handler ships
    // Silent this round, so no real handler can drive this scenario.
    sealed class FakeSilentHandler(int componentType, string displayName) : IOrphanHandler
    {
        public HandlerStatus Status => HandlerStatus.Silent;

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
    public async Task CompareAsync_SilentHandlerFinding_PrintsSilentMarker_ExcludedFromReport()
    {
        // A Silent handler's findings print a "[Silent: HandlerName]" verbose marker and never enter the
        // actionable report (Entries stays empty) — MergeResult drops them to verbose before an
        // OrphanEntry is built; this confirms the marker's exact rendering format.
        var orphanId = Guid.NewGuid();
        var silentOnlyService = new OrphanCleanupService(_console, [new FakeSilentHandler(9999, "Widget 'thing'")]);
        SetupSolutionComponents("MySolution", (orphanId, 9999));

        var result = await silentOnlyService.CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains("[Silent: FakeSilentHandler] Widget 'thing'", _console.Output);
        Assert.Empty(result.Entries);
    }

    // -- CompareAsync(dataverseSolutionFolder, ...) convenience overload — parses committed source itself,
    // for callers (DriftCommand) with no packed/mutating context of their own --

    [Fact]
    public async Task CompareAsync_DataverseSolutionFolderOverload_ParsesLocalSourceAndDelegatesToContextOverload()
    {
        var declaredId = Guid.NewGuid();
        var unexpectedId = Guid.NewGuid();
        var dataverseSolutionFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var otherDir = Path.Combine(dataverseSolutionFolder, "src", "Other");
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

            var result = await _service.CompareAsync(dataverseSolutionFolder, _serviceMock, "MySolution", "https://example.crm.dynamics.com", default);

            // Parses the fixture's Solution.xml itself (no pre-parsed LocalComponents passed in) and
            // reaches the same classification the context-based overload would: declaredId matched,
            // unexpectedId reported.
            Assert.False(result.Skipped);
            var entry = Assert.Single(result.Entries);
            Assert.Equal(unexpectedId, entry.ObjectId);
        }
        finally
        {
            Directory.Delete(dataverseSolutionFolder, true);
        }
    }

    // -- U4: provenance verdict resolution on the compare path --

    // Configurable-status, configurable-identity handler for provenance tests — the fakes above default
    // to LocalSourceIdentity.None (R12/KTD4's "no mapping" case), which ComponentSourceLocator.Locate
    // turns into "nothing to resolve" and never exercises a lookup at all.
    sealed class FakeIdentityHandler(int componentType, string displayName, string entityName, LocalSourceIdentity identity, HandlerStatus status = HandlerStatus.Auto, OrphanAction action = OrphanAction.Delete) : IOrphanHandler
    {
        public HandlerStatus Status => status;

        public Task<HandlerDetectionResult> DetectAsync(
            DetectionContext context,
            IReadOnlyList<(Guid ObjectId, int ComponentType)> candidates,
            CancellationToken ct)
        {
            var claimed = candidates.Where(c => c.ComponentType == componentType).ToList();
            var findings = claimed
                .Select(c => new HandlerFinding(c.ObjectId, c.ComponentType, displayName, action, OrphanPriority.Prio3, SequenceHint: 0, OrphanTiming.PreImportEligible, EntityName: entityName) { Identity = identity })
                .ToList();
            return Task.FromResult(new HandlerDetectionResult(findings, claimed.Select(c => c.ObjectId).ToHashSet()));
        }
    }

    // Test-only IComponentProvenanceLookup: resolve/fault behavior is configurable per test, and every
    // location it was asked about is recorded so a test can assert scoping ("this entry only, not others").
    sealed class FakeProvenanceLookup(Func<ComponentSourceLocation, ComponentProvenance>? resolve = null, Exception? fault = null) : IComponentProvenanceLookup
    {
        public List<ComponentSourceLocation> Calls { get; } = [];

        public Task<ComponentProvenance> ResolveAsync(string? checkoutSolutionSrcRoot, ComponentSourceLocation location, CancellationToken ct)
        {
            Calls.Add(location);
            if (fault != null) throw fault;
            return Task.FromResult(resolve?.Invoke(location) ?? ComponentProvenance.Undetermined);
        }
    }

    [Fact]
    public async Task CompareAsync_EveryEntryCarriesAVerdict_IncludingReportOnlyEntries()
    {
        var orphanId = Guid.NewGuid();
        var declared = ComponentProvenance.Declared("abc123", "Author", DateTimeOffset.UtcNow, "remove role");
        var service = new OrphanCleanupService(_console,
            [new FakeIdentityHandler(20, "Role 'thing'", "role", LocalSourceIdentity.Role("thing"), HandlerStatus.Report)],
            new FakeProvenanceLookup(_ => declared));
        SetupSolutionComponents("MySolution", (orphanId, 20));

        var result = await service.CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        var entry = Assert.Single(result.Entries);
        Assert.True(entry.ReportOnly, "Report handler findings are report-only");
        Assert.Equal(ProvenanceVerdict.Declared, entry.Provenance.Verdict);
    }

    [Fact]
    public async Task CompareAsync_LookupReturnsDeclared_SurfacesOnlyOnTheMatchingEntry()
    {
        var resolvableId = Guid.NewGuid();
        var unresolvableId = Guid.NewGuid();
        var declared = ComponentProvenance.Declared("abc123", "Author", DateTimeOffset.UtcNow, "remove role");
        var lookup = new FakeProvenanceLookup(_ => declared);
        var service = new OrphanCleanupService(_console,
            [
                new FakeIdentityHandler(20, "Role 'thing'", "role", LocalSourceIdentity.Role("thing")),
                new FakeAutoHandler(9999, "Widget 'thing'", "widgettable"), // Identity defaults to None — unresolvable
            ],
            lookup);
        SetupSolutionComponents("MySolution", (resolvableId, 20), (unresolvableId, 9999));

        var result = await service.CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Equal(2, result.Entries.Count);
        var resolvable = result.Entries.Single(e => e.ObjectId == resolvableId);
        var unresolvable = result.Entries.Single(e => e.ObjectId == unresolvableId);
        Assert.Equal(ProvenanceVerdict.Declared, resolvable.Provenance.Verdict);
        Assert.Equal(ProvenanceVerdict.Undetermined, unresolvable.Provenance.Verdict);
        Assert.Single(lookup.Calls); // only the resolvable entry ever reached the lookup
    }

    [Fact]
    public async Task CompareAsync_NoLookupRegistered_EveryEntryReadsUndeterminedAndCompareSucceeds()
    {
        var orphanId = Guid.NewGuid();
        var service = new OrphanCleanupService(_console,
            [new FakeIdentityHandler(20, "Role 'thing'", "role", LocalSourceIdentity.Role("thing"))]);
        SetupSolutionComponents("MySolution", (orphanId, 20));

        var result = await service.CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(ProvenanceVerdict.Undetermined, entry.Provenance.Verdict);
        Assert.False(result.Skipped);
    }

    [Fact]
    public async Task CompareAsync_LookupThrows_EntryReadsUndeterminedAndCompareDoesNotFail()
    {
        var orphanId = Guid.NewGuid();
        var service = new OrphanCleanupService(_console,
            [new FakeIdentityHandler(20, "Role 'thing'", "role", LocalSourceIdentity.Role("thing"))],
            new FakeProvenanceLookup(fault: new InvalidOperationException("boom")));
        SetupSolutionComponents("MySolution", (orphanId, 20));

        var result = await service.CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        var entry = Assert.Single(result.Entries);
        Assert.Equal(ProvenanceVerdict.Undetermined, entry.Provenance.Verdict);
    }

    // R9: a real before/after comparison over the same entry set — no field but Provenance may differ
    // between a compare run with a lookup wired and one without.
    [Fact]
    public async Task CompareAsync_R9_ActionAndReportOnlyUnaffectedByProvenanceResolution()
    {
        var deleteId = Guid.NewGuid();
        var reportOnlyId = Guid.NewGuid();
        var declared = ComponentProvenance.Declared("abc123", "Author", DateTimeOffset.UtcNow, "remove");

        OrphanCleanupService BuildService(IComponentProvenanceLookup? lookup) => new(_console,
            [
                new FakeIdentityHandler(20, "Role 'a'", "role", LocalSourceIdentity.Role("a"), HandlerStatus.Auto),
                new FakeIdentityHandler(21, "Role 'b'", "role", LocalSourceIdentity.Role("b"), HandlerStatus.Report),
            ],
            lookup);

        SetupSolutionComponents("MySolution", (deleteId, 20), (reportOnlyId, 21));

        var withoutLookup = await BuildService(null).CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);
        var withLookup = await BuildService(new FakeProvenanceLookup(_ => declared)).CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Equal(withoutLookup.Entries.Count, withLookup.Entries.Count);
        foreach (var before in withoutLookup.Entries)
        {
            var after = withLookup.Entries.Single(e => e.ObjectId == before.ObjectId);
            Assert.Equal(before.Action, after.Action);
            Assert.Equal(before.ReportOnly, after.ReportOnly);
            Assert.Equal(before.Priority, after.Priority);
            Assert.Equal(before.SequenceHint, after.SequenceHint);
            Assert.Equal(before.Timing, after.Timing);
        }

        // Sanity: the lookup actually changed something, else this test wouldn't be exercising R9 at all.
        Assert.Contains(withLookup.Entries, e => e.Provenance.Verdict == ProvenanceVerdict.Declared);
        Assert.DoesNotContain(withoutLookup.Entries, e => e.Provenance.Verdict == ProvenanceVerdict.Declared);
    }

    // R10: drift's exit code selection mirrors DriftCommand.SelectExitCode exactly (Flowline.Commands
    // isn't referenceable from Flowline.Core.Tests) — it must read identically before and after verdict
    // resolution for the same entry set.
    static int SelectExitCodeLikeDrift(CompareResult result) => result switch
    {
        { Skipped: true }    => (int)ExitCode.Inconclusive,
        { Entries.Count: 0 } => (int)ExitCode.Success,
        _                    => (int)ExitCode.ValidationFailed
    };

    [Fact]
    public async Task CompareAsync_R10_DriftExitCodeUnaffectedByProvenanceResolution()
    {
        var orphanId = Guid.NewGuid();
        var declared = ComponentProvenance.Declared("abc123", "Author", DateTimeOffset.UtcNow, "remove");

        OrphanCleanupService BuildService(IComponentProvenanceLookup? lookup) => new(_console,
            [new FakeIdentityHandler(20, "Role 'a'", "role", LocalSourceIdentity.Role("a"))],
            lookup);

        SetupSolutionComponents("MySolution", (orphanId, 20));

        var withoutLookup = await BuildService(null).CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);
        var withLookup = await BuildService(new FakeProvenanceLookup(_ => declared)).CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Equal(SelectExitCodeLikeDrift(withoutLookup), SelectExitCodeLikeDrift(withLookup));
    }

    // -- U5: verdict rendering (R5, R6, R7, R8, KD6, KTD7) --

    [Fact]
    public async Task CompareAsync_DeclaredVerdict_RendersAuthorDateAndCommitSubject()
    {
        var orphanId = Guid.NewGuid();
        var date = new DateTimeOffset(2026, 3, 4, 0, 0, 0, TimeSpan.Zero);
        var declared = ComponentProvenance.Declared("abc123", "Jane Doe", date, "drop unused role");
        var service = new OrphanCleanupService(_console,
            [new FakeIdentityHandler(20, "Role 'thing'", "role", LocalSourceIdentity.Role("thing"))],
            new FakeProvenanceLookup(_ => declared));
        SetupSolutionComponents("MySolution", (orphanId, 20));

        await service.CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains("Jane Doe", _console.Output);
        Assert.Contains("2026-03-04", _console.Output);
        Assert.Contains("drop unused role", _console.Output);
    }

    [Fact]
    public async Task CompareAsync_NeverInSourceVerdict_RendersAsSuch_MentionsNoCommit()
    {
        var orphanId = Guid.NewGuid();
        var service = new OrphanCleanupService(_console,
            [new FakeIdentityHandler(20, "Role 'thing'", "role", LocalSourceIdentity.Role("thing"))],
            new FakeProvenanceLookup(_ => ComponentProvenance.NeverInSource));
        SetupSolutionComponents("MySolution", (orphanId, 20));

        await service.CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains("Never in source", _console.Output);
        // No commit identity anywhere in the report — a real removal would have printed a sha-derived
        // author/date/subject line, which NeverInSource never carries (ComponentProvenance.Removal is null).
        Assert.DoesNotContain("Removed by", _console.Output);
    }

    [Fact]
    public async Task CompareAsync_UndeterminedVerdict_RendersItsOwnWording_DistinctFromNeverInSourceWording()
    {
        // KD6: rendered as data, not asserted against a hardcoded string — the point is that the two
        // verdicts' wording can never collapse into each other, whatever the exact copy ends up being.
        async Task<string> RenderVerdictLineAsync(ComponentProvenance verdict)
        {
            var console = new TestConsole();
            console.Profile.Width = 400;
            var orphanId = Guid.NewGuid();
            var service = new OrphanCleanupService(console,
                [new FakeIdentityHandler(20, "Role 'thing'", "role", LocalSourceIdentity.Role("thing"))],
                new FakeProvenanceLookup(_ => verdict));
            SetupSolutionComponents("MySolution", (orphanId, 20));

            await service.CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

            var lines = console.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var markerIndex = Array.FindIndex(lines, l => l.Contains("Role 'thing'"));
            return lines[markerIndex + 1].Trim();
        }

        var undeterminedLine = await RenderVerdictLineAsync(ComponentProvenance.Undetermined);
        var neverInSourceLine = await RenderVerdictLineAsync(ComponentProvenance.NeverInSource);

        Assert.NotEqual(undeterminedLine, neverInSourceLine);
        Assert.DoesNotContain("never", undeterminedLine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("in source", undeterminedLine, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("couldn't", neverInSourceLine, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompareAsync_DeclaredVerdict_CommitSubjectWithMarkupCharacters_RendersLiterally()
    {
        var orphanId = Guid.NewGuid();
        const string subject = "fix[ci]: drop [bold]thing[/]";
        var declared = ComponentProvenance.Declared("abc123", "Jane Doe", DateTimeOffset.UtcNow, subject);
        var service = new OrphanCleanupService(_console,
            [new FakeIdentityHandler(20, "Role 'thing'", "role", LocalSourceIdentity.Role("thing"))],
            new FakeProvenanceLookup(_ => declared));
        SetupSolutionComponents("MySolution", (orphanId, 20));

        await service.CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains(subject, _console.Output);
    }

    [Fact]
    public async Task CompareAsync_DeclaredVerdict_RendersUnderBothActionableAndReportOnlyBranches()
    {
        var actionableId = Guid.NewGuid();
        var reportOnlyId = Guid.NewGuid();
        var declared = ComponentProvenance.Declared("abc123", "Jane Doe", DateTimeOffset.UtcNow, "removed both");
        var service = new OrphanCleanupService(_console,
            [
                new FakeIdentityHandler(20, "Role 'auto'", "role", LocalSourceIdentity.Role("auto")),
                new FakeIdentityHandler(21, "Role 'reportonly'", "role", LocalSourceIdentity.Role("reportonly"), HandlerStatus.Report),
            ],
            new FakeProvenanceLookup(_ => declared));
        SetupSolutionComponents("MySolution", (actionableId, 20), (reportOnlyId, 21));

        var result = await service.CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains(result.Entries, e => e.ObjectId == actionableId && !e.ReportOnly);
        Assert.Contains(result.Entries, e => e.ObjectId == reportOnlyId && e.ReportOnly);
        Assert.Equal(2, Regex.Matches(_console.Output, Regex.Escape("removed both")).Count);
    }

    [Fact]
    public async Task CompareAsync_DeclaredVerdict_RendersUnderManualList()
    {
        // Manual entries (RoleHandler-style "human review before removal") are still reported to the
        // operator — R1 gives every reported orphan a verdict with no carve-out for that third list.
        var orphanId = Guid.NewGuid();
        var declared = ComponentProvenance.Declared("abc123", "Jane Doe", DateTimeOffset.UtcNow, "remove manually reviewed thing");
        var service = new OrphanCleanupService(_console,
            [new FakeIdentityHandler(20, "Role 'thing'", "role", LocalSourceIdentity.Role("thing"), action: OrphanAction.Manual)],
            new FakeProvenanceLookup(_ => declared));
        SetupSolutionComponents("MySolution", (orphanId, 20));

        await service.CompareAsync(Ctx("MySolution", [(Guid.NewGuid(), 0)]), default);

        Assert.Contains("can't be removed automatically", _console.Output);
        Assert.Contains("remove manually reviewed thing", _console.Output);
    }
}
