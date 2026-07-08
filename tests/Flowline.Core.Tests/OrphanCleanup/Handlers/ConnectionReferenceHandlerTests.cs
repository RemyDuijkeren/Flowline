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

public class ConnectionReferenceHandlerTests : IDisposable
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly TestConsole _console;
    readonly ConnectionReferenceHandler _handler;
    readonly string _packageSrcRoot;

    public ConnectionReferenceHandlerTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _console.Profile.Width = 400; // avoid word-wrap splitting longer assertion substrings across lines
        _handler = new ConnectionReferenceHandler(_console);
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

    void SetupConnectionReferenceRow(Guid id, string? logicalName)
    {
        var entity = new Entity("connectionreference", id);
        if (logicalName != null) entity["connectionreferencelogicalname"] = logicalName;

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "connectionreference"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([entity])));
    }

    static void WriteConnectionReferencesXml(string packageSrcRoot, params string[] logicalNames)
    {
        var otherDir = Path.Combine(packageSrcRoot, "Other");
        Directory.CreateDirectory(otherDir);
        var refsXml = string.Concat(logicalNames.Select(n => $"<connectionreference connectionreferencelogicalname=\"{n}\"><connectorid>/providers/Microsoft.PowerApps/apis/shared_x</connectorid></connectionreference>"));
        File.WriteAllText(Path.Combine(otherDir, "Customizations.xml"),
            $"<ImportExportXml><connectionreferences>{refsXml}</connectionreferences></ImportExportXml>");
    }

    // -- Happy path --

    [Fact]
    public async Task DetectAsync_OrphanedConnectionReferenceNoLocalMatch_ResolvesToManualPrio2()
    {
        var orphanId = Guid.NewGuid();
        SetupConnectionReferenceRow(orphanId, "av_sharepoint");

        var findings = await _handler.DetectAsync(Ctx(), [(orphanId, 10064)], default);

        var finding = Assert.Single(findings);
        Assert.Equal(orphanId, finding.ObjectId);
        Assert.Equal(OrphanAction.Manual, finding.Action);
        Assert.Equal(OrphanPriority.Prio2, finding.Priority);
        Assert.Equal("connectionreference", finding.EntityName);
        Assert.Equal($"ConnectionReference 'av_sharepoint' ({orphanId})", finding.DisplayName);
    }

    // -- Local-source cross-check (inline Customizations.xml <connectionreferences> section) --

    [Fact]
    public async Task DetectAsync_ConnectionReferenceStillInCustomizationsXml_NotReported()
    {
        var liveId = Guid.NewGuid();
        SetupConnectionReferenceRow(liveId, "av_sharepoint");
        WriteConnectionReferencesXml(_packageSrcRoot, "av_sharepoint");

        var findings = await _handler.DetectAsync(Ctx(), [(liveId, 10064)], default);

        Assert.Empty(findings);
    }

    // -- KTD5: unresolved identity is a skip, never an orphan --

    [Fact]
    public async Task DetectAsync_CandidateNotFoundInConnectionReferenceTable_SkippedNotReported()
    {
        var unresolvedId = Guid.NewGuid();

        var findings = await _handler.DetectAsync(Ctx(), [(unresolvedId, 10064)], default);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task DetectAsync_CandidateRowExistsButLogicalNameIsNull_SkippedNotReported()
    {
        // Part-9 bug #2 shape (docs/solutions/architecture-patterns/orphan-cleanup-two-phase-deploy-
        // pipeline.md): the row IS found, but its identity attribute (connectionreferencelogicalname)
        // comes back unresolved — this is "couldn't verify," not "verified as gone."
        var candidateId = Guid.NewGuid();
        SetupConnectionReferenceRow(candidateId, logicalName: null);

        var findings = await _handler.DetectAsync(Ctx(), [(candidateId, 10064)], default);

        Assert.Empty(findings);
    }

    // -- KTD4: per-handler query isolation (mirrors bug #1's shared-try/catch regression) --

    [Fact]
    public async Task DetectAsync_ConnectionReferenceTableQueryFails_DoesNotThrowOrPropagate()
    {
        var candidateId = Guid.NewGuid();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "connectionreference"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new InvalidOperationException("connectionreference table unavailable")));

        var findings = await _handler.DetectAsync(Ctx(), [(candidateId, 10064)], default);

        Assert.Empty(findings);
        Assert.Contains("connectionreference table unavailable", _console.Output);
    }

    [Fact]
    public async Task DetectAsync_ConnectionReferenceQueryFails_BotHandlerDetectionUnaffected()
    {
        // KTD4 isolation, mirroring bug #1's shared-try/catch regression, from the other direction than
        // BotHandlerTests' equivalent case: a failure querying "connectionreference" must not affect
        // BotHandler's own independent "bot" query against the same underlying service.
        var connectionReferenceId = Guid.NewGuid();
        var botId = Guid.NewGuid();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "connectionreference"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new InvalidOperationException("connectionreference table unavailable")));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "bot"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("bot", botId) { ["schemaname"] = "msdyn_salesCopilot" } // publishedon unset — draft
            ])));

        var botHandler = new BotHandler(_console);

        var connectionReferenceFindings = await _handler.DetectAsync(Ctx(), [(connectionReferenceId, 10064)], default);
        var botFindings = await botHandler.DetectAsync(Ctx(), [(botId, 10082)], default);

        Assert.Empty(connectionReferenceFindings);
        var finding = Assert.Single(botFindings);
        Assert.Equal(botId, finding.ObjectId);
    }

    // -- KTD6: business fault vs infrastructure fault --

    [Fact]
    public async Task DetectAsync_BusinessFault_SkippedSilentlyNoWarning()
    {
        var candidateId = Guid.NewGuid();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "connectionreference"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new FaultException<OrganizationServiceFault>(new OrganizationServiceFault())));

        var findings = await _handler.DetectAsync(Ctx(), [(candidateId, 10064)], default);

        Assert.Empty(findings);
        Assert.DoesNotContain("Warning", _console.Output);
    }

    [Fact]
    public async Task DetectAsync_InfrastructureFault_WarnsAndSkips()
    {
        var candidateId = Guid.NewGuid();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "connectionreference"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new InvalidOperationException("network timeout")));

        var findings = await _handler.DetectAsync(Ctx(), [(candidateId, 10064)], default);

        Assert.Empty(findings);
        Assert.Contains("network timeout", _console.Output);
        Assert.Contains("Warning", _console.Output);
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
