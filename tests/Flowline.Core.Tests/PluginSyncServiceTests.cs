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
}
