using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Flowline.Core.Services;
using Flowline.Core.Services.OrphanCleanup;
using Flowline.Core.Services.OrphanCleanup.Handlers;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests.OrphanCleanup.Handlers;

// Characterizes RoleHandler against today's OrphanCleanupServiceTests Role cases
// (RunPreImportAsync_RoleGenuinelyRemoved_ReportedAsManualRoleWithResolvedName) — this handler must
// reproduce that exact behavior, plus the new Prio3/SequenceHint/Timing fields (U7 plan). Role's
// id-in-LocalComponents suppression happens centrally in the orchestrator's raw-candidate diff before a
// candidate ever reaches a handler, so there is no dedicated false-positive guard scenario here.
public class RoleHandlerTests
{
    const int RoleComponentType = 20;

    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly TestConsole _console;
    readonly RoleHandler _handler;

    public RoleHandlerTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _console.Profile.Width = 400; // avoid word-wrap splitting longer assertion substrings across lines
        _handler = new RoleHandler(_console);

        // Default: any unconfigured RetrieveMultipleAsync returns empty rather than NSubstitute's null
        // default — real Dataverse never returns a null EntityCollection (mirrors OrphanCleanupServiceTests).
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));
    }

    DetectionContext Ctx() => new(
        PackageSrcRoot: "irrelevant",
        Service: _serviceMock,
        SolutionName: "MySolution",
        EnvironmentUrl: "https://example.crm.dynamics.com",
        Mode: RunMode.Normal,
        EntityLogicalNames: []);

    void SetupRoleName(Guid id, string name)
    {
        var entity = new Entity("role", id) { ["name"] = name };

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "role"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([entity])));
    }

    [Fact]
    public void Status_IsActive()
    {
        Assert.Equal(HandlerStatus.Active, _handler.Status);
    }

    [Fact]
    public async Task DetectAsync_OrphanedRole_ManualPrio3WithResolvedName()
    {
        var roleId = Guid.NewGuid();
        SetupRoleName(roleId, "Custom Sales Role");

        var findings = (await _handler.DetectAsync(Ctx(), [(roleId, RoleComponentType)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(roleId, finding.ObjectId);
        Assert.Equal(RoleComponentType, finding.ComponentType);
        Assert.Equal(OrphanAction.Manual, finding.Action);
        Assert.Equal(OrphanPriority.Prio3, finding.Priority);
        Assert.Equal(0, finding.SequenceHint);
        Assert.Equal(OrphanTiming.PreImportEligible, finding.Timing);
        Assert.Equal($"Role 'Custom Sales Role' ({roleId})", finding.DisplayName);
    }

    [Fact]
    public async Task DetectAsync_OrphanedRoleUnresolvedName_FallsBackToBareGuid()
    {
        var roleId = Guid.NewGuid();
        // No SetupRoleName call — the live query returns no matching role record.

        var findings = (await _handler.DetectAsync(Ctx(), [(roleId, RoleComponentType)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanAction.Manual, finding.Action);
        Assert.Equal(OrphanPriority.Prio3, finding.Priority);
        Assert.Equal($"Role {roleId}", finding.DisplayName);
    }

    // -- Fix1 (code-review): fault isolation — RoleHandler is a Pass-1 (componenttype-gated) handler that
    // previously had no try/catch around its name-resolution query; an infrastructure fault used to
    // propagate uncaught through DispatchToHandlersAsync, aborting the whole deploy. It's now caught
    // (KTD6) and degrades to the same bare-id fallback the unresolved-name path above already produces —
    // no new fallback shape. --

    [Fact]
    public async Task DetectAsync_RoleNameQueryFails_DegradesToBareIdFindingAndWarns()
    {
        var roleId = Guid.NewGuid();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "role"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<EntityCollection>(new InvalidOperationException("role table unavailable")));

        var findings = (await _handler.DetectAsync(Ctx(), [(roleId, RoleComponentType)], default)).Findings;

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanAction.Manual, finding.Action);
        Assert.Equal(OrphanPriority.Prio3, finding.Priority);
        Assert.Equal($"Role {roleId}", finding.DisplayName);
        Assert.Contains("role table unavailable", _console.Output);
        Assert.Contains("Warning", _console.Output);
    }

    [Fact]
    public async Task DetectAsync_NonRoleCandidate_NotClaimed()
    {
        var webResourceId = Guid.NewGuid();

        var findings = (await _handler.DetectAsync(Ctx(), [(webResourceId, 61)], default)).Findings;

        Assert.Empty(findings);
    }

    [Fact]
    public async Task DetectAsync_NoCandidates_ReturnsEmptyWithoutQuerying()
    {
        var findings = (await _handler.DetectAsync(Ctx(), [], default)).Findings;

        Assert.Empty(findings);
        _ = _serviceMock.DidNotReceive().RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DetectAsync_ClaimedIds_EqualsFindingsObjectIds()
    {
        // No suppression path in this handler — every componenttype-20 candidate always produces a
        // finding, so ClaimedIds equals Findings' ObjectIds exactly.
        var roleId = Guid.NewGuid();
        SetupRoleName(roleId, "Custom Sales Role");

        var result = await _handler.DetectAsync(Ctx(), [(roleId, RoleComponentType)], default);

        Assert.Equal(result.Findings.Select(f => f.ObjectId).ToHashSet(), result.ClaimedIds);
        Assert.Contains(roleId, result.ClaimedIds);
    }
}
