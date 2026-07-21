using System.ServiceModel;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Flowline.Core.Services;
using Flowline.Core.OrphanCleanup;
using Flowline.Core.OrphanCleanup.Handlers;
using Flowline.Core.Models;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests.OrphanCleanup.Handlers;

public class BotHandlerTests : IDisposable
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly TestConsole _console;
    readonly BotHandler _handler;
    readonly string _dataverseSolutionSrcRoot;

    public BotHandlerTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _console.Profile.Width = 400; // avoid word-wrap splitting longer assertion substrings across lines
        _handler = new BotHandler(_console);
        _dataverseSolutionSrcRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_dataverseSolutionSrcRoot);

        // Default: any unconfigured RetrieveMultipleAsync returns empty rather than NSubstitute's null
        // default — real Dataverse never returns a null EntityCollection (mirrors OrphanCleanupServiceTests).
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dataverseSolutionSrcRoot))
            Directory.Delete(_dataverseSolutionSrcRoot, true);
    }

    DetectionContext Ctx(RunMode mode = RunMode.Normal) =>
        new(_dataverseSolutionSrcRoot, _serviceMock, "MySolution", "https://example.crm.dynamics.com", mode, []);

    void SetupBotRow(Guid id, string? schemaName, DateTime? publishedOn)
    {
        var entity = new Entity("bot", id);
        if (schemaName != null) entity["schemaname"] = schemaName;
        if (publishedOn.HasValue) entity["publishedon"] = publishedOn.Value;

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "bot"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([entity])));
    }

    // -- Happy path / edge case: Prio driven by publishedon (KTD8) --

    [Fact]
    public async Task DetectAsync_OrphanedUnpublishedBotNoLocalMatch_ResolvesToManualPrio3()
    {
        var orphanId = Guid.NewGuid();
        SetupBotRow(orphanId, "av_UnpublishedBot", publishedOn: null); // never published — draft

        var findings = (await _handler.DetectAsync(Ctx(), [(orphanId, 10082)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(orphanId, finding.ObjectId);
        Assert.Equal(OrphanAction.Manual, finding.Action);
        Assert.Equal(OrphanPriority.Prio3, finding.Priority);
        Assert.Equal("bot", finding.EntityName);
        Assert.Equal($"Bot 'av_UnpublishedBot' ({orphanId})", finding.DisplayName);
    }

    [Fact]
    public async Task DetectAsync_OrphanedPublishedBotNoLocalMatch_ResolvesToManualPrio2()
    {
        var orphanId = Guid.NewGuid();
        SetupBotRow(orphanId, "av_PublishedBot", publishedOn: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)); // published/live

        var findings = (await _handler.DetectAsync(Ctx(), [(orphanId, 10082)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanAction.Manual, finding.Action);
        Assert.Equal(OrphanPriority.Prio2, finding.Priority);
    }

    // -- Local-source cross-check (schemaname-keyed bots/ folder) --

    [Fact]
    public async Task DetectAsync_BotStillInLocalSource_NotReported()
    {
        var liveId = Guid.NewGuid();
        SetupBotRow(liveId, "msdyn_salesCopilot", publishedOn: null);
        Directory.CreateDirectory(Path.Combine(_dataverseSolutionSrcRoot, "bots", "msdyn_salesCopilot"));

        var findings = (await _handler.DetectAsync(Ctx(), [(liveId, 10082)], default)).Findings;

        Assert.Empty(findings);
    }

    // -- KTD5: unresolved identity is a skip, never an orphan --

    [Fact]
    public async Task DetectAsync_CandidateNotFoundInBotTable_SkippedNotReported()
    {
        var unresolvedId = Guid.NewGuid();

        var findings = (await _handler.DetectAsync(Ctx(), [(unresolvedId, 10082)], default)).Findings;

        Assert.Empty(findings);
    }

    [Fact]
    public async Task DetectAsync_CandidateRowExistsButSchemaNameIsNull_SkippedNotReported()
    {
        // Part-9 bug #2 shape (docs/solutions/architecture-patterns/orphan-cleanup-two-phase-deploy-
        // pipeline.md): the row IS found, but its identity attribute (schemaname) comes back
        // unresolved — this is "couldn't verify," not "verified as gone."
        var candidateId = Guid.NewGuid();
        SetupBotRow(candidateId, schemaName: null, publishedOn: null);

        var result = await _handler.DetectAsync(Ctx(), [(candidateId, 10082)], default);

        Assert.Empty(result.Findings);
        // A row existing in the "bot" table at all is still evidence this candidate is a Bot, so it's
        // claimed even though the null schemaname keeps it out of Findings (KTD5).
        Assert.Contains(candidateId, result.ClaimedIds);
    }

    // -- KTD4: per-handler query isolation (mirrors bug #1's shared-try/catch regression) --

    [Fact]
    public async Task DetectAsync_BotTableQueryFails_DoesNotThrowOrPropagate()
    {
        var candidateId = Guid.NewGuid();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "bot"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new InvalidOperationException("bot table unavailable")));

        var findings = (await _handler.DetectAsync(Ctx(), [(candidateId, 10082)], default)).Findings;

        Assert.Empty(findings);
        Assert.Contains("bot table unavailable", _console.Output);
    }

    [Fact]
    public async Task DetectAsync_BotQueryFails_ConnectionReferenceHandlerDetectionUnaffected()
    {
        // KTD4 isolation, mirroring bug #1's shared-try/catch regression: BotHandler and
        // ConnectionReferenceHandler are now fully separate handler instances with their own query and
        // try/catch — a failure querying "bot" must not affect ConnectionReferenceHandler's own
        // independent "connectionreference" query against the same underlying service.
        var botId = Guid.NewGuid();
        var connectionReferenceId = Guid.NewGuid();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "bot"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new InvalidOperationException("bot table unavailable")));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "connectionreference"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("connectionreference", connectionReferenceId) { ["connectionreferencelogicalname"] = "av_sharepoint" }
            ])));

        var connectionReferenceHandler = new ConnectionReferenceHandler(_console);

        var botFindings = (await _handler.DetectAsync(Ctx(), [(botId, 10082)], default)).Findings;
        var connectionReferenceFindings = (await connectionReferenceHandler.DetectAsync(Ctx(), [(connectionReferenceId, 10064)], default)).Findings;

        Assert.Empty(botFindings);
        var finding = Assert.Single(connectionReferenceFindings);
        Assert.Equal(connectionReferenceId, finding.ObjectId);
    }

    // -- KTD6: business fault vs infrastructure fault --

    [Fact]
    public async Task DetectAsync_BusinessFault_SkippedSilentlyNoWarning()
    {
        var candidateId = Guid.NewGuid();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "bot"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new FaultException<OrganizationServiceFault>(new OrganizationServiceFault())));

        var findings = (await _handler.DetectAsync(Ctx(), [(candidateId, 10082)], default)).Findings;

        Assert.Empty(findings);
        Assert.DoesNotContain("Warning", _console.Output);
    }

    [Fact]
    public async Task DetectAsync_InfrastructureFault_WarnsAndSkips()
    {
        var candidateId = Guid.NewGuid();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "bot"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new InvalidOperationException("network timeout")));

        var findings = (await _handler.DetectAsync(Ctx(), [(candidateId, 10082)], default)).Findings;

        Assert.Empty(findings);
        Assert.Contains("network timeout", _console.Output);
        Assert.Contains("Warning", _console.Output);
    }

    [Fact]
    public async Task DetectAsync_EmptyCandidateList_ReturnsEmptyWithoutQuerying()
    {
        var findings = (await _handler.DetectAsync(Ctx(), [], default)).Findings;

        Assert.Empty(findings);
        await _serviceMock.DidNotReceive().RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Status_IsActive()
    {
        Assert.Equal(HandlerStatus.Active, _handler.Status);
    }

    // -- ClaimedIds: row found (even if suppressed) vs no matching row at all --

    [Fact]
    public async Task DetectAsync_ClaimedIds_IncludesLiveBotStillLocalButNotUnresolvedCandidate()
    {
        // liveId has a matching "bot" row (still declared locally, so suppressed out of Findings) — it
        // must still be claimed. unresolvedId has no matching row in the "bot" table at all — it must
        // not be claimed, so it can fall through to generic fallback.
        var liveId = Guid.NewGuid();
        var unresolvedId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(_dataverseSolutionSrcRoot, "bots", "msdyn_salesCopilot"));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "bot"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("bot", liveId) { ["schemaname"] = "msdyn_salesCopilot" }
            ])));

        var result = await _handler.DetectAsync(Ctx(), [(liveId, 10082), (unresolvedId, 10082)], default);

        Assert.Empty(result.Findings);
        Assert.Contains(liveId, result.ClaimedIds);
        Assert.DoesNotContain(unresolvedId, result.ClaimedIds);
    }
}
