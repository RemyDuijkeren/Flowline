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

    [Fact]
    public async Task SyncSolutionAsync_CreateNewAssembly_ShouldCallExecuteWithCreateRequest()
    {
        // Arrange
        var solutionName = "MySolution";
        var dllPath = "MyPlugin.dll";
        var isolationMode = IsolationMode.Sandbox;
        var metadata = new PluginAssemblyMetadata(
            "MyPlugin", 
            "MyPlugin, Version=1.0.0.0", 
            new byte[] { 1, 2, 3 }, 
            "1.0.0.0", 
            isolationMode, 
            new List<PluginTypeMetadata>());

        _analysisServiceMock.Setup(x => x.Analyze(dllPath, isolationMode)).Returns(metadata);

        // Mock assembly retrieval (empty)
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "pluginassembly")))
            .ReturnsAsync(new EntityCollection());

        // Mock plugin types retrieval (empty)
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "plugintype")))
            .ReturnsAsync(new EntityCollection());

        // Mock CreateRequest response
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = Guid.NewGuid();
        _serviceMock.Setup(x => x.ExecuteAsync(It.Is<CreateRequest>(r => r.Target.LogicalName == "pluginassembly")))
            .ReturnsAsync(createResponse);

        // Act
        await _service.SyncSolutionAsync(_serviceMock.Object, dllPath, solutionName, isolationMode);

        // Assert
        _serviceMock.Verify(x => x.ExecuteAsync(It.Is<CreateRequest>(r => 
            r.Target.GetAttributeValue<string>("name") == metadata.Name &&
            r["SolutionUniqueName"].ToString() == solutionName
        )), Times.Once);
    }

    [Fact]
    public async Task SyncSolutionAsync_UpdateExistingAssembly_ShouldCallUpdateAsync()
    {
        // Arrange
        var solutionName = "MySolution";
        var dllPath = "MyPlugin.dll";
        var isolationMode = IsolationMode.Sandbox;
        var metadata = new PluginAssemblyMetadata(
            "MyPlugin", 
            "MyPlugin, Version=1.0.0.0", 
            new byte[] { 4, 5, 6 }, 
            "1.0.0.1", 
            isolationMode, 
            new List<PluginTypeMetadata>());

        _analysisServiceMock.Setup(x => x.Analyze(dllPath, isolationMode)).Returns(metadata);

        // Mock assembly retrieval (exists)
        var assemblyId = Guid.NewGuid();
        var existingAssembly = new Entity("pluginassembly", assemblyId)
        {
            ["name"] = "MyPlugin",
            ["version"] = "1.0.0.0"
        };
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "pluginassembly")))
            .ReturnsAsync(new EntityCollection(new List<Entity> { existingAssembly }));

        // Mock plugin types retrieval (empty)
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "plugintype")))
            .ReturnsAsync(new EntityCollection());

        // Act
        await _service.SyncSolutionAsync(_serviceMock.Object, dllPath, solutionName, isolationMode);

        // Assert
        _serviceMock.Verify(x => x.UpdateAsync(It.Is<Entity>(e => 
            e.LogicalName == "pluginassembly" && 
            e.Id == assemblyId && 
            e.GetAttributeValue<string>("version") == "1.0.0.1"
        )), Times.Once);
    }
}
