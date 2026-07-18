using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using NSubstitute;
using Flowline.Core.WebResources;
using Flowline.Core.Models;
using FluentAssertions;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests.WebResources;

public class WebResourceReaderTests : IDisposable
{
    readonly IOrganizationServiceAsync2 _serviceMock;
    readonly TestConsole _console;
    readonly WebResourceReader _reader;
    readonly string _webresourceRoot;

    public WebResourceReaderTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _reader = new WebResourceReader(_console);
        _webresourceRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_webresourceRoot);

        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

        SetupSolution("MySolution", "my");
    }

    public void Dispose()
    {
        if (Directory.Exists(_webresourceRoot))
            Directory.Delete(_webresourceRoot, true);
    }

    [Fact]
    public async Task LoadSnapshotAsync_ExtensionlessFileMatchesSameSolutionResource_AdoptsDataverseType()
    {
        var webResourceId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_webresourceRoot, "my_widget"), "function widget() {}");
        SetupWebResources(RemoteWebResource(webResourceId, "my_widget", WebResourceType.Js));
        SetupOwnership(webResourceId, ("MySolution", false));

        var snapshot = await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        snapshot.LocalResources["my_widget"].Type.Should().Be(WebResourceType.Js);
    }

    [Fact]
    public async Task LoadSnapshotAsync_Tier1Resolution_PrintsWarning()
    {
        var webResourceId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_webresourceRoot, "my_widget"), "function widget() {}");
        SetupWebResources(RemoteWebResource(webResourceId, "my_widget", WebResourceType.Js));
        SetupOwnership(webResourceId, ("MySolution", false));

        await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        _console.Output.Should().Contain("my_widget").And.Contain("Js");
    }

    [Fact]
    public async Task LoadSnapshotAsync_FileWithRecognizedExtension_UntouchedByTier1EvenIfSameNameDataverseTypeDiffers()
    {
        var webResourceId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_webresourceRoot, "widget.js"), "function widget() {}");
        SetupWebResources(RemoteWebResource(webResourceId, "my_MySolution/widget.js", WebResourceType.Css));
        SetupOwnership(webResourceId, ("MySolution", false));

        var snapshot = await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        snapshot.LocalResources["my_MySolution/widget.js"].Type.Should().Be(WebResourceType.Js);
    }

    [Fact]
    public async Task LoadSnapshotAsync_ExtensionlessFileWithNoMatch_FallsThroughToTier2Sniffing()
    {
        // No Dataverse match (Tier 1 miss) and JS content — resolved by Tier 2 content sniffing instead
        // of staying Unknown, since both tiers run in sequence at the same fallback point.
        File.WriteAllText(Path.Combine(_webresourceRoot, "my_orphan"), "function orphan() {}");

        var snapshot = await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        snapshot.LocalResources["my_orphan"].Type.Should().Be(WebResourceType.Js);
    }

    [Fact]
    public async Task LoadSnapshotAsync_ExtensionlessFileWithNoMatchAndNoContentSignal_StaysUnknown()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "my_orphan"), "nothing recognizable here");

        var snapshot = await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        snapshot.LocalResources["my_orphan"].Type.Should().Be(WebResourceType.Unknown);
    }

    [Fact]
    public async Task LoadSnapshotAsync_Tier2Resolution_PrintsWarningNamingFileContentAsSource()
    {
        File.WriteAllText(Path.Combine(_webresourceRoot, "my_orphan"), "function orphan() {}");

        await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        _console.Output.Should().Contain("my_orphan").And.Contain("guessed");
    }

    [Fact]
    public async Task LoadSnapshotAsync_Tier1ResolvedJsFile_RetainsFlowlineDependsAnnotation()
    {
        var webResourceId = Guid.NewGuid();
        var depId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_webresourceRoot, "my_widget"),
            "// flowline:depends my_MySolution/helper.js\nfunction widget() {}");
        SetupWebResources(
            RemoteWebResource(webResourceId, "my_widget", WebResourceType.Js),
            RemoteWebResource(depId, "my_MySolution/helper.js", WebResourceType.Js));
        SetupOwnership(webResourceId, ("MySolution", false));
        SetupOwnership(depId, ("MySolution", false));

        var snapshot = await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        snapshot.LocalResources["my_widget"].DependsOn.Should().Contain("my_MySolution/helper.js");
    }

    [Fact]
    public async Task LoadSnapshotAsync_ExtensionlessFileMatchesGlobalOrphan_AdoptsItsTypeInsteadOfSniffing()
    {
        // The file's JS-shaped content would sniff to Js via Tier 2, but a global orphan (a record
        // owned only by another solution) exists under this name with a different type — Tier 1 must
        // adopt that record's type rather than let Tier 2's guess plan an update to a foreign record.
        var orphanId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_webresourceRoot, "my_widget"), "function widget() {}");
        SetupGlobalOrphans(RemoteWebResource(orphanId, "my_widget", WebResourceType.Html));

        var snapshot = await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        snapshot.LocalResources["my_widget"].Type.Should().Be(WebResourceType.Html);
    }

    [Fact]
    public async Task LoadSnapshotAsync_ReportedFailureShape_MultipleExtensionlessFilesAllMatched_AllResolve()
    {
        var jsId = Guid.NewGuid();
        var imgId = Guid.NewGuid();
        File.WriteAllText(Path.Combine(_webresourceRoot, "dwe_cateringjs"), "function catering() {}");
        File.WriteAllBytes(Path.Combine(_webresourceRoot, "dwe_Logoromeo"), [0xFF, 0xD8, 0xFF, 0xE0]);
        SetupWebResources(
            RemoteWebResource(jsId, "dwe_cateringjs", WebResourceType.Js),
            RemoteWebResource(imgId, "dwe_Logoromeo", WebResourceType.Jpg));
        SetupOwnership(jsId, ("MySolution", false));
        SetupOwnership(imgId, ("MySolution", false));

        var snapshot = await _reader.LoadSnapshotAsync(_serviceMock, _webresourceRoot, "MySolution");

        snapshot.LocalResources["dwe_cateringjs"].Type.Should().Be(WebResourceType.Js);
        snapshot.LocalResources["dwe_Logoromeo"].Type.Should().Be(WebResourceType.Jpg);
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

    static Entity RemoteWebResource(Guid id, string name, WebResourceType type) =>
        new("webresource", id)
        {
            ["name"] = name,
            ["displayname"] = Path.GetFileName(name),
            ["content"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x")),
            ["webresourcetype"] = new OptionSetValue((int)type)
        };

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
