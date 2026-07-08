using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Flowline.Core.Services;
using Flowline.Core.Services.OrphanCleanup;
using Flowline.Core.Services.OrphanCleanup.Handlers;

namespace Flowline.Core.Tests.OrphanCleanup.Handlers;

// Characterizes WebResourceHandler against today's OrphanCleanupServiceTests WebResource/annotation-
// exemption cases (see OrphanCleanupServiceTests.cs's SetupWebResourceNames-based tests) — this handler
// must reproduce that exact behavior, plus the new Prio3/SequenceHint/Timing fields (U3 plan).
public class WebResourceHandlerTests : IDisposable
{
    const int WebResourceComponentType = 61;

    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly WebResourceHandler _handler;
    readonly string _packageSrcRoot;
    readonly string _webResourcesDir;

    public WebResourceHandlerTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _handler = new WebResourceHandler();
        _packageSrcRoot = Path.Combine(Path.GetTempPath(), $"flowline-test-{Guid.NewGuid():N}");
        _webResourcesDir = Path.Combine(_packageSrcRoot, "WebResources");
        Directory.CreateDirectory(_webResourcesDir);

        // Default: any unconfigured RetrieveMultipleAsync returns empty rather than NSubstitute's null
        // default — real Dataverse never returns a null EntityCollection.
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_packageSrcRoot))
            Directory.Delete(_packageSrcRoot, true);
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

    DetectionContext Ctx(string? packageSrcRoot = null) => new(
        packageSrcRoot ?? _packageSrcRoot,
        _serviceMock,
        "MySolution",
        "https://example.crm.dynamics.com",
        RunMode.Normal,
        []);

    [Fact]
    public void Status_IsActive()
    {
        Assert.Equal(HandlerStatus.Active, _handler.Status);
    }

    [Fact]
    public async Task DetectAsync_OrphanedWebResourceNoAnnotationRef_AutoDeletePrio3()
    {
        var orphanId = Guid.NewGuid();
        SetupWebResourceNames((orphanId, "av_ext/unref.js"));
        File.WriteAllText(Path.Combine(_webResourcesDir, "form.js"), "// no deps\ncode();");

        var findings = await _handler.DetectAsync(Ctx(), [(orphanId, WebResourceComponentType)], default);

        var finding = Assert.Single(findings);
        Assert.Equal(orphanId, finding.ObjectId);
        Assert.Equal(WebResourceComponentType, finding.ComponentType);
        Assert.Equal(OrphanAction.Delete, finding.Action);
        Assert.Equal(OrphanPriority.Prio3, finding.Priority);
        Assert.Equal(0, finding.SequenceHint);
        Assert.Equal(OrphanTiming.PreImportEligible, finding.Timing);
        Assert.Contains("av_ext/unref.js", finding.DisplayName);
        Assert.Contains(orphanId.ToString(), finding.DisplayName);
    }

    [Fact]
    public async Task DetectAsync_AnnotationReferencedWebResource_Exempted()
    {
        var orphanId = Guid.NewGuid();
        SetupWebResourceNames((orphanId, "av_ext/shared.js"));
        File.WriteAllText(Path.Combine(_webResourcesDir, "form.js"),
            "// flowline:depends av_ext/shared.js\nconsole.log('hi');");

        var findings = await _handler.DetectAsync(Ctx(), [(orphanId, WebResourceComponentType)], default);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task DetectAsync_SameRefInMultipleFiles_StillExempted()
    {
        var orphanId = Guid.NewGuid();
        SetupWebResourceNames((orphanId, "av_ext/shared.js"));
        File.WriteAllText(Path.Combine(_webResourcesDir, "a.js"),
            "// flowline:depends av_ext/shared.js\ncode();");
        File.WriteAllText(Path.Combine(_webResourcesDir, "b.js"),
            "// flowline:depends av_ext/shared.js\ncode();");

        var findings = await _handler.DetectAsync(Ctx(), [(orphanId, WebResourceComponentType)], default);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task DetectAsync_NoWebResourcesDirUnderPackageSrc_NoExemptionCheck_ReportsOrphan()
    {
        var orphanId = Guid.NewGuid();
        SetupWebResourceNames((orphanId, "av_ext/lib.js"));
        var packageSrcRootWithoutWebResourcesDir = Path.Combine(Path.GetTempPath(), $"flowline-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(packageSrcRootWithoutWebResourcesDir);
        try
        {
            var findings = await _handler.DetectAsync(Ctx(packageSrcRootWithoutWebResourcesDir), [(orphanId, WebResourceComponentType)], default);

            var finding = Assert.Single(findings);
            Assert.Equal(orphanId, finding.ObjectId);
        }
        finally
        {
            Directory.Delete(packageSrcRootWithoutWebResourcesDir, true);
        }
    }

    [Fact]
    public async Task DetectAsync_ExemptionReadsFromPackageSrcWebResources_NotDistBuildOutput()
    {
        // Real folder shape: solutions/<Name>/WebResources/dist (build output, a sibling of Package,
        // unrelated by nesting to Package/src/WebResources). A reference sitting only in that separate
        // dist location must not exempt the orphan — the handler must only ever scan
        // context.PackageSrcRoot/WebResources.
        var orphanId = Guid.NewGuid();
        SetupWebResourceNames((orphanId, "av_ext/shared.js"));
        var distDir = Path.Combine(Path.GetTempPath(), $"flowline-test-{Guid.NewGuid():N}", "WebResources", "dist");
        Directory.CreateDirectory(distDir);
        try
        {
            File.WriteAllText(Path.Combine(distDir, "bundle.js"),
                "// flowline:depends av_ext/shared.js\ncode();");
            // No such reference under _webResourcesDir (packageSrcRoot/WebResources) itself.

            var findings = await _handler.DetectAsync(Ctx(), [(orphanId, WebResourceComponentType)], default);

            var finding = Assert.Single(findings);
            Assert.Equal(orphanId, finding.ObjectId);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(distDir))!, true);
        }
    }

    [Fact]
    public async Task DetectAsync_NonWebResourceCandidate_NotClaimed()
    {
        var pluginAssemblyId = Guid.NewGuid();

        var findings = await _handler.DetectAsync(Ctx(), [(pluginAssemblyId, 91)], default);

        Assert.Empty(findings);
    }

    [Fact]
    public async Task DetectAsync_NoCandidates_ReturnsEmpty()
    {
        var findings = await _handler.DetectAsync(Ctx(), [], default);

        Assert.Empty(findings);
    }
}
