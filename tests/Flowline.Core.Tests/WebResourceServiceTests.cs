using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.PowerPlatform.Dataverse.Client;
using NSubstitute;
using Flowline.Core.Services;
using Flowline.Core.WebResources;
using Flowline.Core.Models;
using Flowline.Core;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests;

public class WebResourceServiceTests : IDisposable
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly TestConsole _console;
    readonly WebResourceService _service;
    readonly string _webresourceRoot;

    public WebResourceServiceTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _service = new WebResourceService(_console);
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
    public async Task SyncSolutionAsync_SnapshotTrees_RenderSideBySide()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "local.js"), "console.log('local');");
        SetupWebResources(RemoteWebResource(Guid.NewGuid(), "my_remote.js", "console.log('remote');"));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false, runMode: RunMode.DryRun);

        Assert.Contains(_console.Lines, l => l.Contains("Local (1)") && l.Contains("Dataverse (1)"));
        Assert.Contains(_console.Lines, l => l.Contains("my_MySolution") && l.Contains("my_remote.js"));
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

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<CreateRequest>(r =>
            r.Target.GetAttributeValue<string>("name") == "my_MySolution/test.js" &&
            r["SolutionUniqueName"].ToString() == "MySolution")), Arg.Any<CancellationToken>());

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<OrganizationRequest>(r =>
            r.RequestName == "PublishXml" &&
            r["ParameterXml"].ToString()!.Contains(createdId.ToString()))), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_NoPublish_ShouldSyncWithoutPublishing()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "test.js"), "console.log('test');");
        var createdId = Guid.NewGuid();
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = createdId;
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<CreateRequest>(r =>
            r.Target.GetAttributeValue<string>("name") == "my_MySolution/test.js")), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "PublishXml")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_DryRun_ShouldNotMutate()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "test.js"), "console.log('test');");

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", runMode: RunMode.DryRun);

        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        Assert.Contains("Creates (1)", _console.Output);
        Assert.Contains("1 create(s)", _console.Output);
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

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<OrganizationRequest>(r =>
            r.RequestName == "RemoveSolutionComponent" &&
            (Guid)r["ComponentId"] == webResourceId &&
            (int)r["ComponentType"] == 61 &&
            r["SolutionUniqueName"].ToString() == "MySolution")), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("webresource", webResourceId, Arg.Any<CancellationToken>());
        Assert.Contains("still in other solution", _console.Output);
    }

    [Fact]
    public async Task SyncSolutionAsync_UnmanagedOwnershipMissing_ShouldSkipAsUnclear()
    {
        var webResourceId = Guid.NewGuid();
        SetupWebResources(RemoteWebResource(webResourceId, "my_MySolution/unknown.js", "old"));
        SetupOwnership(webResourceId, ("ManagedBase", true));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "RemoveSolutionComponent")), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("webresource", webResourceId, Arg.Any<CancellationToken>());
        Assert.Contains("ownership unclear", _console.Output);
        // U4/R8: an ordinary skip (not the reference-only kind) stays neutral, not a warning.
        Assert.DoesNotContain("not pushed", _console.Output);
    }

    [Fact]
    public async Task SyncSolutionAsync_ManagedSolutionMetadata_ShouldStillReadSnapshot()
    {
        SetupSolution("MySolution", "my", isManaged: true);

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_PatchSolution_ShouldThrowBeforeMutating()
    {
        SetupSolution("MySolution", "my", parentSolutionId: Guid.NewGuid());
        File.WriteAllText(Path.Combine(_webresourceRoot, "test.js"), "console.log('test');");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false));

        Assert.Contains("patch solution", ex.Message);
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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
    public async Task SyncSolutionAsync_ManagedAndCurrentUnmanagedOrphan_ShouldRemoveFromSolutionInsteadOfDelete()
    {
        var webResourceId = Guid.NewGuid();
        SetupWebResources(RemoteWebResource(webResourceId, "my_MySolution/orphan.js", "old"));
        SetupOwnership(webResourceId,
            ("MySolution", false),
            ("msdyn_FieldService", true));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<OrganizationRequest>(r =>
            r.RequestName == "RemoveSolutionComponent" &&
            (Guid)r["ComponentId"] == webResourceId &&
            (int)r["ComponentType"] == 61 &&
            r["SolutionUniqueName"].ToString() == "MySolution")), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("webresource", webResourceId, Arg.Any<CancellationToken>());
        Assert.Contains("owned by managed solution", _console.Output);
    }

    [Fact]
    public async Task SyncSolutionAsync_NoDeleteMode_ShouldKeepOrphan()
    {
        var webResourceId = Guid.NewGuid();
        SetupWebResources(RemoteWebResource(webResourceId, "my_MySolution/orphan.js", "old"));
        SetupOwnership(webResourceId, ("MySolution", false));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false, runMode: RunMode.NoDelete);

        await _serviceMock.DidNotReceive().DeleteAsync("webresource", webResourceId, Arg.Any<CancellationToken>());
        Assert.Contains("--no-delete", _console.Output);
    }

    [Fact]
    public async Task SyncSolutionAsync_DeleteOnly_ShouldNotPublish()
    {
        var webResourceId = Guid.NewGuid();
        SetupWebResources(RemoteWebResource(webResourceId, "my_MySolution/orphan.js", "old"));
        SetupOwnership(webResourceId, ("MySolution", false));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "PublishXml")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_UnsupportedExtension_ShouldThrow()
    {
        // "not a web resource" has no magic bytes, RESX/SVG/XML/HTML markers, or CSS/JS signals —
        // Tier 2 content sniffing doesn't resolve it either, so it still fails validation.
        File.WriteAllText(Path.Combine(_webresourceRoot, "notes.txt"), "not a web resource");

        await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false));
    }

    [Fact]
    public async Task SyncSolutionAsync_XapFile_ShouldAbortBeforeMutating()
    {
        File.WriteAllBytes(Path.Combine(_webresourceRoot, "legacy.xap"), []);

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false));

        Assert.Contains("cannot be synced", ex.Message);
        Assert.Contains("legacy.xap", _console.Output);
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_InvalidWebResourceName_ShouldAbortBeforeMutating()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "my file.js"), "console.log('test');");

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false));

        Assert.Contains("cannot be synced", ex.Message);
        Assert.Contains("my file.js", _console.Output);
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_MultipleInvalidNames_ShouldListAllInError()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "my file.js"), "console.log('test');");
        File.WriteAllText(Path.Combine(_webresourceRoot, "other file.css"), "body {}");

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false));

        Assert.Contains("2 web resource", ex.Message);
        Assert.Contains("my file.js", _console.Output);
        Assert.Contains("other file.css", _console.Output);
    }

    [Fact]
    public async Task SyncSolutionAsync_CreateFails_ShouldContinueOtherCreatesAndPublishSucceeded()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "a.js"), "let a = 1;");
        File.WriteAllText(Path.Combine(_webresourceRoot, "b.js"), "let b = 2;");

        var succeededId = Guid.NewGuid();
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = succeededId;

        _serviceMock.ExecuteAsync(
                Arg.Is(Matching<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "my_MySolution/b.js")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));
        _serviceMock.ExecuteAsync(
                Arg.Is(Matching<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "my_MySolution/a.js")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<OrganizationResponse>(new FaultException<OrganizationServiceFault>(new OrganizationServiceFault(), "Dataverse error")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution"));

        Assert.Contains("1 web resource", ex.Message);
        Assert.Contains("my_MySolution/a.js", _console.Output);
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<OrganizationRequest>(r =>
            r.RequestName == "PublishXml" &&
            r["ParameterXml"].ToString()!.Contains(succeededId.ToString()))), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_UpdateFails_ShouldPublishSucceededAndThrow()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        SetupWebResources(
            RemoteWebResource(id1, "my_MySolution/a.js", "old"),
            RemoteWebResource(id2, "my_MySolution/b.js", "old"));

        File.WriteAllText(Path.Combine(_webresourceRoot, "a.js"), "new content");
        File.WriteAllText(Path.Combine(_webresourceRoot, "b.js"), "new content");

        _serviceMock.UpdateAsync(Arg.Is(Matching<Entity>(e => e.Id == id1)), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new FaultException<OrganizationServiceFault>(new OrganizationServiceFault(), "Dataverse update error")));
        _serviceMock.UpdateAsync(Arg.Is(Matching<Entity>(e => e.Id == id2)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution"));

        Assert.Contains("1 web resource", ex.Message);
        Assert.Contains("my_MySolution/a.js", _console.Output);
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<OrganizationRequest>(r =>
            r.RequestName == "PublishXml" &&
            r["ParameterXml"].ToString()!.Contains(id2.ToString()))), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_ExistsInOtherSolutionWithDifferentContent_ShouldUpdateAndAddToSolution()
    {
        var webResourceId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_webresourceRoot, "shared.js"), "new content");
        SetupGlobalOrphans(RemoteWebResource(webResourceId, "my_MySolution/shared.js", "old content"));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).UpdateAsync(
            Arg.Is(Matching<Entity>(e => e.Id == webResourceId)), Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r =>
                r.RequestName == "AddSolutionComponent" &&
                (Guid)r["ComponentId"] == webResourceId &&
                r["SolutionUniqueName"].ToString() == "MySolution")),
            Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r =>
                r.RequestName == "PublishXml" &&
                r["ParameterXml"].ToString()!.Contains(webResourceId.ToString()))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_ExistsInOtherSolutionWithSameContent_ShouldAddToSolutionWithoutUpdate()
    {
        var webResourceId = Guid.NewGuid();
        var contentBytes = System.Text.Encoding.UTF8.GetBytes("same content");
        File.WriteAllBytes(Path.Combine(_webresourceRoot, "shared.js"), contentBytes);
        SetupGlobalOrphans(RemoteWebResource(webResourceId, "my_MySolution/shared.js", "same content"));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r =>
                r.RequestName == "AddSolutionComponent" &&
                (Guid)r["ComponentId"] == webResourceId &&
                r["SolutionUniqueName"].ToString() == "MySolution")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_GlobalOrphanOwnedByOtherUnmanagedSolution_ShouldThrowBeforeMutating()
    {
        var webResourceId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_webresourceRoot, "shared.js"), "new content");
        SetupGlobalOrphans(RemoteWebResource(webResourceId, "my_MySolution/shared.js", "old content"));
        SetupOwnership(webResourceId, ("OtherSolution", false));

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false));

        Assert.Equal(ExitCode.ValidationFailed, ex.ExitCode);
        var combined = ex.Message + _console.Output;
        Assert.Contains("shared.js", combined);
        Assert.Contains("my_MySolution/shared.js", combined);
        Assert.Contains("OtherSolution", combined);
        Assert.Contains("co-management", combined, StringComparison.OrdinalIgnoreCase);

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "AddSolutionComponent")), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "PublishXml")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_GlobalOrphanOwnedSolelyByManagedSolution_ShouldThrowNamingManagedOwnerNotCoManagement()
    {
        var webResourceId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_webresourceRoot, "shared.js"), "new content");
        SetupGlobalOrphans(RemoteWebResource(webResourceId, "my_MySolution/shared.js", "old content"));
        // Pinned: zero unmanaged owners, managed reference only — WebResourceOwnership(0, false, true).
        // A count-only predicate (NonDefaultUnmanagedSolutionCount > 0) would miss this and silently adopt.
        SetupOwnership(webResourceId, ("ManagedVendor", true));

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false));

        Assert.Equal(ExitCode.ValidationFailed, ex.ExitCode);
        var combined = ex.Message + _console.Output;
        Assert.Contains("ManagedVendor", combined);
        Assert.DoesNotContain("co-management", combined, StringComparison.OrdinalIgnoreCase);

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "AddSolutionComponent")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_TwoGlobalOrphansForeignOwned_ShouldThrowNamingBoth()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_webresourceRoot, "shared1.js"), "content 1");
        File.WriteAllText(Path.Combine(_webresourceRoot, "shared2.js"), "content 2");
        SetupGlobalOrphans(
            RemoteWebResource(id1, "my_MySolution/shared1.js", "old 1"),
            RemoteWebResource(id2, "my_MySolution/shared2.js", "old 2"));
        SetupOwnership(id1, ("OtherSolution", false));
        SetupOwnership(id2, ("OtherSolution", false));

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false));

        var combined = ex.Message + _console.Output;
        Assert.Contains("shared1.js", combined);
        Assert.Contains("shared2.js", combined);
    }

    [Fact]
    public async Task SyncSolutionAsync_GlobalOrphanForeignOwnedIdenticalContent_ShouldStillThrow()
    {
        var webResourceId = Guid.NewGuid();
        var content = "same content";
        File.WriteAllText(Path.Combine(_webresourceRoot, "shared.js"), content);
        SetupGlobalOrphans(RemoteWebResource(webResourceId, "my_MySolution/shared.js", content));
        SetupOwnership(webResourceId, ("OtherSolution", false));

        await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false));

        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "AddSolutionComponent")), Arg.Any<CancellationToken>());
    }

    // --- U3: // flowline:depends as a reference-only declaration (R7) ---

    [Fact]
    public async Task SyncSolutionAsync_ForeignOwnedOrphanReferencedByDepends_ShouldSkipNotThrowAndKeepDependencyXml()
    {
        var webResourceId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_webresourceRoot, "shared.js"), "new content");
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:depends my_MySolution/shared.js\ncode();");
        SetupGlobalOrphans(RemoteWebResource(webResourceId, "my_MySolution/shared.js", "old content"));
        SetupOwnership(webResourceId, ("OtherSolution", false));
        var createdId = Guid.NewGuid();
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = createdId;
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        // shared.js itself: no create/update/adopt
        await _serviceMock.DidNotReceive().UpdateAsync(
            Arg.Is(Matching<Entity>(e => e.Id == webResourceId)), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r =>
                r.RequestName == "AddSolutionComponent" && (Guid)r["ComponentId"] == webResourceId)),
            Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "my_MySolution/shared.js")),
            Arg.Any<CancellationToken>());
        // U4/R8: reference-only skip renders as a warning, not a neutral "kept" skip line.
        Assert.Contains("my_MySolution/shared.js", _console.Output);
        Assert.Contains("OtherSolution", _console.Output);
        Assert.Contains("not pushed", _console.Output);

        // form.js still pushed, still carries the dependency
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r =>
                r.Target.GetAttributeValue<string>("name") == "my_MySolution/form.js" &&
                r.Target.GetAttributeValue<string>("dependencyxml")!.Contains("my_MySolution/shared.js"))),
            Arg.Any<CancellationToken>());
    }

    // --- U4: reference-only skip is a warning, not a neutral skip (R8) ---

    [Fact]
    public async Task SyncSolutionAsync_ForeignOwnedOrphanReferencedByDepends_ShouldAdviseRemovingFromResolvedFolder()
    {
        // Short, distinctive folder name — TestConsole word-wraps output, and a long GUID temp
        // path can split across lines, so a full-path Contains() check would be unreliable.
        var resolvedRoot = Path.Combine(Path.GetTempPath(), "ResolvedFolder_" + Guid.NewGuid());
        Directory.CreateDirectory(resolvedRoot);
        try
        {
            var webResourceId = Guid.NewGuid();
            File.WriteAllText(Path.Combine(resolvedRoot, "shared.js"), "new content");
            File.WriteAllText(Path.Combine(resolvedRoot, "form.js"),
                "// flowline:depends my_MySolution/shared.js\ncode();");
            SetupGlobalOrphans(RemoteWebResource(webResourceId, "my_MySolution/shared.js", "old content"));
            SetupOwnership(webResourceId, ("OtherSolution", false));
            _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ => { var r = new CreateResponse(); r.Results["id"] = Guid.NewGuid(); return Task.FromResult<OrganizationResponse>(r); });

            var result = await _service.SyncSolutionAsync(_serviceMock, resolvedRoot, "MySolution", publishAfterSync: false);

            // Push still succeeds — the skip is a warning, not a failure.
            Assert.True(result);
            Assert.Contains("Remove it", _console.Output);
            Assert.Contains("ResolvedFolder_", _console.Output);
        }
        finally
        {
            Directory.Delete(resolvedRoot, true);
        }
    }

    [Fact]
    public async Task SyncSolutionAsync_ForeignOwnedOrphanReferencedByDepends_ShouldNameCustomFolderNotDist()
    {
        var customRoot = Path.Combine(Path.GetTempPath(), "CustomWebResources_" + Guid.NewGuid());
        Directory.CreateDirectory(customRoot);
        try
        {
            var webResourceId = Guid.NewGuid();
            File.WriteAllText(Path.Combine(customRoot, "shared.js"), "new content");
            File.WriteAllText(Path.Combine(customRoot, "form.js"),
                "// flowline:depends my_MySolution/shared.js\ncode();");
            SetupGlobalOrphans(RemoteWebResource(webResourceId, "my_MySolution/shared.js", "old content"));
            SetupOwnership(webResourceId, ("OtherSolution", false));
            _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
                .Returns(_ => { var r = new CreateResponse(); r.Results["id"] = Guid.NewGuid(); return Task.FromResult<OrganizationResponse>(r); });

            await _service.SyncSolutionAsync(_serviceMock, customRoot, "MySolution", publishAfterSync: false);

            Assert.Contains("CustomWebResources_", _console.Output);
            Assert.DoesNotContain("dist", _console.Output, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(customRoot, true);
        }
    }

    [Fact]
    public async Task SyncSolutionAsync_NoGlobalOrphans_ShouldNotPrintReferencedNotOwnedWarning()
    {
        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        Assert.DoesNotContain("not pushed", _console.Output);
    }

    [Fact]
    public async Task SyncSolutionAsync_ForeignOwnedOrphanReferencedByBareDependsName_ShouldSkip()
    {
        var webResourceId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_webresourceRoot, "shared.js"), "new content");
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:depends shared.js\ncode();");
        SetupGlobalOrphans(RemoteWebResource(webResourceId, "my_MySolution/shared.js", "old content"));
        SetupOwnership(webResourceId, ("OtherSolution", false));
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = Guid.NewGuid();
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.DidNotReceive().UpdateAsync(
            Arg.Is(Matching<Entity>(e => e.Id == webResourceId)), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r =>
                r.RequestName == "AddSolutionComponent" && (Guid)r["ComponentId"] == webResourceId)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_ForeignOwnedOrphanReferencedByAmbiguousDependsName_ShouldThrowFullyQualifyMessage()
    {
        var webResourceId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_webresourceRoot, "dupe.js"), "new content");
        var subDir = Directory.CreateDirectory(Path.Combine(_webresourceRoot, "sub"));
        File.WriteAllText(Path.Combine(subDir.FullName, "dupe.js"), "// other file");
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:depends dupe.js\ncode();");
        SetupGlobalOrphans(RemoteWebResource(webResourceId, "my_MySolution/dupe.js", "old content"));
        SetupOwnership(webResourceId, ("OtherSolution", false));

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false));

        Assert.Equal(ExitCode.ValidationFailed, ex.ExitCode);
        // TestConsole word-wraps output, so check the two words separately rather than as one phrase.
        var combined = ex.Message + _console.Output;
        Assert.Contains("dupe.js", combined);
        Assert.Contains("fully", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("qualify", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ambiguous", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("co-management", combined, StringComparison.OrdinalIgnoreCase);

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "AddSolutionComponent")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_GlobalOrphanUnownedButReferencedByDepends_ShouldAdoptNormally()
    {
        // KTD4 table row 3: referenced + owned by nobody → push normally, depends is load-order only.
        var webResourceId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_webresourceRoot, "shared.js"), "new content");
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:depends my_MySolution/shared.js\ncode();");
        SetupGlobalOrphans(RemoteWebResource(webResourceId, "my_MySolution/shared.js", "old content"));
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = Guid.NewGuid();
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.Received(1).UpdateAsync(
            Arg.Is(Matching<Entity>(e => e.Id == webResourceId)), Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r =>
                r.RequestName == "AddSolutionComponent" && (Guid)r["ComponentId"] == webResourceId)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_ReferenceOnlySkipIsOnlyPlanContent_ShouldStillWarn()
    {
        // Regression: TotalChanges excludes Skips, so a plan whose ONLY content is a reference-only
        // skip took the TotalChanges == 0 early return. form.js already matches Dataverse exactly
        // (content, displayname, deps) so it produces no create/update — the referenced global orphan
        // is the plan's only entry, forcing the exact steady-state path that dropped the warning.
        var formId = Guid.NewGuid();
        var sharedId = Guid.NewGuid();
        const string formContent = "// flowline:depends my_MySolution/shared.js\ncode();";
        const string depXml = """<Dependencies><Dependency componentType="WebResource"><Library name="my_MySolution/shared.js" displayName="shared.js" languagecode="" description="" libraryUniqueId="{0e58647c-5eb8-e4cc-b94d-19e6acb09469}"/></Dependency></Dependencies>""";
        SetupWebResources(RemoteWebResourceWithDepXml(formId, "my_MySolution/form.js", formContent, depXml));
        SetupOwnership(formId, ("MySolution", false));
        SetupGlobalOrphans(RemoteWebResource(sharedId, "my_MySolution/shared.js", "old content"));
        SetupOwnership(sharedId, ("OtherSolution", false));
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"), formContent);
        File.WriteAllText(Path.Combine(_webresourceRoot, "shared.js"), "new content");

        var result = await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        Assert.False(result); // TotalChanges == 0 early-return path
        Assert.Contains("shared.js", _console.Output);
        Assert.Contains("OtherSolution", _console.Output);
        Assert.Contains("not pushed", _console.Output);
        Assert.Contains("Remove it", _console.Output);

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_AutoMatchedResxDependency_ShouldNotExemptForeignOwnership()
    {
        // Regression: the ownership exemption used to gate on DependsOn, which AutoMatchResxDependencies
        // enriches by folder-qualified base-name match with no annotation behind it. A JS+RESX pair
        // sharing a base name ("Form.js" / "Form.1033.resx") must NOT silently downgrade the foreign-owned
        // RESX's ownership block to a warning — nothing in source declares the dependency.
        var resxId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_webresourceRoot, "Form.js"), "code();"); // no // flowline:depends
        File.WriteAllText(Path.Combine(_webresourceRoot, "Form.1033.resx"), "");
        SetupGlobalOrphans(RemoteWebResource(resxId, "my_MySolution/Form.1033.resx", "old content"));
        SetupOwnership(resxId, ("OtherSolution", false));

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false));

        Assert.Equal(ExitCode.ValidationFailed, ex.ExitCode);
        var combined = ex.Message + _console.Output;
        Assert.Contains("Form.1033.resx", combined);
        Assert.Contains("OtherSolution", combined);

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_SolutionNotFound_ShouldThrow()
    {
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "solution")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution"));
    }

    // --- Dependency planner (U4) ---

    [Fact]
    public async Task SyncSolutionAsync_ContentChangedDepsUnchanged_UpdateWithoutDependencyXml()
    {
        var id = Guid.NewGuid();
        var depXml = """<Dependencies><Dependency componentType="WebResource"><Library name="av_Sol/lib.js" displayName="lib.js" languagecode="" description="" libraryUniqueId="{0e58647c-5eb8-e4cc-b94d-19e6acb09469}"/></Dependency></Dependencies>""";
        // Remote has the annotation + old body; local has annotation + new body — content differs, deps same
        SetupWebResources(RemoteWebResourceWithDepXml(id, "my_MySolution/form.js",
            "// flowline:depends av_Sol/lib.js\nold content", depXml));
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:depends av_Sol/lib.js\nnew content");
        SetupOwnership(id, ("MySolution", false));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.Received(1).UpdateAsync(
            Arg.Is(Matching<Entity>(e => e.Id == id && !e.Attributes.ContainsKey("dependencyxml"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_DepsChangedContentUnchanged_UpdateWithDependencyXml()
    {
        var id = Guid.NewGuid();
        // Remote: same content, no deps. Local: same content + annotation → new dep.
        var fileContent = "// flowline:depends av_Sol/lib.js\ncode();";
        SetupWebResources(RemoteWebResourceWithDepXml(id, "my_MySolution/form.js", fileContent, null));
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"), fileContent);
        SetupOwnership(id, ("MySolution", false));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.Received(1).UpdateAsync(
            Arg.Is(Matching<Entity>(e => e.Id == id && e.Attributes.ContainsKey("dependencyxml"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_ContentAndDepsChanged_PlanReasonListsBoth()
    {
        var id = Guid.NewGuid();
        // Remote: old content, no deps. Local: new content + annotation → both content and deps changed.
        SetupWebResources(RemoteWebResourceWithDepXml(id, "my_MySolution/form.js", "old content", null));
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:depends av_Sol/lib.js\nnew content");
        SetupOwnership(id, ("MySolution", false));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false, runMode: RunMode.DryRun);

        Assert.Contains("my_MySolution/form.js (content, dependencies)", _console.Output);
    }

    [Fact]
    public async Task SyncSolutionAsync_DepsAndContentUnchanged_NoUpdate()
    {
        var id = Guid.NewGuid();
        var depXml = """<Dependencies><Dependency componentType="WebResource"><Library name="av_Sol/lib.js" displayName="lib.js" languagecode="" description="" libraryUniqueId="{0e58647c-5eb8-e4cc-b94d-19e6acb09469}"/></Dependency></Dependencies>""";
        var fileContent = "// flowline:depends av_Sol/lib.js\ncode();";
        // Remote: same content + same dep. No change.
        SetupWebResources(RemoteWebResourceWithDepXml(id, "my_MySolution/form.js", fileContent, depXml));
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"), fileContent);
        SetupOwnership(id, ("MySolution", false));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_DepsRemovedRemoteHadDeps_UpdateWithNullDependencyXml()
    {
        var id = Guid.NewGuid();
        var depXml = """<Dependencies><Dependency componentType="WebResource"><Library name="av_Sol/lib.js" displayName="lib.js" languagecode="" description="" libraryUniqueId="{0e58647c-5eb8-e4cc-b94d-19e6acb09469}"/></Dependency></Dependencies>""";
        SetupWebResources(RemoteWebResourceWithDepXml(id, "my_MySolution/form.js", "code();", depXml));
        // Local has no annotations → no deps
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"), "code();");
        SetupOwnership(id, ("MySolution", false));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.Received(1).UpdateAsync(
            Arg.Is(Matching<Entity>(e => e.Id == id && e.Attributes.ContainsKey("dependencyxml") && e["dependencyxml"] == null)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_NewResourceWithDeps_CreateHasDependencyXml()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:depends av_Sol/lib.js\ncode();");
        var createdId = Guid.NewGuid();
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = createdId;
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r => r.Target.Attributes.ContainsKey("dependencyxml"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_BareSiblingNameAnnotation_QualifiesLibraryName()
    {
        // Annotation names a local sibling by its bare filename, matching how the Maker Portal's own
        // dependency editor writes Library@name (fully qualified) — Flowline must match that, not
        // echo the bare annotation text back, or the dependency shows a name unresolvable by the UI.
        File.WriteAllText(Path.Combine(_webresourceRoot, "lib.js"), "code();");
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:depends lib.js\ncode();");
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = Guid.NewGuid();
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r =>
                r.Target.GetAttributeValue<string>("name") == "my_MySolution/form.js" &&
                r.Target.GetAttributeValue<string>("dependencyxml")!.Contains("name=\"my_MySolution/lib.js\"") &&
                !r.Target.GetAttributeValue<string>("dependencyxml")!.Contains("name=\"lib.js\""))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_NewResourceNoDeps_CreateWithoutDependencyXml()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"), "code();");
        var createdId = Guid.NewGuid();
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = createdId;
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r => !r.Target.Attributes.ContainsKey("dependencyxml"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_NoDepsNullRemoteDeps_NoSpuriousUpdate()
    {
        var id = Guid.NewGuid();
        SetupWebResources(RemoteWebResourceWithDepXml(id, "my_MySolution/form.js", "code();", null));
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"), "code();");
        SetupOwnership(id, ("MySolution", false));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    // --- Dependency annotations ---

    [Fact]
    public async Task SyncSolutionAsync_ResxNoMatchingJs_ShouldEmitWarning()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "Labels.1033.resx"), "");
        // No JS file with base name "Labels"

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false, runMode: RunMode.DryRun);

        Assert.Contains("Labels", _console.Output);
        Assert.Contains("no JS file", _console.Output);
    }

    [Fact]
    public async Task SyncSolutionAsync_ResxCrossFolderJs_ShouldWarnNoMatch()
    {
        // RESX at root, JS only in subfolder — folder-qualified base names differ, so no auto-match.
        File.WriteAllText(Path.Combine(_webresourceRoot, "Labels.1033.resx"), "");
        Directory.CreateDirectory(Path.Combine(_webresourceRoot, "sub"));
        File.WriteAllText(Path.Combine(_webresourceRoot, "sub", "Labels.js"), "// no deps");

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false, runMode: RunMode.DryRun);

        Assert.Contains("no JS file", _console.Output);
    }

    [Fact]
    public async Task SyncSolutionAsync_ResxSameFolderJs_AutoMatchesSameFolderOnly()
    {
        // RESX at root auto-matches root JS, not subfolder JS with the same base name.
        File.WriteAllText(Path.Combine(_webresourceRoot, "Labels.1033.resx"), "");
        File.WriteAllText(Path.Combine(_webresourceRoot, "Labels.js"), "code();");
        Directory.CreateDirectory(Path.Combine(_webresourceRoot, "sub"));
        File.WriteAllText(Path.Combine(_webresourceRoot, "sub", "Labels.js"), "code();");
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = Guid.NewGuid();
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r =>
                r.Target.GetAttributeValue<string>("name") == "my_MySolution/Labels.js" &&
                r.Target.Attributes.ContainsKey("dependencyxml") &&
                r.Target.GetAttributeValue<string>("dependencyxml")!.Contains("my_MySolution/Labels.1033.resx"))),
            Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r =>
                r.Target.GetAttributeValue<string>("name") == "my_MySolution/sub/Labels.js" &&
                !r.Target.Attributes.ContainsKey("dependencyxml"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_ResxAutoMatchesSingleJs_RegisteredAsDependency()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "Form.js"), "code();");
        File.WriteAllText(Path.Combine(_webresourceRoot, "Form.1033.resx"), "");
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = Guid.NewGuid();
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r =>
                r.Target.GetAttributeValue<string>("name") == "my_MySolution/Form.js" &&
                r.Target.Attributes.ContainsKey("dependencyxml") &&
                r.Target.GetAttributeValue<string>("dependencyxml")!.Contains("my_MySolution/Form.1033.resx"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_BareResxAnnotationExpandedToLcidVariant_RegisteredAsDependency()
    {
        // Local RESX variant exists; bare ".resx" annotation should expand to the LCID variant.
        File.WriteAllText(Path.Combine(_webresourceRoot, "Form.js"),
            "// flowline:depends my_MySolution/strings.resx\ncode();");
        File.WriteAllText(Path.Combine(_webresourceRoot, "strings.1033.resx"), "");
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = Guid.NewGuid();
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r =>
                r.Target.GetAttributeValue<string>("name") == "my_MySolution/Form.js" &&
                r.Target.Attributes.ContainsKey("dependencyxml") &&
                r.Target.GetAttributeValue<string>("dependencyxml")!.Contains("my_MySolution/strings.1033.resx"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_GlobalOrphanWithChangedDeps_UpdatesDepXmlAndAddsToSolution()
    {
        var orphanId = Guid.NewGuid();
        var existingDepXml = """<Dependencies><Dependency componentType="WebResource"><Library name="old/lib.js" displayName="lib.js" languagecode="" description="" libraryUniqueId="{0e58647c-5eb8-e4cc-b94d-19e6acb09469}"/></Dependency></Dependencies>""";
        SetupGlobalOrphans(RemoteWebResourceWithDepXml(orphanId, "my_MySolution/shared.js", "same content", existingDepXml));
        File.WriteAllText(Path.Combine(_webresourceRoot, "shared.js"),
            "// flowline:depends av_Sol/new-lib.js\nsame content");
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = orphanId;
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).UpdateAsync(
            Arg.Is(Matching<Entity>(e =>
                e.Id == orphanId &&
                e.Attributes.ContainsKey("dependencyxml") &&
                e.GetAttributeValue<string>("dependencyxml")!.Contains("av_Sol/new-lib.js"))),
            Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r =>
                r.RequestName == "AddSolutionComponent" &&
                (Guid)r["ComponentId"] == orphanId &&
                r["SolutionUniqueName"].ToString() == "MySolution")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_RemoteResourceHasDependencyXml_LocalNoAnnotation_ClearsDeps()
    {
        var depXml = """<Dependencies><Dependency componentType="WebResource"><Library name="av_Sol/lib.js" displayName="lib.js" languagecode="" description="" libraryUniqueId="{0e58647c-5eb8-e4cc-b94d-19e6acb09469}"/></Dependency></Dependencies>""";
        var id = Guid.NewGuid();
        SetupWebResources(RemoteWebResourceWithDepXml(id, "my_MySolution/form.js", "code();", depXml));
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"), "code();");
        SetupOwnership(id, ("MySolution", false));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        // Remote had dep, local has no annotation → planner clears dependencyxml
        await _serviceMock.Received(1).UpdateAsync(
            Arg.Is(Matching<Entity>(e => e.Id == id && e.Attributes.ContainsKey("dependencyxml") && e["dependencyxml"] == null)),
            Arg.Any<CancellationToken>());
    }

    // --- Verbatim mode ---

    [Fact]
    public async Task SyncSolutionAsync_VerbatimMode_RootLevelFileWithPublisherLikeName_ShouldUseVerbatimName()
    {
        // A root-level file whose name starts with a publisher-like prefix (any publisher, not just
        // this project's) goes verbatim even without a containing subfolder — same rule as the
        // folder case, so a flat pre-existing Dataverse name round-trips unchanged.
        File.WriteAllText(Path.Combine(_webresourceRoot, "av_helper.js"), "// helper");
        var createdId = Guid.NewGuid();
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = createdId;
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "av_helper.js")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_VerbatimMode_OwnPublisherFolder_ShouldUseVerbatimName()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_webresourceRoot, "my_MySolution", "js"));
        File.WriteAllText(Path.Combine(dir.FullName, "app.js"), "// app");
        var createdId = Guid.NewGuid();
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = createdId;
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "my_MySolution/js/app.js")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_VerbatimMode_RootLevelPrefixedFilename_ShouldUseVerbatimName()
    {
        // Legacy flat Dataverse name, no subfolder — e.g. cloned from a pre-Flowline solution.
        File.WriteAllText(Path.Combine(_webresourceRoot, "my_legacyscript.js"), "// legacy");
        var createdId = Guid.NewGuid();
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = createdId;
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "my_legacyscript.js")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_VerbatimMode_DifferentPublisherFolder_ShouldUseVerbatimName()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_webresourceRoot, "new_Other", "util"));
        File.WriteAllText(Path.Combine(dir.FullName, "helper.js"), "// helper");
        var createdId = Guid.NewGuid();
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = createdId;
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "new_Other/util/helper.js")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_VerbatimMode_SharedNamespaceFolder_ShouldUseVerbatimName()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_webresourceRoot, "dh_", "lib"));
        File.WriteAllText(Path.Combine(dir.FullName, "jquery.js"), "// jquery");
        var createdId = Guid.NewGuid();
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = createdId;
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "dh_/lib/jquery.js")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_VerbatimMode_MixedRoot_ShouldResolveDistinctNames()
    {
        // auto-prefix file
        File.WriteAllText(Path.Combine(_webresourceRoot, "app.js"), "// app");
        // verbatim file under a different path that won't collide with the auto-prefix result
        var dir = Directory.CreateDirectory(Path.Combine(_webresourceRoot, "av_Shared", "lib"));
        File.WriteAllText(Path.Combine(dir.FullName, "util.js"), "// util");

        var createdId = Guid.NewGuid();
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = createdId;
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "my_MySolution/app.js")),
            Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "av_Shared/lib/util.js")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_CollisionDetection_VerbatimAndAutoPrefixSameName_ShouldThrow()
    {
        // auto-prefix: js/app.js → my_MySolution/js/app.js
        Directory.CreateDirectory(Path.Combine(_webresourceRoot, "js"));
        File.WriteAllText(Path.Combine(_webresourceRoot, "js", "app.js"), "// app");
        // verbatim: my_MySolution/js/app.js → my_MySolution/js/app.js  (collision!)
        var dir = Directory.CreateDirectory(Path.Combine(_webresourceRoot, "my_MySolution", "js"));
        File.WriteAllText(Path.Combine(dir.FullName, "app.js"), "// app verbatim");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution"));
    }

    // --- U3: dependency check runs once per Deletes/RemovesFromSolution entry, after planning (R2/R3) ---

    [Fact]
    public async Task SyncSolutionAsync_DeletesAndRemoves_IssueOneDependencyRequestPerEntry()
    {
        var deleteId = Guid.NewGuid();
        var removeId = Guid.NewGuid();
        SetupWebResources(
            RemoteWebResource(deleteId, "my_MySolution/delete.js", "old"),
            RemoteWebResource(removeId, "my_MySolution/remove.js", "old"));
        SetupOwnership(deleteId, ("MySolution", false));
        SetupOwnership(removeId, ("MySolution", false), ("SharedSolution", false));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r =>
                r.RequestName == "RetrieveDependenciesForDelete" && (Guid)r["ObjectId"] == deleteId)),
            Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r =>
                r.RequestName == "RetrieveDependenciesForDelete" && (Guid)r["ObjectId"] == removeId)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_SkipsOnly_IssuesNoDependencyRequests()
    {
        var webResourceId = Guid.NewGuid();
        SetupWebResources(RemoteWebResource(webResourceId, "my_MySolution/unknown.js", "old"));
        SetupOwnership(webResourceId, ("ManagedBase", true)); // ownership unclear -> Skips only

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "RetrieveDependenciesForDelete")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_NoDeletesNoRemoves_IssuesNoDependencyRequests()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "test.js"), "console.log('test');");
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = Guid.NewGuid();
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r => r.RequestName == "RetrieveDependenciesForDelete")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_DryRunAndRealRun_IssueSameDependencyChecks()
    {
        // Same snapshot, dry-run first then a real run — the dependency check must run identically
        // before the branch that distinguishes them, so both read the same result (KD5).
        var webResourceId = Guid.NewGuid();
        SetupWebResources(RemoteWebResource(webResourceId, "my_MySolution/orphan.js", "old"));
        SetupOwnership(webResourceId, ("MySolution", false));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false, runMode: RunMode.DryRun);

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r =>
                r.RequestName == "RetrieveDependenciesForDelete" && (Guid)r["ObjectId"] == webResourceId)),
            Arg.Any<CancellationToken>());

        _serviceMock.ClearReceivedCalls();

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is(Matching<OrganizationRequest>(r =>
                r.RequestName == "RetrieveDependenciesForDelete" && (Guid)r["ObjectId"] == webResourceId)),
            Arg.Any<CancellationToken>());
    }

    void SetupSolution(string solutionName, string prefix, bool isManaged = false, Guid? parentSolutionId = null)
    {
        var solution = new Entity("solution", Guid.NewGuid())
        {
            ["uniquename"] = solutionName,
            ["ismanaged"] = isManaged,
            ["publisher.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", prefix)
        };
        if (parentSolutionId.HasValue)
            solution["parentsolutionid"] = new EntityReference("solution", parentSolutionId.Value);

        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "solution")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([solution])));
    }

    void SetupWebResources(params Entity[] webResources)
    {
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "webresource" && q.LinkEntities.Count > 0)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(webResources.ToList())));
    }

    void SetupGlobalOrphans(params Entity[] webResources)
    {
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "webresource" && q.LinkEntities.Count == 0)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(webResources.ToList())));
    }

    static Entity RemoteWebResource(Guid id, string name, string content)
    {
        var entity = new Entity("webresource", id)
        {
            ["name"] = name,
            ["displayname"] = Path.GetFileName(name),
            ["content"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content)),
            ["webresourcetype"] = new OptionSetValue((int)WebResourceType.Js)
        };
        return entity;
    }

    static Entity RemoteWebResourceWithDepXml(Guid id, string name, string content, string? dependencyXml)
    {
        var entity = RemoteWebResource(id, name, content);
        if (dependencyXml != null)
            entity["dependencyxml"] = dependencyXml;
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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "solutioncomponent" &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "objectid" && c.Values.Contains(webResourceId)))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(rows)));
    }
}
