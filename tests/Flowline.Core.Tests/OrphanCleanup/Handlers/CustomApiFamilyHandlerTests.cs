using System.ServiceModel;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Flowline.Core.Services;
using Flowline.Core.Services.OrphanCleanup;
using Flowline.Core.Services.OrphanCleanup.Handlers;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests.OrphanCleanup.Handlers;

public class CustomApiFamilyHandlerTests : IDisposable
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly TestConsole _console;
    readonly CustomApiFamilyHandler _handler;
    readonly string _packageSrcRoot;

    public CustomApiFamilyHandlerTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _console.Profile.Width = 400; // avoid word-wrap splitting longer assertion substrings across lines
        _handler = new CustomApiFamilyHandler(_console);
        _packageSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_packageSrcRoot);

        // Default: any unconfigured RetrieveMultipleAsync returns empty rather than NSubstitute's null
        // default — real Dataverse never returns a null EntityCollection (mirrors OrphanCleanupServiceTests).
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_packageSrcRoot))
            Directory.Delete(_packageSrcRoot, true);
    }

    DetectionContext Ctx(RunMode mode = RunMode.Normal) =>
        new(_packageSrcRoot, _serviceMock, "MySolution", "https://example.crm.dynamics.com", mode, []);

    void SetupTableNames(string entityLogicalName, params (Guid Id, string Name)[] rows)
    {
        var entities = rows.Select(r => new Entity(entityLogicalName, r.Id) { ["name"] = r.Name }).ToList();
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == entityLogicalName),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }

    // -- Happy path --

    [Fact]
    public async Task DetectAsync_OrphanedCustomApiNoLocalMatch_ResolvesToAutoDeletePrio2()
    {
        var orphanId = Guid.NewGuid();
        SetupTableNames("customapi", (orphanId, "av_GenuinelyRemovedApi"));

        var findings = await _handler.DetectAsync(Ctx(), [(orphanId, 10036)], default);

        var finding = Assert.Single(findings);
        Assert.Equal(orphanId, finding.ObjectId);
        Assert.Equal(OrphanAction.Delete, finding.Action);
        Assert.Equal(OrphanPriority.Prio2, finding.Priority);
        Assert.Equal("customapi", finding.EntityName);
        Assert.Equal($"CustomApi 'av_GenuinelyRemovedApi' ({orphanId})", finding.DisplayName);
    }

    // -- Recreated-uniquename false-positive guard --

    [Fact]
    public async Task DetectAsync_RecreatedCustomApiSameUniqueName_NotReported()
    {
        // CustomApi source has no GUID at all — uniquename is the only local identity. A CustomApi
        // recreated with a new customapiid still has the same uniquename in source, so it must not be
        // reported as an orphan even though its objectid differs from before.
        var liveId = Guid.NewGuid();
        SetupTableNames("customapi", (liveId, "av_AatYourService"));
        Directory.CreateDirectory(Path.Combine(_packageSrcRoot, "customapis", "av_AatYourService"));

        var findings = await _handler.DetectAsync(Ctx(), [(liveId, 10036)], default);

        Assert.Empty(findings);
    }

    // -- KTD5: unresolved identity is a skip, never an orphan --

    [Fact]
    public async Task DetectAsync_CandidateNotFoundInAnyTable_SkippedNotReported()
    {
        // A candidate that resolves to no row in any of the three tables (e.g. it belongs to a
        // different family entirely, or the query genuinely found nothing) is never claimed by this
        // handler — never added to the findings list, not reported as orphaned.
        var unresolvedId = Guid.NewGuid();

        var findings = await _handler.DetectAsync(Ctx(), [(unresolvedId, 10036)], default);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task DetectAsync_CandidateRowExistsButNameIsNull_SkippedNotReported()
    {
        // Part-9 bug #2 shape (docs/solutions/architecture-patterns/orphan-cleanup-two-phase-deploy-
        // pipeline.md): the row IS found in the table, but its identity attribute (here, name) comes
        // back null — this is "couldn't verify," not "verified as gone." Today's CustomApi path
        // (OrphanCleanupService.cs customApiOrphans filter) still has this latent defect; this handler
        // must not carry it forward. A row with a null/empty name must never be reported.
        var candidateId = Guid.NewGuid();
        var entity = new Entity("customapi", candidateId) { ["name"] = null };

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "customapi"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([entity])));

        var findings = await _handler.DetectAsync(Ctx(), [(candidateId, 10036)], default);

        Assert.Empty(findings);
    }

    // -- KTD4: per-table failure isolation --

    [Fact]
    public async Task DetectAsync_ResponsePropertyTableQueryFails_CustomApiAndRequestParameterDetectionUnaffected()
    {
        var customApiId = Guid.NewGuid();
        var paramId = Guid.NewGuid();
        var propId = Guid.NewGuid();

        SetupTableNames("customapi", (customApiId, "av_GenuinelyRemovedApi"));
        SetupTableNames("customapirequestparameter", (paramId, "av_GenuinelyRemovedParam"));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "customapiresponseproperty"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new InvalidOperationException("table unavailable")));

        var findings = await _handler.DetectAsync(Ctx(), [(customApiId, 10036), (paramId, 10037), (propId, 10034)], default);

        Assert.Contains(findings, f => f.ObjectId == customApiId);
        Assert.Contains(findings, f => f.ObjectId == paramId);
        Assert.DoesNotContain(findings, f => f.ObjectId == propId);
        Assert.Contains("table unavailable", _console.Output);
    }

    // -- KTD6: business fault vs infrastructure fault --

    [Fact]
    public async Task DetectAsync_BusinessFault_SkippedSilentlyNoWarning()
    {
        // A well-formed Dataverse business-logic fault (mirrors ResolveOptionSetMetadataIdsAsync's
        // existing pattern) is not evidence any candidate was deleted — it resolves quietly to "no
        // candidates claimed for this table", no operator-facing warning.
        var candidateId = Guid.NewGuid();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "customapi"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new FaultException<OrganizationServiceFault>(new OrganizationServiceFault())));

        var findings = await _handler.DetectAsync(Ctx(), [(candidateId, 10036)], default);

        Assert.Empty(findings);
        Assert.DoesNotContain("Warning", _console.Output);
    }

    [Fact]
    public async Task DetectAsync_InfrastructureFault_WarnsAndSkips()
    {
        // Network/auth/throttle failures must not count as evidence of deletion — they warn and skip,
        // distinct from the business-fault case above (KTD6).
        var candidateId = Guid.NewGuid();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "customapi"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new InvalidOperationException("network timeout")));

        var findings = await _handler.DetectAsync(Ctx(), [(candidateId, 10036)], default);

        Assert.Empty(findings);
        Assert.Contains("network timeout", _console.Output);
        Assert.Contains("Warning", _console.Output);
    }

    // -- Integration: SequenceHint ordering --

    [Fact]
    public async Task DetectAsync_MixedBatch_SequenceHintPlacesChildrenBeforeParent()
    {
        var customApiId = Guid.NewGuid();
        var paramId = Guid.NewGuid();
        var propId = Guid.NewGuid();

        SetupTableNames("customapi", (customApiId, "av_Api"));
        SetupTableNames("customapirequestparameter", (paramId, "av_Param"));
        SetupTableNames("customapiresponseproperty", (propId, "av_Prop"));

        var findings = await _handler.DetectAsync(Ctx(), [(customApiId, 10036), (paramId, 10037), (propId, 10034)], default);

        var byId = findings.ToDictionary(f => f.ObjectId);
        Assert.Equal(3, byId.Count);
        Assert.Equal(1, byId[customApiId].SequenceHint);
        Assert.Equal(0, byId[paramId].SequenceHint);
        Assert.Equal(0, byId[propId].SequenceHint);
    }

    [Fact]
    public async Task DetectAsync_EmptyCandidateList_ReturnsEmptyWithoutQuerying()
    {
        var findings = await _handler.DetectAsync(Ctx(), [], default);

        Assert.Empty(findings);
        await _serviceMock.DidNotReceive().RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Status_IsActive()
    {
        Assert.Equal(HandlerStatus.Active, _handler.Status);
    }
}
