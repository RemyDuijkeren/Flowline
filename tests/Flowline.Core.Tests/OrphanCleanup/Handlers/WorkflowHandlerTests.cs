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

public class WorkflowHandlerTests
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly TestConsole _console;
    readonly WorkflowHandler _handler;

    public WorkflowHandlerTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _console.Profile.Width = 400; // avoid word-wrap splitting longer assertion substrings across lines
        _handler = new WorkflowHandler(_console);

        // Default: any unconfigured RetrieveMultipleAsync returns empty rather than NSubstitute's null
        // default — real Dataverse never returns a null EntityCollection (mirrors OrphanCleanupServiceTests).
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));
    }

    DetectionContext Ctx(RunMode mode = RunMode.Normal) => new(
        PackageSrcRoot: "irrelevant",
        Service: _serviceMock,
        SolutionName: "TestSolution",
        EnvironmentUrl: "https://example.crm.dynamics.com",
        Mode: mode,
        EntityLogicalNames: []);

    void SetupWorkflow(Guid id, string name, int stateCode)
    {
        var entity = new Entity("workflow", id)
        {
            ["name"] = name,
            ["statecode"] = new OptionSetValue(stateCode)
        };

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "workflow"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([entity])));
    }

    [Fact]
    public async Task DetectAsync_ActivatedWorkflow_ReturnsPrio2()
    {
        var id = Guid.NewGuid();
        SetupWorkflow(id, "MyFlow", stateCode: 1); // Activated

        var findings = (await _handler.DetectAsync(Ctx(), [(id, 29)], CancellationToken.None)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(29, finding.ComponentType);
        Assert.Equal(OrphanAction.Delete, finding.Action);
        Assert.Equal(OrphanPriority.Prio2, finding.Priority);
        Assert.Contains("MyFlow", finding.DisplayName);
    }

    [Fact]
    public async Task DetectAsync_DeactivatedWorkflow_ReturnsPrio3()
    {
        var id = Guid.NewGuid();
        SetupWorkflow(id, "MyFlow", stateCode: 0); // Draft/Deactivated

        var findings = (await _handler.DetectAsync(Ctx(), [(id, 29)], CancellationToken.None)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(29, finding.ComponentType);
        Assert.Equal(OrphanAction.Delete, finding.Action);
        Assert.Equal(OrphanPriority.Prio3, finding.Priority);
        Assert.Contains("MyFlow", finding.DisplayName);
    }

    // -- KTD5-equivalent: workflow row not found by the live query is an unresolved default, not an error --

    [Fact]
    public async Task DetectAsync_WorkflowNotFoundInLiveQuery_DefaultsToPrio3WithBareId()
    {
        // Fix5 (code-review): byId.TryGetValue(id, out var entity) returning false — the workflow record
        // wasn't found by the live query — was never exercised; both existing tests always set up the
        // record via SetupWorkflow. No SetupWorkflow call here — the live query returns no matching row.
        var id = Guid.NewGuid();

        var findings = (await _handler.DetectAsync(Ctx(), [(id, 29)], CancellationToken.None)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanPriority.Prio3, finding.Priority);
        Assert.Equal($"Workflow {id}", finding.DisplayName); // bare id, no quoted name
    }

    // -- Fix1 (code-review): fault isolation — a live query fault degrades instead of propagating --

    [Fact]
    public async Task DetectAsync_WorkflowQueryFails_DegradesToPrio3WithBareIdAndWarns()
    {
        var id = Guid.NewGuid();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "workflow"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new InvalidOperationException("workflow table unavailable")));

        var findings = (await _handler.DetectAsync(Ctx(), [(id, 29)], CancellationToken.None)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanPriority.Prio3, finding.Priority);
        Assert.Equal($"Workflow {id}", finding.DisplayName);
        Assert.Contains("workflow table unavailable", _console.Output);
        Assert.Contains("Warning", _console.Output);
    }

    [Fact]
    public void Status_IsActive()
    {
        Assert.Equal(HandlerStatus.Active, _handler.Status);
    }

    [Fact]
    public async Task DetectAsync_NonWorkflowCandidate_IsIgnored()
    {
        var id = Guid.NewGuid();

        var findings = (await _handler.DetectAsync(Ctx(), [(id, 61)], CancellationToken.None)).Findings;

        Assert.Empty(findings);
    }

    [Fact]
    public async Task DetectAsync_NoCandidates_ReturnsEmptyWithoutQuerying()
    {
        var findings = (await _handler.DetectAsync(Ctx(), [], CancellationToken.None)).Findings;

        Assert.Empty(findings);
        _ = _serviceMock.DidNotReceive().RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectAsync_ClaimedIds_EqualsFindingsObjectIds()
    {
        // No suppression path in this handler — every componenttype-29 candidate always produces a
        // finding (Prio3-default when unresolved), so ClaimedIds equals Findings' ObjectIds exactly.
        var id = Guid.NewGuid();
        SetupWorkflow(id, "MyFlow", stateCode: 1);

        var result = await _handler.DetectAsync(Ctx(), [(id, 29)], CancellationToken.None);

        Assert.Equal(result.Findings.Select(f => f.ObjectId).ToHashSet(), result.ClaimedIds);
        Assert.Contains(id, result.ClaimedIds);
    }
}
