using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using NSubstitute;
using Flowline.Core.Services;
using Flowline.Core.Models;
using Flowline.Core;

namespace Flowline.Core.Tests;

public class WebResourceSyncServiceTests
{
    private readonly IOrganizationServiceAsync2 _serviceMock;
    private readonly WebResourceSyncService _service;

    public WebResourceSyncServiceTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
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
            _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "solution"))
                .Returns(Task.FromResult(solutionResult));

            // Mock web resource retrieval (empty CRM)
            _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "webresource"))
                .Returns(Task.FromResult(new EntityCollection()));

            // Act
            await _service.SyncSolutionAsync(_serviceMock, webresourceRoot, solutionName, publishAfterSync: false);

            // Assert
            await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>());
            await _serviceMock.DidNotReceive().CreateAsync(Arg.Any<Entity>());
            await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>());
            await _serviceMock.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<Guid>());
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
            _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "solution"))
                .Returns(Task.FromResult(solutionResult));

            // Mock web resource retrieval (empty CRM)
            _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "webresource"))
                .Returns(Task.FromResult(new EntityCollection()));

            // Act
            await _service.SyncSolutionAsync(_serviceMock, webresourceRoot, solutionName, publishAfterSync: false);

            // Assert
            await _serviceMock.Received(1).ExecuteAsync(Arg.Is<CreateRequest>(r =>
                r.Target.GetAttributeValue<string>("name") == "my_MySolution/test.js" &&
                r["SolutionUniqueName"].ToString() == solutionName
            ));
        }
        finally
        {
            Directory.Delete(webresourceRoot, true);
        }
    }
}
