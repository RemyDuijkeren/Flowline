using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Metadata;
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

        SetupSolution("MySolution", "my");
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
        SetupEntityObjectTypeCode("account", 1);
        SetupSystemForms(1, "Account Main", (formId, "Account Main", "<form></form>"));

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
        SetupEntityObjectTypeCode("account", 1);
        SetupSystemForms(1, "Account Main", (formId, "Account Main", "<form></form>"));

        var result = await _service.SyncSolutionAsync(
            _serviceMock, _webresourceRoot, "MySolution", force: false, runMode: RunMode.DryRun);

        Assert.True(result);
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is<OrganizationRequest>(r => r.RequestName == "PublishXml"), Arg.Any<CancellationToken>());
    }

    void SetupSolution(string solutionName, string prefix)
    {
        var solution = new Entity("solution", Guid.NewGuid())
        {
            ["uniquename"] = solutionName,
            ["ismanaged"] = false,
            ["publisher.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", prefix)
        };

        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "solution"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([solution])));
    }

    void SetupEntityObjectTypeCode(string logicalName, int? objectTypeCode)
    {
        EntityMetadata? metadata = null;
        if (objectTypeCode.HasValue)
        {
            metadata = new EntityMetadata { LogicalName = logicalName };
            typeof(EntityMetadata).GetProperty("ObjectTypeCode")!.SetValue(metadata, objectTypeCode.Value);
        }

        var response = new RetrieveEntityResponse
        {
            Results = new ParameterCollection { ["EntityMetadata"] = metadata }
        };

        _serviceMock.ExecuteAsync(
                Arg.Is<OrganizationRequest>(r => r.RequestName == "RetrieveEntity" && (string)r.Parameters["LogicalName"] == logicalName),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(response));
    }

    void SetupSystemForms(int objectTypeCode, string formName, params (Guid Id, string Name, string FormXml)[] forms)
    {
        var entities = forms.Select(f => new Entity("systemform", f.Id)
        {
            ["name"] = f.Name,
            ["formxml"] = f.FormXml
        }).ToList();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "systemform" &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "objecttypecode" && c.Values.Contains(objectTypeCode)) &&
                    q.Criteria.Conditions.Any(c => c.AttributeName == "name" && c.Values.Contains(formName))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }
}
