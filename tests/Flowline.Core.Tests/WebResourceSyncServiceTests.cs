using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Moq;
using Flowline.Core.Services;
using Flowline.Core.Models;
using Flowline.Core;

namespace Flowline.Core.Tests;

public class WebResourceSyncServiceTests
{
    private readonly Mock<IOrganizationServiceAsync2> _serviceMock;
    private readonly WebResourceSyncService _service;

    public WebResourceSyncServiceTests()
    {
        _serviceMock = new Mock<IOrganizationServiceAsync2>();
        _service = new WebResourceSyncService(new NullFlowlineOutput());
    }

    [Fact]
    public async Task SyncSolutionAsync_NoChanges_ShouldNotCallExecute()
    {
        // Arrange
        var solutionName = "MySolution";
        var prefix = "my";
        var webresourceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(webresourceRoot);

        try
        {
            var solutionId = Guid.NewGuid();
            
            // Mock solution retrieval
            var solutionResult = new EntityCollection(new List<Entity>
            {
                new Entity("solution", solutionId)
                {
                    ["publisher.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", prefix)
                }
            });
            _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "solution")))
                .ReturnsAsync(solutionResult);

            // Mock web resource retrieval (empty CRM)
            _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "webresource")))
                .ReturnsAsync(new EntityCollection());

            // Act
            await _service.SyncSolutionAsync(_serviceMock.Object, webresourceRoot, solutionName, publishAfterSync: false);

            // Assert
            _serviceMock.Verify(x => x.ExecuteAsync(It.IsAny<OrganizationRequest>()), Times.Never);
            _serviceMock.Verify(x => x.CreateAsync(It.IsAny<Entity>()), Times.Never);
            _serviceMock.Verify(x => x.UpdateAsync(It.IsAny<Entity>()), Times.Never);
            _serviceMock.Verify(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>()), Times.Never);
        }
        finally
        {
            Directory.Delete(webresourceRoot, true);
        }
    }

    [Fact]
    public async Task SyncSolutionAsync_CreateNewWebResource_ShouldCallExecuteWithCreateRequest()
    {
        // Arrange
        var solutionName = "MySolution";
        var prefix = "my";
        var webresourceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(webresourceRoot);
        
        var filePath = Path.Combine(webresourceRoot, "test.js");
        File.WriteAllText(filePath, "console.log('test');");

        try
        {
            var solutionId = Guid.NewGuid();
            
            // Mock solution retrieval
            var solutionResult = new EntityCollection(new List<Entity>
            {
                new Entity("solution", solutionId)
                {
                    ["publisher.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", prefix)
                }
            });
            _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "solution")))
                .ReturnsAsync(solutionResult);

            // Mock web resource retrieval (empty CRM)
            _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "webresource")))
                .ReturnsAsync(new EntityCollection());

            // Act
            await _service.SyncSolutionAsync(_serviceMock.Object, webresourceRoot, solutionName, publishAfterSync: false);

            // Assert
            _serviceMock.Verify(x => x.ExecuteAsync(It.Is<CreateRequest>(r => 
                r.Target.GetAttributeValue<string>("name") == "my_MySolution/test.js" &&
                r["SolutionUniqueName"].ToString() == solutionName
            )), Times.Once);
        }
        finally
        {
            Directory.Delete(webresourceRoot, true);
        }
    }
}
