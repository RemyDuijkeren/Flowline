using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Flowline.Core.Services;
using Flowline.Core.Services.OrphanCleanup;
using Flowline.Core.Services.OrphanCleanup.Handlers;

namespace Flowline.Core.Tests.OrphanCleanup.Handlers;

public class WorkflowHandlerTests
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly WorkflowHandler _handler;

    public WorkflowHandlerTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _handler = new WorkflowHandler();

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

        var findings = await _handler.DetectAsync(Ctx(), [(id, 29)], CancellationToken.None);

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

        var findings = await _handler.DetectAsync(Ctx(), [(id, 29)], CancellationToken.None);

        var finding = Assert.Single(findings);
        Assert.Equal(29, finding.ComponentType);
        Assert.Equal(OrphanAction.Delete, finding.Action);
        Assert.Equal(OrphanPriority.Prio3, finding.Priority);
        Assert.Contains("MyFlow", finding.DisplayName);
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

        var findings = await _handler.DetectAsync(Ctx(), [(id, 61)], CancellationToken.None);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task DetectAsync_NoCandidates_ReturnsEmptyWithoutQuerying()
    {
        var findings = await _handler.DetectAsync(Ctx(), [], CancellationToken.None);

        Assert.Empty(findings);
        _ = _serviceMock.DidNotReceive().RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>());
    }
}
