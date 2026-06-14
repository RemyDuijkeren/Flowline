using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Flowline.Core.Services;
using Flowline.Core;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests;

public class OrphanCleanupServiceTests : IDisposable
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly TestConsole _console;
    readonly OrphanCleanupService _service;
    readonly string _webresourceRoot;

    public OrphanCleanupServiceTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _service = new OrphanCleanupService(_console, new FlowlineRuntimeOptions());
        _webresourceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_webresourceRoot);

        // Default: no cross-solution membership
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "solutioncomponent" && q.LinkEntities.Count > 0),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_webresourceRoot))
            Directory.Delete(_webresourceRoot, true);
    }

    void SetupSolutionComponents(string solutionName, params (Guid Id, int ComponentType)[] components)
    {
        var entities = components.Select(c => new Entity("solutioncomponent")
        {
            ["objectid"] = c.Id,
            ["componenttype"] = new OptionSetValue(c.ComponentType)
        }).ToList();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q =>
                    q.EntityName == "solutioncomponent" &&
                    q.LinkEntities.Any(le => le.LinkToEntityName == "solution")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }

    void SetupWebResourceNames(params (Guid Id, string Name)[] webResources)
    {
        var entities = webResources.Select(wr => new Entity("webresource", wr.Id)
        {
            ["name"] = wr.Name
        }).ToList();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "webresource"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }

    [Fact]
    public async Task RunPreImportAsync_AnnotationReferencedWebResource_NotDeleted()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61));
        SetupWebResourceNames((orphanId, "av_ext/shared.js"));
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:depends av_ext/shared.js\nconsole.log('hi');");

        await _service.RunPreImportAsync(_serviceMock, "MySolution", [(Guid.NewGuid(), 0)], RunMode.Normal, _webresourceRoot, default);

        await _serviceMock.DidNotReceive().DeleteAsync("webresource", orphanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPreImportAsync_AnnotationReferencedWebResource_SkipMessageEmitted()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61));
        SetupWebResourceNames((orphanId, "av_ext/shared.js"));
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"),
            "// flowline:depends av_ext/shared.js\ncode();");

        await _service.RunPreImportAsync(_serviceMock, "MySolution", [(Guid.NewGuid(), 0)], RunMode.Normal, _webresourceRoot, default);

        Assert.Contains("av_ext/shared.js", _console.Output);
        Assert.Contains("preserved", _console.Output);
    }

    [Fact]
    public async Task RunPreImportAsync_NotAnnotationReferenced_NormalOrphanHandling()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61));
        SetupWebResourceNames((orphanId, "av_ext/unref.js"));
        // No annotations referencing unref.js
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"), "// no deps\ncode();");

        await _service.RunPreImportAsync(_serviceMock, "MySolution", [(Guid.NewGuid(), 0)], RunMode.Normal, _webresourceRoot, default);

        await _serviceMock.Received(1).DeleteAsync("webresource", orphanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPreImportAsync_NoAnnotations_NoExemptions()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61));
        SetupWebResourceNames((orphanId, "av_ext/lib.js"));
        File.WriteAllText(Path.Combine(_webresourceRoot, "form.js"), "code(); // no annotations");

        await _service.RunPreImportAsync(_serviceMock, "MySolution", [(Guid.NewGuid(), 0)], RunMode.Normal, _webresourceRoot, default);

        await _serviceMock.Received(1).DeleteAsync("webresource", orphanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPreImportAsync_SameRefInMultipleFiles_DedupedSingleExemption()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61));
        SetupWebResourceNames((orphanId, "av_ext/shared.js"));
        File.WriteAllText(Path.Combine(_webresourceRoot, "a.js"),
            "// flowline:depends av_ext/shared.js\ncode();");
        File.WriteAllText(Path.Combine(_webresourceRoot, "b.js"),
            "// flowline:depends av_ext/shared.js\ncode();");

        await _service.RunPreImportAsync(_serviceMock, "MySolution", [(Guid.NewGuid(), 0)], RunMode.Normal, _webresourceRoot, default);

        await _serviceMock.DidNotReceive().DeleteAsync("webresource", orphanId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunPreImportAsync_NoWebresourceRoot_NoExemptionCheck()
    {
        var orphanId = Guid.NewGuid();
        SetupSolutionComponents("MySolution", (orphanId, 61));
        // No webresource name query is set up — if exemption ran, it would try to query
        // and the mock would return an empty collection, potentially still deleting.
        // The point is: no webresourceRoot → no name query, normal orphan flow.

        await _service.RunPreImportAsync(_serviceMock, "MySolution", [(Guid.NewGuid(), 0)], RunMode.Normal, webresourceRoot: null, ct: default);

        // With no name query setup, it falls through to delete the orphan
        await _serviceMock.Received(1).DeleteAsync("webresource", orphanId, Arg.Any<CancellationToken>());
    }
}
