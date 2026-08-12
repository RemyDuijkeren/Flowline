using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Flowline.Core.OrphanCleanup;

namespace Flowline.Core.Tests.OrphanCleanup;

public class ComponentTypeCatalogTests
{
    // U1: NameResolvableTypes/ManualTypeLabels/ResolveGroupNamesAsync moved here from
    // OrphanCleanupService (KTD3) — same maps, same empty-on-unknown-type behavior.

    [Fact]
    public async Task ResolveGroupNamesAsync_ComponentTypePresentInMap_ResolvesRecordNames()
    {
        var service = Substitute.For<IOrganizationServiceAsync2>();
        var id = Guid.NewGuid();
        service.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "webresource"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([
                new Entity("webresource", id) { ["name"] = "av_ext/shared.js" }
            ])));

        var names = await ComponentTypeCatalog.ResolveGroupNamesAsync(service, 61, [id], default);

        Assert.Equal("av_ext/shared.js", Assert.Single(names).Value);
    }

    [Fact]
    public async Task ResolveGroupNamesAsync_ComponentTypeAbsentFromMap_ResolvesEmptyWithoutThrowing()
    {
        var service = Substitute.For<IOrganizationServiceAsync2>();

        var names = await ComponentTypeCatalog.ResolveGroupNamesAsync(service, 9999, [Guid.NewGuid()], default);

        Assert.Empty(names);
        await service.DidNotReceiveWithAnyArgs().RetrieveMultipleAsync(default!, default);
    }

    // Site map (62) added by U1 — verified against learn.microsoft.com/power-apps/developer/data-platform/
    // reference/entities/sitemap: LogicalName "sitemap", PrimaryIdAttribute "sitemapid", name attribute
    // "sitemapname".
    [Fact]
    public void NameResolvableTypes_ContainsSiteMapEntry()
    {
        var lookup = ComponentTypeCatalog.NameResolvableTypes[62];

        Assert.Equal(("sitemap", "sitemapid", "sitemapname"), lookup);
    }

    [Fact]
    public void ManualTypeLabels_ContainsSiteMapEntry()
    {
        Assert.Equal("SiteMap", ComponentTypeCatalog.ManualTypeLabels[62]);
    }
}
