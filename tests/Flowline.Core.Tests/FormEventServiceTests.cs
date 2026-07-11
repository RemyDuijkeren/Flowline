using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using NSubstitute;
using Flowline.Core;
using Flowline.Core.Services;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests;

// U7: FormEventService orchestrates Reader → Planner → Executor. Mocking approach mirrors
// FormEventReaderTests (solution/entity-metadata/systemform setup) and WebResourceServiceTests
// (SetupSolution/SetupWebResources shape) since the reader constructs its own WebResourceReader
// internally (KTD10) and needs the same solution/webresource plumbing to resolve local names.
public class FormEventServiceTests : IDisposable
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly TestConsole _console;
    readonly FormEventService _service;
    readonly string _webresourceRoot;

    public FormEventServiceTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _service = new FormEventService(_console);
        _webresourceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_webresourceRoot);

        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));
        _serviceMock.ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new OrganizationResponse()));

        _serviceMock.SetupSolution("MySolution", "my");
    }

    public void Dispose()
    {
        if (Directory.Exists(_webresourceRoot))
            Directory.Delete(_webresourceRoot, true);
    }

    [Fact]
    public async Task SyncSolutionAsync_NoAnnotations_ShouldNoOpAndReturnFalse()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "plain.js"), "console.log('no annotations');");

        var result = await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", force: false);

        Assert.False(result);
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_NewAnnotationOnExistingForm_ShouldUpdateAndPublishReturnTrue()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account \"Account Main\" onLoadHandler\nfunction onLoadHandler() {}");

        var formId = Guid.NewGuid();
        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemFormsInSolution((formId, "Account Main", "<form></form>", 1));

        var result = await _service.SyncSolutionAsync(_serviceMock, _webresourceRoot, "MySolution", force: false);

        Assert.True(result);
        await _serviceMock.Received(1).UpdateAsync(Arg.Is<Entity>(e => e.Id == formId), Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_DryRunWithPendingChanges_ShouldNotWriteButReturnTrue()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:onload account \"Account Main\" onLoadHandler\nfunction onLoadHandler() {}");

        var formId = Guid.NewGuid();
        _serviceMock.SetupEntityObjectTypeCode("account", 1);
        _serviceMock.SetupSystemFormsInSolution((formId, "Account Main", "<form></form>", 1));

        var result = await _service.SyncSolutionAsync(
            _serviceMock, _webresourceRoot, "MySolution", force: false, runMode: RunMode.DryRun);

        Assert.True(result);
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml"), Arg.Any<CancellationToken>());
    }
}
