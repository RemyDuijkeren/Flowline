using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Moq;
using Flowline.Core.Services;
using Flowline.Core.Models;

namespace Flowline.Core.Tests;

public class PluginSyncServiceTests
{
    private readonly Mock<IOrganizationServiceAsync2> _serviceMock;
    private readonly Mock<IAssemblyAnalysisService> _analysisServiceMock;
    private readonly PluginSyncService _service;

    public PluginSyncServiceTests()
    {
        _serviceMock = new Mock<IOrganizationServiceAsync2>();
        _analysisServiceMock = new Mock<IAssemblyAnalysisService>();
        _service = new PluginSyncService(_analysisServiceMock.Object);
    }

    // -- Helpers --

    private Entity ExistingAssembly(Guid id, string version = "1.0.0.0")
    {
        var e = new Entity("pluginassembly", id);
        e["name"] = "MyPlugin";
        e["version"] = version;
        return e;
    }

    private void SetupAssembly(Entity? existing = null)
    {
        if (existing == null)
        {
            _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "pluginassembly")))
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
        }
    }

    private void SetupPluginTypes(params Entity[] types)
    {
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "plugintype")))
            .ReturnsAsync(new EntityCollection(types.ToList()));
    }

    private void SetupSteps(params Entity[] steps)
    {
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep")))
            .ReturnsAsync(new EntityCollection(steps.ToList()));
    }

    private PluginAssemblyMetadata Metadata(string name = "MyPlugin", string version = "1.0.0.0", params PluginTypeMetadata[] plugins) =>
        new(name, $"{name}, Version={version}", new byte[] { 1, 2, 3 }, version, IsolationMode.Sandbox, plugins.ToList());

    // -- Assembly create/update --

    [Fact]
    public async Task SyncSolutionAsync_NewAssembly_CreatesWithSolutionName()
    {
        SetupAssembly();
        SetupPluginTypes();
        _analysisServiceMock.Setup(x => x.Analyze(It.IsAny<string>(), It.IsAny<IsolationMode>()))
            .Returns(Metadata());

        await _service.SyncSolutionAsync(_serviceMock.Object, "MyPlugin.dll", "MySolution", IsolationMode.Sandbox);

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
        _analysisServiceMock.Setup(x => x.Analyze(It.IsAny<string>(), It.IsAny<IsolationMode>()))
            .Returns(Metadata(version: "1.0.0.1"));

        await _service.SyncSolutionAsync(_serviceMock.Object, "MyPlugin.dll", "MySolution", IsolationMode.Sandbox);

        _serviceMock.Verify(x => x.UpdateAsync(It.Is<Entity>(e =>
            e.LogicalName == "pluginassembly" &&
            e.Id == assemblyId &&
            e.GetAttributeValue<string>("version") == "1.0.0.1"
        )), Times.Once);
    }

    // -- Plugin type creation --

    [Fact]
    public async Task SyncSolutionAsync_NewPluginType_CreatesPluginType()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();
        SetupSteps();
        _analysisServiceMock.Setup(x => x.Analyze(It.IsAny<string>(), It.IsAny<IsolationMode>()))
            .Returns(Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", false, [])));

        await _service.SyncSolutionAsync(_serviceMock.Object, "MyPlugin.dll", "MySolution", IsolationMode.Sandbox);

        _serviceMock.Verify(x => x.CreateAsync(It.Is<Entity>(e =>
            e.LogicalName == "plugintype" &&
            e.GetAttributeValue<string>("typename") == "MyNamespace.MyPlugin" &&
            !e.Contains("workflowactivitygroupname")
        )), Times.Once);
    }

    // -- Workflow type creation --

    [Fact]
    public async Task SyncSolutionAsync_NewWorkflowType_SetsWorkflowActivityGroupName()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();
        _analysisServiceMock.Setup(x => x.Analyze(It.IsAny<string>(), It.IsAny<IsolationMode>()))
            .Returns(Metadata(plugins: new PluginTypeMetadata("MyActivity", "MyNamespace.MyActivity", true, [])));

        await _service.SyncSolutionAsync(_serviceMock.Object, "MyPlugin.dll", "MySolution", IsolationMode.Sandbox);

        _serviceMock.Verify(x => x.CreateAsync(It.Is<Entity>(e =>
            e.LogicalName == "plugintype" &&
            e.GetAttributeValue<string>("typename") == "MyNamespace.MyActivity" &&
            e.GetAttributeValue<string>("workflowactivitygroupname") == "MyPlugin (1.0.0.0)"
        )), Times.Once);
    }

    [Fact]
    public async Task SyncSolutionAsync_WorkflowType_DoesNotQuerySteps()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();
        _analysisServiceMock.Setup(x => x.Analyze(It.IsAny<string>(), It.IsAny<IsolationMode>()))
            .Returns(Metadata(plugins: new PluginTypeMetadata("MyActivity", "MyNamespace.MyActivity", true, [])));

        await _service.SyncSolutionAsync(_serviceMock.Object, "MyPlugin.dll", "MySolution", IsolationMode.Sandbox);

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
        SetupSteps(obsoleteStep);

        _analysisServiceMock.Setup(x => x.Analyze(It.IsAny<string>(), It.IsAny<IsolationMode>()))
            .Returns(Metadata()); // no plugins in assembly

        await _service.SyncSolutionAsync(_serviceMock.Object, "MyPlugin.dll", "MySolution", IsolationMode.Sandbox);

        _serviceMock.Verify(x => x.DeleteAsync("sdkmessageprocessingstep", stepId), Times.Once);
        _serviceMock.Verify(x => x.DeleteAsync("plugintype", obsoleteTypeId), Times.Once);
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

        _analysisServiceMock.Setup(x => x.Analyze(It.IsAny<string>(), It.IsAny<IsolationMode>()))
            .Returns(Metadata());

        await _service.SyncSolutionAsync(_serviceMock.Object, "MyPlugin.dll", "MySolution", IsolationMode.Sandbox);

        _serviceMock.Verify(x => x.RetrieveMultipleAsync(
            It.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep")), Times.Never);
        _serviceMock.Verify(x => x.DeleteAsync("plugintype", obsoleteTypeId), Times.Once);
    }

    // -- DLL as source of truth: all orphaned steps deleted --

    [Fact]
    public async Task SyncSolutionAsync_PluginWithNoSteps_DeletesAllExistingSteps()
    {
        // [Entity] removed to disable a plugin — Flowline deletes all steps for that type
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();
        var stepId = Guid.NewGuid();
        SetupSteps(new Entity("sdkmessageprocessingstep", stepId) { ["name"] = "Old step" });
        _analysisServiceMock.Setup(x => x.Analyze(It.IsAny<string>(), It.IsAny<IsolationMode>()))
            .Returns(Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", false, [])));

        await _service.SyncSolutionAsync(_serviceMock.Object, "MyPlugin.dll", "MySolution", IsolationMode.Sandbox);

        _serviceMock.Verify(x => x.DeleteAsync("sdkmessageprocessingstep", stepId), Times.Once);
    }

    [Fact]
    public async Task SyncSolutionAsync_PluginWithSteps_DeletesOrphanedSteps()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();

        var orphanId = Guid.NewGuid();
        SetupSteps(new Entity("sdkmessageprocessingstep", orphanId) { ["name"] = "Orphaned step" });

        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessage")))
            .ReturnsAsync(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) }));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter")))
            .ReturnsAsync(new EntityCollection());

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of contact", "Update", "contact", 20, 0, 1, null, null, []);
        _analysisServiceMock.Setup(x => x.Analyze(It.IsAny<string>(), It.IsAny<IsolationMode>()))
            .Returns(Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", false, [step])));

        await _service.SyncSolutionAsync(_serviceMock.Object, "MyPlugin.dll", "MySolution", IsolationMode.Sandbox);

        _serviceMock.Verify(x => x.DeleteAsync("sdkmessageprocessingstep", orphanId), Times.Once);
    }

    // -- Save mode: report skipped deletions --

    [Fact]
    public async Task SyncSolutionAsync_SaveMode_ReportsSkippedStepAndTypeDeletions()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));

        SetupPluginTypes(new Entity("plugintype", Guid.NewGuid())
        {
            ["typename"] = "Obsolete.Plugin",
            ["isworkflowactivity"] = false
        });

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of contact", "Update", "contact", 20, 0, 1, null, null, []);
        var plugin = new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", false, [step]);

        var existingStepId = Guid.NewGuid();
        SetupSteps(new Entity("sdkmessageprocessingstep", existingStepId) { ["name"] = "Orphaned step" });

        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessage")))
            .ReturnsAsync(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) }));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter")))
            .ReturnsAsync(new EntityCollection());

        _analysisServiceMock.Setup(x => x.Analyze(It.IsAny<string>(), It.IsAny<IsolationMode>()))
            .Returns(Metadata(plugins: plugin));

        var skipped = new List<string>();
        await _service.SyncSolutionAsync(_serviceMock.Object, "MyPlugin.dll", "MySolution", IsolationMode.Sandbox, save: true, onSaveSkip: skipped.Add);

        _serviceMock.Verify(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
        Assert.Contains(skipped, s => s.Contains("Orphaned step"));
        Assert.Contains(skipped, s => s.Contains("Obsolete.Plugin"));
    }
}
