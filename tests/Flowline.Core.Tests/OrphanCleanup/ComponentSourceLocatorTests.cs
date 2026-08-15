using Flowline.Core.OrphanCleanup;

namespace Flowline.Core.Tests.OrphanCleanup;

// U2: ComponentSourceLocator is pure string composition from a handler-declared LocalSourceIdentity to
// where that identity lives in the unpacked solution source — no filesystem access (on deploy the
// compare ran against a temp extraction, so an existence check would test the wrong tree).
public class ComponentSourceLocatorTests
{
    [Fact]
    public void Locate_Role_ResolvesToOwnFileUnderRolesFolder()
    {
        var location = ComponentSourceLocator.Locate(LocalSourceIdentity.Role("System Administrator"));

        Assert.NotNull(location);
        Assert.Equal(SourceLocationKind.File, location!.Kind);
        Assert.Equal("Roles/System Administrator.xml", location.RelativePath);
    }

    [Fact]
    public void Locate_WebResource_ResolvesToOwnFileUnderWebResourcesFolder()
    {
        var location = ComponentSourceLocator.Locate(LocalSourceIdentity.WebResource("av_ext/shared.js"));

        Assert.NotNull(location);
        Assert.Equal(SourceLocationKind.File, location!.Kind);
        Assert.Equal("WebResources/av_ext/shared.js", location.RelativePath);
    }

    [Fact]
    public void Locate_ConnectionReference_ResolvesToCustomizationsFileWithBothMarkerEncodings()
    {
        var location = ComponentSourceLocator.Locate(LocalSourceIdentity.ConnectionReference("av_sharedconn"));

        Assert.NotNull(location);
        Assert.Equal(SourceLocationKind.Inline, location!.Kind);
        Assert.Equal("Other/Customizations.xml", location.RelativePath);
        Assert.Contains("connectionreferencelogicalname=\"av_sharedconn\"", location.InlineMarkers);
        Assert.Contains("<connectionreferencelogicalname>av_sharedconn</connectionreferencelogicalname>", location.InlineMarkers);
    }

    [Fact]
    public void Locate_Bot_ResolvesToSchemaNamedFolder()
    {
        var location = ComponentSourceLocator.Locate(LocalSourceIdentity.SchemaNamedFolder("bots", "av_mybot"));

        Assert.NotNull(location);
        Assert.Equal(SourceLocationKind.Folder, location!.Kind);
        Assert.Equal("bots/av_mybot", location.RelativePath);
    }

    [Fact]
    public void Locate_CustomApi_ResolvesToSchemaNamedFolder()
    {
        var location = ComponentSourceLocator.Locate(LocalSourceIdentity.SchemaNamedFolder("customapis", "av_MyApi"));

        Assert.NotNull(location);
        Assert.Equal(SourceLocationKind.Folder, location!.Kind);
        Assert.Equal("customapis/av_MyApi", location.RelativePath);
    }

    [Fact]
    public void Locate_EntityAttribute_ResolvesToOwningEntityFilePlusLogicalNameMarker()
    {
        var location = ComponentSourceLocator.Locate(LocalSourceIdentity.EntityAttribute("av_project", "av_taxid"));

        Assert.NotNull(location);
        Assert.Equal(SourceLocationKind.Inline, location!.Kind);
        Assert.Equal("Entities/av_project/Entity.xml", location.RelativePath);
        Assert.Contains("<LogicalName>av_taxid</LogicalName>", location.InlineMarkers);
    }

    [Fact]
    public void Locate_NoDeclaredShape_ReturnsNullAndDoesNotFallThroughToAnotherShape()
    {
        var location = ComponentSourceLocator.Locate(LocalSourceIdentity.None);

        Assert.Null(location);
    }

    [Fact]
    public void Locate_ConnectionReference_ResolvesIdenticallyUnderDifferentComponentTypeCodes()
    {
        // Two HandlerFindings differing only in ComponentType (an environment-assigned code) — the
        // locator never sees ComponentType at all, only the declared LocalSourceIdentity, so it cannot
        // depend on it (R12/KTD4).
        var findingDevOrg = new HandlerFinding(
            Guid.NewGuid(), ComponentType: 10248, DisplayName: "ConnectionReference 'av_sharedconn'",
            Action: OrphanAction.Manual, Priority: OrphanPriority.Prio2, SequenceHint: 0,
            Timing: OrphanTiming.PreImportEligible)
        { Identity = LocalSourceIdentity.ConnectionReference("av_sharedconn") };

        var findingProdOrg = new HandlerFinding(
            Guid.NewGuid(), ComponentType: 10391, DisplayName: "ConnectionReference 'av_sharedconn'",
            Action: OrphanAction.Manual, Priority: OrphanPriority.Prio2, SequenceHint: 0,
            Timing: OrphanTiming.PreImportEligible)
        { Identity = LocalSourceIdentity.ConnectionReference("av_sharedconn") };

        var locationDev  = ComponentSourceLocator.Locate(findingDevOrg.Identity);
        var locationProd = ComponentSourceLocator.Locate(findingProdOrg.Identity);

        // ComponentSourceLocation (U1, fixed) has no custom equality — InlineMarkers is a plain
        // IReadOnlyList<string>, so record equality would reference-compare it. Compare content instead.
        Assert.NotNull(locationDev);
        Assert.NotNull(locationProd);
        Assert.Equal(locationDev!.Kind, locationProd!.Kind);
        Assert.Equal(locationDev.RelativePath, locationProd.RelativePath);
        Assert.Equal(locationDev.InlineMarkers, locationProd.InlineMarkers);
    }

    [Theory]
    [MemberData(nameof(AllDeclaredShapes))]
    public void Locate_EveryDeclaredShape_ReturnsRelativePathNeverAbsolute(LocalSourceIdentity identity)
    {
        var location = ComponentSourceLocator.Locate(identity);

        Assert.NotNull(location);
        Assert.False(Path.IsPathRooted(location!.RelativePath));
        Assert.DoesNotMatch(@"^[A-Za-z]:", location.RelativePath);
        Assert.False(location.RelativePath.StartsWith('/'));
        Assert.False(location.RelativePath.StartsWith('\\'));
    }

    public static IEnumerable<object[]> AllDeclaredShapes()
    {
        yield return [LocalSourceIdentity.Role("System Administrator")];
        yield return [LocalSourceIdentity.WebResource("av_ext/shared.js")];
        yield return [LocalSourceIdentity.SchemaNamedFolder("bots", "av_mybot")];
        yield return [LocalSourceIdentity.SchemaNamedFolder("customapis", "av_MyApi")];
        yield return [LocalSourceIdentity.ConnectionReference("av_sharedconn")];
        yield return [LocalSourceIdentity.EntityAttribute("av_project", "av_taxid")];
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Locate_BlankOrWhitespaceKey_ReturnsNull(string blankKey)
    {
        var location = ComponentSourceLocator.Locate(LocalSourceIdentity.Role(blankKey));

        Assert.Null(location);
    }
}
