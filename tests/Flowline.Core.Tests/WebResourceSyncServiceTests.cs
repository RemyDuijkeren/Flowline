using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using NSubstitute;
using Flowline.Core.Services;
using Flowline.Core.Models;
using Flowline.Core;

namespace Flowline.Core.Tests;

public class WebResourceSyncServiceTests : IDisposable
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly IFlowlineOutput _outputMock;
    readonly WebResourceSyncService _service;
    readonly string _webresourceRoot;

    public WebResourceSyncServiceTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _outputMock = Substitute.For<IFlowlineOutput>();
        _service = new WebResourceSyncService(_outputMock);
        _webresourceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_webresourceRoot);

        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));
        _serviceMock.ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OrganizationResponse()));

        SetupSolution("MySolution", "my");
        SetupWebResources();
    }

    public void Dispose()
    {
        if (Directory.Exists(_webresourceRoot))
            Directory.Delete(_webresourceRoot, true);
    }

    [Fact]
    public async Task SyncSolutionAsync_NoChanges_ShouldNotCallExecute()
    {
        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_CreateNewWebResource_ShouldCreateAndPublishTargeted()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "test.js"), "console.log('test');");
        var createdId = Guid.NewGuid();
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = createdId;
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<CreateRequest>(r =>
            r.Target.GetAttributeValue<string>("name") == "my_MySolution/test.js" &&
            r["SolutionUniqueName"].ToString() == "MySolution"), Arg.Any<CancellationToken>());

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<OrganizationRequest>(r =>
            r.RequestName == "PublishXml" &&
            r["ParameterXml"].ToString()!.Contains(createdId.ToString())), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_DryRun_ShouldNotMutate()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "test.js"), "console.log('test');");

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", runMode: RunMode.DryRun);

        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _outputMock.Received().Skip(Arg.Is<string>(s => s.Contains("would create")));
    }

    [Fact]
    public async Task SyncSolutionAsync_SharedOrphan_ShouldRemoveFromSolutionInsteadOfDelete()
    {
        var webResourceId = Guid.NewGuid();
        SetupWebResources(RemoteWebResource(webResourceId, "my_MySolution/orphan.js", "old"));
        SetupOwnership(webResourceId,
            ("MySolution", false),
            ("SharedSolution", false));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<OrganizationRequest>(r =>
            r.RequestName == "RemoveSolutionComponent" &&
            (Guid)r["ComponentId"] == webResourceId &&
            (int)r["ComponentType"] == 61 &&
            r["SolutionUniqueName"].ToString() == "MySolution"), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("webresource", webResourceId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_UnmanagedOwnershipMissing_ShouldSkipAsUnclear()
    {
        var webResourceId = Guid.NewGuid();
        SetupWebResources(RemoteWebResource(webResourceId, "my_MySolution/unknown.js", "old"));
        SetupOwnership(webResourceId, ("ManagedBase", true));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Is<OrganizationRequest>(r => r.RequestName == "RemoveSolutionComponent"), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("webresource", webResourceId, Arg.Any<CancellationToken>());
        _outputMock.Received().Skip(Arg.Is<string>(s => s.Contains("ownership unclear")));
    }

    [Fact]
    public async Task SyncSolutionAsync_ManagedSolutionMetadata_ShouldStillReadSnapshot()
    {
        SetupSolution("MySolution", "my", isManaged: true);

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_CurrentSolutionOnlyOrphan_ShouldDelete()
    {
        var webResourceId = Guid.NewGuid();
        SetupWebResources(RemoteWebResource(webResourceId, "my_MySolution/orphan.js", "old"));
        SetupOwnership(webResourceId, ("MySolution", false));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.Received(1).DeleteAsync("webresource", webResourceId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_SaveMode_ShouldKeepOrphan()
    {
        var webResourceId = Guid.NewGuid();
        SetupWebResources(RemoteWebResource(webResourceId, "my_MySolution/orphan.js", "old"));
        SetupOwnership(webResourceId, ("MySolution", false));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false, runMode: RunMode.Save);

        await _serviceMock.DidNotReceive().DeleteAsync("webresource", webResourceId, Arg.Any<CancellationToken>());
        _outputMock.Received().Skip(Arg.Is<string>(s => s.Contains("--save")));
    }

    [Fact]
    public async Task SyncSolutionAsync_DeleteOnly_ShouldNotPublish()
    {
        var webResourceId = Guid.NewGuid();
        SetupWebResources(RemoteWebResource(webResourceId, "my_MySolution/orphan.js", "old"));
        SetupOwnership(webResourceId, ("MySolution", false));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_UnsupportedExtension_ShouldThrow()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "notes.txt"), "not a web resource");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false));
    }

    void SetupSolution(string solutionName, string prefix, bool isManaged = false)
    {
        var solution = new Entity("solution", Guid.NewGuid())
        {
            ["uniquename"] = solutionName,
            ["ismanaged"] = isManaged,
            ["publisher.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", prefix)
        };

        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "solution"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([solution])));
    }

    void SetupWebResources(params Entity[] webResources)
    {
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "webresource"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(webResources.ToList())));
    }

    static Entity RemoteWebResource(Guid id, string name, string content)
    {
        var entity = new Entity("webresource", id)
        {
            ["name"] = name,
            ["displayname"] = Path.GetFileName(name),
            ["content"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content)),
            ["webresourcetype"] = new OptionSetValue((int)WebResourceType.JS)
        };
        return entity;
    }

    void SetupOwnership(Guid webResourceId, params (string Name, bool IsManaged)[] solutions)
    {
        var rows = solutions.Select(s => new Entity("solutioncomponent")
        {
            ["solution.uniquename"] = new AliasedValue("solution", "uniquename", s.Name),
            ["solution.ismanaged"] = new AliasedValue("solution", "ismanaged", s.IsManaged)
        }).ToList();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "solutioncomponent" &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "objectid" && c.Values.Contains(webResourceId))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(rows)));
    }
}
