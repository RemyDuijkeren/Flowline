using Flowline.Core.Services;
using Flowline.Core.Services.OrphanCleanup;
using Flowline.Core.Services.OrphanCleanup.Handlers;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;

namespace Flowline.Core.Tests.OrphanCleanup.Handlers;

// Characterization + Prio coverage for U2: PluginAssemblyFamilyHandler migrates PluginAssembly (91) /
// PluginType (90) / Step (92) / StepImage (93) detection from OrphanCleanupService's
// NameResolvableTypes/ExecutionOrder, preserving today's exact Auto/Delete output and ordering, plus
// the new Prio1/Prio2/Prio3 axis (KTD8). Tests call DetectAsync directly — execution (DeleteAsync,
// dependency-fault deferral) stays in the orchestrator (U9), so this suite asserts only on the
// returned HandlerFinding, never on service-mutation calls.
public class PluginAssemblyFamilyHandlerTests
{
    readonly IOrganizationServiceAsync2 _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
    readonly PluginAssemblyFamilyHandler _handler = new();

    public PluginAssemblyFamilyHandlerTests()
    {
        // Default: any unconfigured RetrieveMultipleAsync returns empty rather than NSubstitute's null
        // default — real Dataverse never returns a null EntityCollection. Matches OrphanCleanupServiceTests'
        // own convention.
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));
    }

    DetectionContext Ctx(RunMode mode = RunMode.Normal) =>
        new("unused-package-src-root", _serviceMock, "MySolution", "https://example.crm.dynamics.com", mode, []);

    void SetupName(string entityLogicalName, string nameAttribute, Guid id, string name)
    {
        var entity = new Entity(entityLogicalName, id) { [nameAttribute] = name };
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == entityLogicalName && q.ColumnSet.Columns.Contains(nameAttribute)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([entity])));
    }

    void SetupStepStates(params (Guid StepId, Guid PluginTypeId, bool Enabled)[] steps)
    {
        var entities = steps.Select(s => new Entity("sdkmessageprocessingstep", s.StepId)
        {
            ["plugintypeid"] = new EntityReference("plugintype", s.PluginTypeId),
            ["statecode"]    = new OptionSetValue(s.Enabled ? 0 : 1),
        }).ToList();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep" && q.ColumnSet.Columns.Contains("statecode")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }

    [Fact]
    public async Task DetectAsync_OrphanedPluginAssembly_ReturnsDeleteWithResolvedName()
    {
        var id = Guid.NewGuid();
        SetupName("pluginassembly", "name", id, "MyPlugins.dll");

        var findings = await _handler.DetectAsync(Ctx(), [(id, 91)], default);

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanAction.Delete, finding.Action);
        // Exact match, not Contains: this is the characterization gate — the display format must stay
        // byte-identical to OrphanCleanupService.TypeName's "{label} '{detail}' ({id})" shape.
        Assert.Equal($"PluginAssembly 'MyPlugins.dll' ({id})", finding.DisplayName);
    }

    [Fact]
    public async Task DetectAsync_OrphanedPluginAssemblyWithUnresolvableName_FallsBackToBareIdFormat()
    {
        // No SetupName call — the live name query returns empty, matching today's unresolved-name
        // fallback branch in OrphanCleanupService.TypeName ("{label} {id}", no quoted detail).
        var id = Guid.NewGuid();

        var findings = await _handler.DetectAsync(Ctx(), [(id, 91)], default);

        var finding = Assert.Single(findings);
        Assert.Equal($"PluginAssembly {id}", finding.DisplayName);
    }

    [Fact]
    public async Task DetectAsync_OrphanedPluginType_ReturnsDeleteWithResolvedName()
    {
        var id = Guid.NewGuid();
        SetupName("plugintype", "typename", id, "MyNamespace.MyPlugin");

        var findings = await _handler.DetectAsync(Ctx(), [(id, 90)], default);

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanAction.Delete, finding.Action);
        Assert.Contains("MyNamespace.MyPlugin", finding.DisplayName);
    }

    [Fact]
    public async Task DetectAsync_OrphanedStep_ReturnsDeleteWithResolvedName()
    {
        var id = Guid.NewGuid();
        SetupName("sdkmessageprocessingstep", "name", id, "MyPlugin: Create of account");

        var findings = await _handler.DetectAsync(Ctx(), [(id, 92)], default);

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanAction.Delete, finding.Action);
        Assert.Contains("MyPlugin: Create of account", finding.DisplayName);
    }

    [Fact]
    public async Task DetectAsync_OrphanedStepImage_ReturnsDeleteWithResolvedName()
    {
        var id = Guid.NewGuid();
        SetupName("sdkmessageprocessingstepimage", "name", id, "PreImage");

        var findings = await _handler.DetectAsync(Ctx(), [(id, 93)], default);

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanAction.Delete, finding.Action);
        Assert.Contains("PreImage", finding.DisplayName);
    }

    [Fact]
    public async Task DetectAsync_CandidateOutsideFamily_IsNotClaimed()
    {
        var findings = await _handler.DetectAsync(Ctx(), [(Guid.NewGuid(), 61)], default); // 61 = WebResource

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

        var findings = await _handler.DetectAsync(
            Ctx(RunMode.NoDelete),
            [(assemblyId, 91), (typeId, 90), (stepId, 92), (imageId, 93)],
            default);

        Assert.Equal(4, findings.Count);
        Assert.All(findings, f => Assert.Equal(OrphanPriority.Prio1, f.Priority));
    }

    [Fact]
    public async Task DetectAsync_LivePluginTypeHasOnlyDisabledSteps_ReturnsPrio3NotPrio2()
    {
        var typeId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        SetupStepStates((stepId, typeId, false));

        var findings = await _handler.DetectAsync(Ctx(), [(typeId, 90)], default);

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanPriority.Prio3, finding.Priority);
    }

    [Fact]
    public async Task DetectAsync_LivePluginTypeHasEnabledStep_ReturnsPrio2()
    {
        var typeId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        SetupStepStates((stepId, typeId, true));

        var findings = await _handler.DetectAsync(Ctx(), [(typeId, 90)], default);

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanPriority.Prio2, finding.Priority);
    }

    [Fact]
    public async Task DetectAsync_LiveStepIsEnabled_ReturnsPrio2()
    {
        var stepId = Guid.NewGuid();
        SetupStepStates((stepId, Guid.NewGuid(), true));

        var findings = await _handler.DetectAsync(Ctx(), [(stepId, 92)], default);

        var finding = Assert.Single(findings);
        Assert.Equal(OrphanPriority.Prio2, finding.Priority);
    }

    [Fact]
    public async Task DetectAsync_LiveStepIsDisabled_ReturnsPrio3()
    {
        var stepId = Guid.NewGuid();
        SetupStepStates((stepId, Guid.NewGuid(), false));

        var findings = await _handler.DetectAsync(Ctx(), [(stepId, 92)], default);

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

        var findings = await _handler.DetectAsync(Ctx(), [(imageId, 93), (assemblyId, 91)], default);

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

        var findings = await _handler.DetectAsync(
            Ctx(),
            [(assemblyId, 91), (typeId, 90), (stepId, 92), (imageId, 93)],
            default);

        var byType = findings.ToDictionary(f => f.ComponentType, f => f.SequenceHint);
        Assert.Equal(0, byType[93]); // StepImage
        Assert.Equal(1, byType[92]); // Step
        Assert.Equal(2, byType[90]); // PluginType
        Assert.Equal(3, byType[91]); // PluginAssembly
    }
}
