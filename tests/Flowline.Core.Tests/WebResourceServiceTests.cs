using System.ServiceModel;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.PowerPlatform.Dataverse.Client;
using NSubstitute;
using Flowline.Core.Services;
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
    public async Task SyncSolutionAsync_NoPublish_ShouldSyncWithoutPublishing()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "test.js"), "console.log('test');");
        var createdId = Guid.NewGuid();
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = createdId;
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false);

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<CreateRequest>(r =>
            r.Target.GetAttributeValue<string>("name") == "my_MySolution/test.js"), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml"), Arg.Any<CancellationToken>());
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
        Assert.Contains("ownership unclear", _console.Output);
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

        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_UnsupportedExtension_ShouldThrow()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "notes.txt"), "not a web resource");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false));
    }

    [Fact]
    public async Task SyncSolutionAsync_XapFile_ShouldAbortBeforeMutating()
    {
        File.WriteAllBytes(Path.Combine(_webresourceRoot, "legacy.xap"), []);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", publishAfterSync: false));

        Assert.Contains("cannot be synced", ex.Message);
        Assert.Contains("legacy.xap", _console.Output);
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_InvalidWebResourceName_ShouldAbortBeforeMutating()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "my file.js"), "console.log('test');");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
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

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
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
                Arg.Is<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "my_MySolution/b.js"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));
        _serviceMock.ExecuteAsync(
                Arg.Is<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "my_MySolution/a.js"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromException<OrganizationResponse>(new FaultException<OrganizationServiceFault>(new OrganizationServiceFault(), "Dataverse error")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution"));

        Assert.Contains("1 web resource", ex.Message);
        Assert.Contains("my_MySolution/a.js", _console.Output);
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<OrganizationRequest>(r =>
            r.RequestName == "PublishXml" &&
            r["ParameterXml"].ToString()!.Contains(succeededId.ToString())), Arg.Any<CancellationToken>());
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

        _serviceMock.UpdateAsync(Arg.Is<Entity>(e => e.Id == id1), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new FaultException<OrganizationServiceFault>(new OrganizationServiceFault(), "Dataverse update error")));
        _serviceMock.UpdateAsync(Arg.Is<Entity>(e => e.Id == id2), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution"));

        Assert.Contains("1 web resource", ex.Message);
        Assert.Contains("my_MySolution/a.js", _console.Output);
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<OrganizationRequest>(r =>
            r.RequestName == "PublishXml" &&
            r["ParameterXml"].ToString()!.Contains(id2.ToString())), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_ExistsInOtherSolutionWithDifferentContent_ShouldUpdateAndAddToSolution()
    {
        var webResourceId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_webresourceRoot, "shared.js"), "new content");
        SetupGlobalOrphans(RemoteWebResource(webResourceId, "my_MySolution/shared.js", "old content"));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).UpdateAsync(
            Arg.Is<Entity>(e => e.Id == webResourceId), Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is<OrganizationRequest>(r =>
                r.RequestName == "AddSolutionComponent" &&
                (Guid)r["ComponentId"] == webResourceId &&
                r["SolutionUniqueName"].ToString() == "MySolution"),
            Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is<OrganizationRequest>(r =>
                r.RequestName == "PublishXml" &&
                r["ParameterXml"].ToString()!.Contains(webResourceId.ToString())),
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
            Arg.Is<OrganizationRequest>(r =>
                r.RequestName == "AddSolutionComponent" &&
                (Guid)r["ComponentId"] == webResourceId &&
                r["SolutionUniqueName"].ToString() == "MySolution"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DownloadWebResourcesAsync_NoWebResources_ShouldSkip()
    {
        await _service.DownloadWebResourcesAsync(_serviceMock, _webresourceRoot, "MySolution");

        Assert.Empty(Directory.EnumerateFiles(_webresourceRoot, "*.*", SearchOption.AllDirectories));
        Assert.Contains("skipping", _console.Output);
    }

    [Fact]
    public async Task DownloadWebResourcesAsync_WithResources_ShouldWriteFilesStrippingPrefix()
    {
        SetupWebResources(
            RemoteWebResource(Guid.NewGuid(), "my_MySolution/scripts/form.js", "console.log('test');"),
            RemoteWebResource(Guid.NewGuid(), "my_MySolution/styles/main.css", "body {}"));

        await _service.DownloadWebResourcesAsync(_serviceMock, _webresourceRoot, "MySolution");

        Assert.Equal("console.log('test');", File.ReadAllText(Path.Combine(_webresourceRoot, "scripts", "form.js")));
        Assert.Equal("body {}", File.ReadAllText(Path.Combine(_webresourceRoot, "styles", "main.css")));
    }

    [Fact]
    public async Task DownloadWebResourcesAsync_WithResources_ShouldShowSuccessCount()
    {
        SetupWebResources(
            RemoteWebResource(Guid.NewGuid(), "my_MySolution/a.js", "a"),
            RemoteWebResource(Guid.NewGuid(), "my_MySolution/b.js", "b"));

        await _service.DownloadWebResourcesAsync(_serviceMock, _webresourceRoot, "MySolution");

        Assert.Contains("2", _console.Output);
    }

    [Fact]
    public async Task DownloadWebResourcesAsync_NameWithoutPrefix_ShouldFallbackToFullNameAsPath()
    {
        SetupWebResources(RemoteWebResource(Guid.NewGuid(), "other_prefix_name.js", "content"));

        await _service.DownloadWebResourcesAsync(_serviceMock, _webresourceRoot, "MySolution");

        Assert.True(File.Exists(Path.Combine(_webresourceRoot, "other_prefix_name.js")));
    }

    [Fact]
    public async Task DownloadWebResourcesAsync_NullContent_ShouldSkipFile()
    {
        var resource = new Entity("webresource", Guid.NewGuid())
        {
            ["name"] = "my_MySolution/empty.js",
            ["displayname"] = "empty.js",
            ["webresourcetype"] = new OptionSetValue((int)WebResourceType.Js)
        };
        SetupWebResources(resource);

        await _service.DownloadWebResourcesAsync(_serviceMock, _webresourceRoot, "MySolution");

        Assert.Empty(Directory.EnumerateFiles(_webresourceRoot, "*.*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DownloadWebResourcesAsync_SolutionNotFound_ShouldThrow()
    {
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "solution"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.DownloadWebResourcesAsync(_serviceMock, _webresourceRoot, "MySolution"));
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
            Arg.Is<Entity>(e => e.Id == id && !e.Attributes.ContainsKey("dependencyxml")),
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
            Arg.Is<Entity>(e => e.Id == id && e.Attributes.ContainsKey("dependencyxml")),
            Arg.Any<CancellationToken>());
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
            Arg.Is<Entity>(e => e.Id == id && e.Attributes.ContainsKey("dependencyxml") && e["dependencyxml"] == null),
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
            Arg.Is<CreateRequest>(r => r.Target.Attributes.ContainsKey("dependencyxml")),
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
            Arg.Is<CreateRequest>(r =>
                r.Target.GetAttributeValue<string>("name") == "my_MySolution/form.js" &&
                r.Target.GetAttributeValue<string>("dependencyxml")!.Contains("name=\"my_MySolution/lib.js\"") &&
                !r.Target.GetAttributeValue<string>("dependencyxml")!.Contains("name=\"lib.js\"")),
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
            Arg.Is<CreateRequest>(r => !r.Target.Attributes.ContainsKey("dependencyxml")),
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
            Arg.Is<CreateRequest>(r =>
                r.Target.GetAttributeValue<string>("name") == "my_MySolution/Labels.js" &&
                r.Target.Attributes.ContainsKey("dependencyxml") &&
                r.Target.GetAttributeValue<string>("dependencyxml")!.Contains("my_MySolution/Labels.1033.resx")),
            Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is<CreateRequest>(r =>
                r.Target.GetAttributeValue<string>("name") == "my_MySolution/sub/Labels.js" &&
                !r.Target.Attributes.ContainsKey("dependencyxml")),
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
            Arg.Is<CreateRequest>(r =>
                r.Target.GetAttributeValue<string>("name") == "my_MySolution/Form.js" &&
                r.Target.Attributes.ContainsKey("dependencyxml") &&
                r.Target.GetAttributeValue<string>("dependencyxml")!.Contains("my_MySolution/Form.1033.resx")),
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
            Arg.Is<CreateRequest>(r =>
                r.Target.GetAttributeValue<string>("name") == "my_MySolution/Form.js" &&
                r.Target.Attributes.ContainsKey("dependencyxml") &&
                r.Target.GetAttributeValue<string>("dependencyxml")!.Contains("my_MySolution/strings.1033.resx")),
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
            Arg.Is<Entity>(e =>
                e.Id == orphanId &&
                e.Attributes.ContainsKey("dependencyxml") &&
                e.GetAttributeValue<string>("dependencyxml")!.Contains("av_Sol/new-lib.js")),
            Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is<OrganizationRequest>(r =>
                r.RequestName == "AddSolutionComponent" &&
                (Guid)r["ComponentId"] == orphanId &&
                r["SolutionUniqueName"].ToString() == "MySolution"),
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
            Arg.Is<Entity>(e => e.Id == id && e.Attributes.ContainsKey("dependencyxml") && e["dependencyxml"] == null),
            Arg.Any<CancellationToken>());
    }

    // --- Verbatim mode ---

    [Fact]
    public async Task SyncSolutionAsync_AutoPrefix_RootLevelFileWithPublisherLikeName_ShouldAutoPrefix()
    {
        // A root-level file whose name starts with a publisher-like prefix must still be auto-prefixed
        // because it has no subfolder — verbatim mode only fires when there is a containing folder.
        File.WriteAllText(Path.Combine(_webresourceRoot, "av_helper.js"), "// helper");
        var createdId = Guid.NewGuid();
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = createdId;
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));

        await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "my_MySolution/av_helper.js"),
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
            Arg.Is<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "my_MySolution/js/app.js"),
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
            Arg.Is<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "new_Other/util/helper.js"),
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
            Arg.Is<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "dh_/lib/jquery.js"),
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
            Arg.Is<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "my_MySolution/app.js"),
            Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is<CreateRequest>(r => r.Target.GetAttributeValue<string>("name") == "av_Shared/lib/util.js"),
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

        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "solution"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([solution])));
    }

    void SetupWebResources(params Entity[] webResources)
    {
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "webresource" && q.LinkEntities.Count > 0),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(webResources.ToList())));
    }

    void SetupGlobalOrphans(params Entity[] webResources)
    {
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "webresource" && q.LinkEntities.Count == 0),
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
                Arg.Is<QueryExpression>(q => q.EntityName == "solutioncomponent" &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "objectid" && c.Values.Contains(webResourceId))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(rows)));
    }
}
