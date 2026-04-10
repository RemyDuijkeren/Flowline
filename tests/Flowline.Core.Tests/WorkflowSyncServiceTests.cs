using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Moq;
using Flowline.Core.Services;
using Flowline.Core.Models;

namespace Flowline.Core.Tests;

public class WorkflowSyncServiceTests
{
    private readonly Mock<IOrganizationServiceAsync2> _serviceMock;
    private readonly Mock<IAssemblyAnalysisService> _analysisServiceMock;
    private readonly WorkflowSyncService _service;

    public WorkflowSyncServiceTests()
    {
        _serviceMock = new Mock<IOrganizationServiceAsync2>();
        _analysisServiceMock = new Mock<IAssemblyAnalysisService>();
        _service = new WorkflowSyncService(_analysisServiceMock.Object);
    }

    [Fact]
    public async Task SyncSolutionAsync_CreateNewWorkflowActivity_ShouldCallCreateAsync()
    {
        // Arrange
        var solutionName = "MySolution";
        var dllPath = "MyWorkflow.dll";
        var isolationMode = IsolationMode.Sandbox;
        var pluginMetadata = new PluginTypeMetadata("MyActivity", "MyNamespace.MyActivity", new List<PluginStepMetadata>());
        var metadata = new PluginAssemblyMetadata(
            "MyWorkflow", 
            "MyWorkflow, Version=1.0.0.0", 
            new byte[] { 1, 2, 3 }, 
            "1.0.0.0", 
            isolationMode, 
            new List<PluginTypeMetadata> { pluginMetadata });

        _analysisServiceMock.Setup(x => x.Analyze(dllPath, isolationMode)).Returns(metadata);

        // Mock assembly retrieval (exists)
        var assemblyId = Guid.NewGuid();
        var assembly = new Entity("pluginassembly", assemblyId);
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "pluginassembly")))
            .ReturnsAsync(new EntityCollection(new List<Entity> { assembly }));

        // Mock plugintype retrieval (empty)
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "plugintype")))
            .ReturnsAsync(new EntityCollection());

        // Act
        await _service.SyncSolutionAsync(_serviceMock.Object, dllPath, solutionName, isolationMode);

        // Assert
        _serviceMock.Verify(x => x.CreateAsync(It.Is<Entity>(e => 
            e.LogicalName == "plugintype" && 
            e.GetAttributeValue<string>("typename") == "MyNamespace.MyActivity"
        )), Times.Once);
    }

    [Fact]
    public async Task SyncSolutionAsync_DeleteObsoleteWorkflowActivity_ShouldCallDeleteAsync()
    {
        // Arrange
        var solutionName = "MySolution";
        var dllPath = "MyWorkflow.dll";
        var isolationMode = IsolationMode.Sandbox;
        var metadata = new PluginAssemblyMetadata(
            "MyWorkflow", 
            "MyWorkflow, Version=1.0.0.0", 
            new byte[] { 1, 2, 3 }, 
            "1.0.0.0", 
            isolationMode, 
            new List<PluginTypeMetadata>()); // No local activities

        _analysisServiceMock.Setup(x => x.Analyze(dllPath, isolationMode)).Returns(metadata);

        // Mock assembly retrieval (exists)
        var assemblyId = Guid.NewGuid();
        var assembly = new Entity("pluginassembly", assemblyId);
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "pluginassembly")))
            .ReturnsAsync(new EntityCollection(new List<Entity> { assembly }));

        // Mock plugintype retrieval (one exists in CRM)
        var obsoleteActivityId = Guid.NewGuid();
        var obsoleteActivity = new Entity("plugintype", obsoleteActivityId)
        {
            ["typename"] = "Obsolete.Activity"
        };
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "plugintype")))
            .ReturnsAsync(new EntityCollection(new List<Entity> { obsoleteActivity }));

        // Act
        await _service.SyncSolutionAsync(_serviceMock.Object, dllPath, solutionName, isolationMode);

        // Assert
        _serviceMock.Verify(x => x.DeleteAsync("plugintype", obsoleteActivityId), Times.Once);
    }
}
