using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Moq;
using Flowline.Core.Services;
using Flowline.Core.Models;
using Flowline.Core;

namespace Flowline.Core.Tests;

public class PluginRegistrationServiceTests
{
    private readonly Mock<IOrganizationServiceAsync2> _serviceMock;
    private readonly Mock<IFlowlineOutput> _outputMock;
    private readonly PluginRegistrationService _service;

    public PluginRegistrationServiceTests()
    {
        _serviceMock = new Mock<IOrganizationServiceAsync2>();
        _outputMock = new Mock<IFlowlineOutput>();
        _service = new PluginRegistrationService(_outputMock.Object);

        // Default empty results for all queries
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.IsAny<QueryExpression>()))
            .ReturnsAsync(new EntityCollection());
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.IsAny<QueryExpression>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());

        var defaultMessage = new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" };
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessage")))
            .ReturnsAsync(new EntityCollection([defaultMessage]));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessage"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection([defaultMessage]));

        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter")))
            .ReturnsAsync(new EntityCollection());
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());
    }

    // -- Helpers --

    private Entity ExistingAssembly(Guid id, string version = "1.0.0.0", string? hash = null)
    {
        var e = new Entity("pluginassembly", id);
        e["name"] = "MyPlugin";
        e["version"] = version;
        if (hash != null)
            e["description"] = $"[flowline] sha256={hash}";
        return e;
    }

    private void SetupAssembly(Entity? existing = null)
    {
        if (existing == null)
        {
            _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "pluginassembly")))
                .ReturnsAsync(new EntityCollection());
            _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "pluginassembly"), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EntityCollection());
            var createResponse = new CreateResponse();
            createResponse.Results["id"] = Guid.NewGuid();
            _serviceMock.Setup(x => x.ExecuteAsync(It.IsAny<CreateRequest>()))
                .ReturnsAsync(createResponse);
        }
        else
        {
            _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "pluginassembly")))
                .ReturnsAsync(new EntityCollection(new List<Entity> { existing }));
            _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "pluginassembly"), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EntityCollection(new List<Entity> { existing }));
        }
    }

    private void SetupPluginTypes(params Entity[] types)
    {
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "plugintype")))
            .ReturnsAsync(new EntityCollection(types.ToList()));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "plugintype"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(types.ToList()));
    }

    private void SetupSteps(params Entity[] steps)
    {
        foreach (var s in steps)
        {
            if (!s.Contains("plugintypeid"))
                s["plugintypeid"] = new EntityReference("plugintype", Guid.NewGuid());
            if (!s.Contains("stage"))
                s["stage"] = new OptionSetValue(20);
        }
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep")))
            .ReturnsAsync(new EntityCollection(steps.ToList()));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(steps.ToList()));
    }

    private void SetupImages(params Entity[] images)
    {
        foreach (var i in images)
        {
            if (!i.Contains("sdkmessageprocessingstepid"))
                i["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", Guid.NewGuid());
        }
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstepimage")))
            .ReturnsAsync(new EntityCollection(images.ToList()));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstepimage"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(images.ToList()));
    }

    private PluginAssemblyMetadata Metadata(string name = "MyPlugin", string version = "1.0.0.0", string hash = "deadbeef", params PluginTypeMetadata[] plugins) =>
        new(name, $"{name}, Version={version}", new byte[] { 1, 2, 3 }, hash, version, plugins.ToList(), []);

    // -- Assembly create/update --

    [Fact]
    public async Task SyncSolutionAsync_NewAssembly_CreatesWithSolutionName()
    {
        SetupAssembly();
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock.Object, Metadata(), "MySolution");

        _serviceMock.Verify(x => x.ExecuteAsync(It.Is<CreateRequest>(r =>
            r.Target.LogicalName == "pluginassembly" &&
            r.Target.GetAttributeValue<string>("name") == "MyPlugin" &&
            r["SolutionUniqueName"].ToString() == "MySolution"
        )), Times.Once);
    }

    [Fact]
    public async Task SyncSolutionAsync_ExistingAssembly_UpdatesVersion()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, "1.0.0.0"));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock.Object, Metadata(version: "1.0.0.1"), "MySolution");

        _serviceMock.Verify(x => x.UpdateAsync(It.Is<Entity>(e =>
            e.LogicalName == "pluginassembly" &&
            e.Id == assemblyId &&
            e.GetAttributeValue<string>("version") == "1.0.0.1"
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    // -- Plugin type creation --

    [Fact]
    public async Task SyncSolutionAsync_NewPluginType_CreatesPluginType()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();
        SetupSteps();

        await _service.SyncAsync(_serviceMock.Object, Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", false, [])), "MySolution");

        _serviceMock.Verify(x => x.CreateAsync(It.Is<Entity>(e =>
            e.LogicalName == "plugintype" &&
            e.GetAttributeValue<string>("typename") == "MyNamespace.MyPlugin" &&
            !e.Contains("workflowactivitygroupname")
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    // -- Workflow type creation --

    [Fact]
    public async Task SyncSolutionAsync_NewWorkflowType_SetsWorkflowActivityGroupName()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock.Object, Metadata(plugins: new PluginTypeMetadata("MyActivity", "MyNamespace.MyActivity", true, [])), "MySolution");

        _serviceMock.Verify(x => x.CreateAsync(It.Is<Entity>(e =>
            e.LogicalName == "plugintype" &&
            e.GetAttributeValue<string>("typename") == "MyNamespace.MyActivity" &&
            e.GetAttributeValue<string>("workflowactivitygroupname") == "MyPlugin (1.0.0.0)"
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncSolutionAsync_WorkflowType_DoesNotQuerySteps()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock.Object, Metadata(plugins: new PluginTypeMetadata("MyActivity", "MyNamespace.MyActivity", true, [])), "MySolution");

        _serviceMock.Verify(x => x.RetrieveMultipleAsync(
            It.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep")), Times.Never);
    }

    // -- Deletion of obsolete types --

    [Fact]
    public async Task SyncSolutionAsync_ObsoletePluginType_DeletesStepsThenType()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));

        var obsoleteTypeId = Guid.NewGuid();
        var obsoleteType = new Entity("plugintype", obsoleteTypeId)
        {
            ["typename"] = "Obsolete.Plugin",
            ["isworkflowactivity"] = false
        };
        SetupPluginTypes(obsoleteType);

        var stepId = Guid.NewGuid();
        var obsoleteStep = new Entity("sdkmessageprocessingstep", stepId);
        obsoleteStep["plugintypeid"] = new EntityReference("plugintype", obsoleteTypeId);
        SetupSteps(obsoleteStep);

        await _service.SyncAsync(_serviceMock.Object, Metadata(), "MySolution"); // no plugins in assembly

        _serviceMock.Verify(x => x.DeleteAsync("sdkmessageprocessingstep", stepId, It.IsAny<CancellationToken>()), Times.Once);
        _serviceMock.Verify(x => x.DeleteAsync("plugintype", obsoleteTypeId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncSolutionAsync_ObsoleteWorkflowType_DeletesTypeWithoutQueryingSteps()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));

        var obsoleteTypeId = Guid.NewGuid();
        var obsoleteType = new Entity("plugintype", obsoleteTypeId)
        {
            ["typename"] = "Obsolete.Activity",
            ["isworkflowactivity"] = true
        };
        SetupPluginTypes(obsoleteType);

        await _service.SyncAsync(_serviceMock.Object, Metadata(), "MySolution");

        _serviceMock.Verify(x => x.RetrieveMultipleAsync(
            It.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep")), Times.Never);
        _serviceMock.Verify(x => x.DeleteAsync("plugintype", obsoleteTypeId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // -- DLL as source of truth: all orphaned steps deleted --

    [Fact]
    public async Task SyncSolutionAsync_PluginWithNoSteps_DeletesAllExistingSteps()
    {
        // [Entity] removed to disable a plugin — Flowline deletes all steps for that type
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        
        var pluginType = new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "MyNamespace.MyPlugin", ["isworkflowactivity"] = false };
        SetupPluginTypes(pluginType);
        
        var stepId = Guid.NewGuid();
        var existingStep = new Entity("sdkmessageprocessingstep", stepId) { ["name"] = "Old step", ["plugintypeid"] = pluginType.ToEntityReference() };
        SetupSteps(existingStep);

        await _service.SyncAsync(_serviceMock.Object, Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", false, [])), "MySolution");

        _serviceMock.Verify(x => x.DeleteAsync("sdkmessageprocessingstep", stepId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncSolutionAsync_PluginWithSteps_DeletesOrphanedSteps()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        
        var pluginType = new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "MyNamespace.MyPlugin", ["isworkflowactivity"] = false };
        SetupPluginTypes(pluginType);

        var orphanId = Guid.NewGuid();
        SetupSteps(new Entity("sdkmessageprocessingstep", orphanId) { ["name"] = "Orphaned step", ["plugintypeid"] = pluginType.ToEntityReference() });
        SetupImages();

        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessage")))
            .ReturnsAsync(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } }));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessage"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } }));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter")))
            .ReturnsAsync(new EntityCollection());
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of contact", "Update", "contact", 20, 0, 1, null, null, [], []);

        await _service.SyncAsync(_serviceMock.Object, Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", false, [step])), "MySolution");

        _serviceMock.Verify(x => x.DeleteAsync("sdkmessageprocessingstep", orphanId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // -- Hash-based change detection --

    [Fact]
    public async Task SyncAsync_UnchangedAssembly_SkipsUpload()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "abc123"));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock.Object, Metadata(hash: "abc123"), "MySolution");

        _serviceMock.Verify(x => x.UpdateAsync(It.Is<Entity>(e => e.LogicalName == "pluginassembly"), It.IsAny<CancellationToken>()), Times.Never);
        _outputMock.Verify(x => x.Skip(It.Is<string>(s => s.Contains("unchanged"))), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_ChangedAssembly_UploadsNewContent()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock.Object, Metadata(hash: "newhash"), "MySolution");

        _serviceMock.Verify(x => x.UpdateAsync(It.Is<Entity>(e =>
            e.LogicalName == "pluginassembly" &&
            e.GetAttributeValue<string>("description") == "[flowline] sha256=newhash"
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    // -- Save mode: report skipped deletions --

    [Fact]
    public async Task SyncSolutionAsync_SaveMode_ReportsSkippedStepAndTypeDeletions()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));

        var obsoleteTypeId = Guid.NewGuid();
        SetupPluginTypes(new Entity("plugintype", obsoleteTypeId)
        {
            ["typename"] = "Obsolete.Plugin",
            ["isworkflowactivity"] = false
        });

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of contact", "Update", "contact", 20, 0, 1, null, null, [], []);
        var plugin = new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", false, [step]);

        var existingStepId = Guid.NewGuid();
        SetupSteps(new Entity("sdkmessageprocessingstep", existingStepId) { ["name"] = "Orphaned step", ["plugintypeid"] = new EntityReference("plugintype", obsoleteTypeId) });
        SetupImages();

        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessage")))
            .ReturnsAsync(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } }));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessage"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } }));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter")))
            .ReturnsAsync(new EntityCollection());
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());

        await _service.SyncAsync(_serviceMock.Object, Metadata(plugins: plugin), "MySolution", save: true);

        _serviceMock.Verify(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _outputMock.Verify(x => x.Skip(It.Is<string>(s => s.Contains("Orphaned step"))), Times.AtLeastOnce);
        _outputMock.Verify(x => x.Skip(It.Is<string>(s => s.Contains("Obsolete.Plugin"))), Times.AtLeastOnce);
    }

    [Fact]
    public async Task SyncAsync_DeletePhaseCompletesBeforeAssemblyUpdate()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));

        var obsoleteTypeId = Guid.NewGuid();
        SetupPluginTypes(new Entity("plugintype", obsoleteTypeId)
        {
            ["typename"] = "Obsolete.Plugin",
            ["isworkflowactivity"] = false
        });

        var obsoleteStepId = Guid.NewGuid();
        SetupSteps(new Entity("sdkmessageprocessingstep", obsoleteStepId)
        {
            ["name"] = "Obsolete.Step",
            ["plugintypeid"] = new EntityReference("plugintype", obsoleteTypeId)
        });

        var callOrder = new List<string>();
        _serviceMock.Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid, CancellationToken>((logicalName, _, _) => callOrder.Add($"delete:{logicalName}"))
            .Returns(Task.CompletedTask);
        _serviceMock.Setup(x => x.UpdateAsync(It.Is<Entity>(e => e.LogicalName == "pluginassembly"), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((_, _) => callOrder.Add("update:pluginassembly"))
            .Returns(Task.CompletedTask);

        await _service.SyncAsync(_serviceMock.Object, Metadata(hash: "newhash"), "MySolution");

        var updateIndex = callOrder.IndexOf("update:pluginassembly");
        Assert.True(updateIndex > 0);
        Assert.DoesNotContain(callOrder.Skip(updateIndex + 1), c => c.StartsWith("delete:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SyncAsync_EmitsStructuredWaveLogs_ForDeleteBarrierAndUpserts()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));

        var obsoleteTypeId = Guid.NewGuid();
        SetupPluginTypes(new Entity("plugintype", obsoleteTypeId)
        {
            ["typename"] = "Obsolete.Plugin",
            ["isworkflowactivity"] = false
        });
        SetupSteps(new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["name"] = "Obsolete.Step",
            ["plugintypeid"] = new EntityReference("plugintype", obsoleteTypeId)
        });

        await _service.SyncAsync(_serviceMock.Object, Metadata(hash: "newhash"), "MySolution");

        _outputMock.Verify(x => x.Verbose(It.Is<string>(s => s.Contains("phase=2 wave=images status=completed"))), Times.Once);
        _outputMock.Verify(x => x.Verbose(It.Is<string>(s => s.Contains("phase=2 wave=steps status=completed"))), Times.Once);
        _outputMock.Verify(x => x.Verbose(It.Is<string>(s => s.Contains("phase=2 wave=types-apis-workflows status=completed"))), Times.Once);
        _outputMock.Verify(x => x.Verbose(It.Is<string>(s => s.Contains("phase=3 wave=plugin-types-steps-images status=completed"))), Times.Once);
        _outputMock.Verify(x => x.Verbose(It.Is<string>(s => s.Contains("phase=3 wave=custom-api-and-params status=completed"))), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_DeletePhase_SkipsNonModifiableStageSteps()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));

        var obsoleteTypeId = Guid.NewGuid();
        SetupPluginTypes(new Entity("plugintype", obsoleteTypeId)
        {
            ["typename"] = "Obsolete.Plugin",
            ["isworkflowactivity"] = false
        });

        var protectedStepId = Guid.NewGuid();
        SetupSteps(new Entity("sdkmessageprocessingstep", protectedStepId)
        {
            ["name"] = "Protected.Step",
            ["plugintypeid"] = new EntityReference("plugintype", obsoleteTypeId),
            ["stage"] = new OptionSetValue(30)
        });

        await _service.SyncAsync(_serviceMock.Object, Metadata(hash: "newhash"), "MySolution");

        _serviceMock.Verify(x => x.DeleteAsync("sdkmessageprocessingstep", protectedStepId, It.IsAny<CancellationToken>()), Times.Never);
        _serviceMock.Verify(x => x.DeleteAsync("plugintype", obsoleteTypeId, It.IsAny<CancellationToken>()), Times.Never);
    }
}
