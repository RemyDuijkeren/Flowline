using Flowline.Core.Models;
using Flowline.Core.Services;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;

namespace Flowline.Core.Tests;

// U4: PluginReader.LoadPackageSnapshotsAsync — N independently-scoped RegistrationSnapshots for a
// package's plugin-bearing assemblies (KD5, KTD15). The KTD15 regression guard below is the load-bearing
// test in this file: it proves there is no merged/shared PluginTypes dictionary anywhere that a later
// diff (PluginPlanner.Plan) could misread as "the other assembly's types are gone".
public class PluginReaderTests
{
    private readonly IOrganizationServiceAsync2 _serviceMock;
    private readonly PluginReader _reader;

    public PluginReaderTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _reader = new PluginReader();

        // Default empty results for all queries
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>())
            .Returns(Task.FromResult(new EntityCollection()));
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

        var defaultSolution = new Entity("solution")
        {
            ["pub.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", "abc")
        };
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "solution"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { defaultSolution })));

        // Solution membership lookups (round 3) — mirror PluginServiceTests: return no memberships,
        // not relevant to these scenarios.
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "solutioncomponent"),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));
    }

    private static PluginAssemblyMetadata Metadata(string name) =>
        new(name, $"{name}, Version=1.0.0.0", new byte[] { 1, 2, 3 }, "deadbeef", "1.0.0.0", null, "neutral", []);

    private static bool HasCondition(QueryExpression query, string attributeName, object value) =>
        query.Criteria.Conditions.Any(c =>
            string.Equals(c.AttributeName, attributeName, StringComparison.OrdinalIgnoreCase) &&
            c.Values.Count > 0 &&
            Equals(c.Values[0], value));

    // Mocks the pluginassembly lookup FindPackageAssemblyAsync performs, scoped to packageid + name.
    private void SetupPackageAssembly(Guid packageId, Guid assemblyId, string assemblyName)
    {
        var entity = new Entity("pluginassembly", assemblyId) { ["name"] = assemblyName };
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "pluginassembly"
                    && HasCondition(q, "packageid", packageId)
                    && HasCondition(q, "name", assemblyName)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { entity })));
    }

    // Mocks GetRegisteredPluginTypesAsync's query, differentiated by assemblyId — critical for the
    // KTD15 guard: without this, every assembly's query would return the same result regardless of
    // pluginassemblyid, and the "assembly A never sees B's types" assertion would pass vacuously.
    private void SetupPluginTypesFor(Guid assemblyId, params Entity[] types)
    {
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "plugintype" && HasCondition(q, "pluginassemblyid", assemblyId)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(types.ToList())));
    }

    private void SetupSteps(params Entity[] steps)
    {
        foreach (var s in steps)
        {
            if (!s.Contains("plugintypeid"))
                s["plugintypeid"] = new EntityReference("plugintype", Guid.NewGuid());
            if (!s.Contains("stage"))
                s["stage"] = new OptionSetValue(20);
        }
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep"))
            .Returns(Task.FromResult(new EntityCollection(steps.ToList())));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(steps.ToList())));
    }

    private void SetupImages(params Entity[] images)
    {
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstepimage"))
            .Returns(Task.FromResult(new EntityCollection(images.ToList())));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstepimage"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(images.ToList())));
    }

    private void SetupCustomApis(params Entity[] apis)
    {
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "customapi"))
            .Returns(Task.FromResult(new EntityCollection(apis.ToList())));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "customapi"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(apis.ToList())));
    }

    private void SetupRequestParameters(params Entity[] parameters)
    {
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "customapirequestparameter"))
            .Returns(Task.FromResult(new EntityCollection(parameters.ToList())));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "customapirequestparameter"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(parameters.ToList())));
    }

    private void SetupResponseProperties(params Entity[] properties)
    {
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "customapiresponseproperty"))
            .Returns(Task.FromResult(new EntityCollection(properties.ToList())));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "customapiresponseproperty"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(properties.ToList())));
    }

    // -- Happy path: single assembly matches the classic path's shape (regression guard) --

    [Fact]
    public async Task LoadPackageSnapshotsAsync_SingleAssembly_MatchesClassicPathShape()
    {
        var packageId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        var pluginTypeId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var customApiId = Guid.NewGuid();
        var metadata = Metadata("AssemblyA");

        SetupPackageAssembly(packageId, assemblyId, "AssemblyA");
        SetupPluginTypesFor(assemblyId, new Entity("plugintype", pluginTypeId)
        {
            ["typename"] = "NsA.TypeA",
            ["isworkflowactivity"] = false
        });
        SetupSteps(new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"] = "NsA.TypeA: Update of account",
            ["plugintypeid"] = new EntityReference("plugintype", pluginTypeId),
            ["stage"] = new OptionSetValue(20)
        });
        SetupImages(new Entity("sdkmessageprocessingstepimage", Guid.NewGuid())
        {
            ["name"] = "PreImage",
            ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", stepId)
        });
        SetupCustomApis(new Entity("customapi", customApiId)
        {
            ["uniquename"] = "abc_MyApi",
            ["plugintypeid"] = new EntityReference("plugintype", pluginTypeId)
        });
        SetupRequestParameters(new Entity("customapirequestparameter", Guid.NewGuid())
        {
            ["uniquename"] = "abc_Input",
            ["customapiid"] = new EntityReference("customapi", customApiId)
        });
        SetupResponseProperties(new Entity("customapiresponseproperty", Guid.NewGuid())
        {
            ["uniquename"] = "abc_Output",
            ["customapiid"] = new EntityReference("customapi", customApiId)
        });

        var classic = await _reader.LoadSnapshotAsync(_serviceMock, assemblyId, metadata, "MySolution");
        var results = await _reader.LoadPackageSnapshotsAsync(_serviceMock, packageId, [metadata], "MySolution");

        Assert.Single(results);
        var (resultMetadata, assembly, snapshot) = results[0];
        Assert.Same(metadata, resultMetadata);
        Assert.NotNull(assembly);
        Assert.Equal(assemblyId, assembly!.Id);
        Assert.NotNull(snapshot);

        Assert.Equal(classic.PublisherPrefix, snapshot!.PublisherPrefix);
        Assert.Equal(classic.PluginTypes.Keys.OrderBy(k => k), snapshot.PluginTypes.Keys.OrderBy(k => k));
        Assert.Equal(classic.Steps.Select(e => e.Id).OrderBy(id => id), snapshot.Steps.Select(e => e.Id).OrderBy(id => id));
        Assert.Equal(classic.Images.Select(e => e.Id).OrderBy(id => id), snapshot.Images.Select(e => e.Id).OrderBy(id => id));
        Assert.Equal(classic.CustomApis.Select(e => e.Id).OrderBy(id => id), snapshot.CustomApis.Select(e => e.Id).OrderBy(id => id));
        Assert.Equal(classic.RequestParams.Select(e => e.Id).OrderBy(id => id), snapshot.RequestParams.Select(e => e.Id).OrderBy(id => id));
        Assert.Equal(classic.ResponseProps.Select(e => e.Id).OrderBy(id => id), snapshot.ResponseProps.Select(e => e.Id).OrderBy(id => id));
        Assert.Equal(classic.SdkMessageIds.Count, snapshot.SdkMessageIds.Count);
        Assert.Equal(classic.SystemUserIds.Count, snapshot.SystemUserIds.Count);
        Assert.Equal(classic.ComponentSolutionMembership.Count, snapshot.ComponentSolutionMembership.Count);
        Assert.Equal(classic.ComponentTypeById.Count, snapshot.ComponentTypeById.Count);
    }

    // -- Happy path: two assemblies with distinct plugin types produce independent snapshots --

    [Fact]
    public async Task LoadPackageSnapshotsAsync_TwoAssembliesDistinctTypes_ProducesIndependentSnapshots()
    {
        var packageId = Guid.NewGuid();
        var assemblyIdA = Guid.NewGuid();
        var assemblyIdB = Guid.NewGuid();
        var metadataA = Metadata("AssemblyA");
        var metadataB = Metadata("AssemblyB");

        SetupPackageAssembly(packageId, assemblyIdA, "AssemblyA");
        SetupPackageAssembly(packageId, assemblyIdB, "AssemblyB");
        SetupPluginTypesFor(assemblyIdA, new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "NsA.TypeA" });
        SetupPluginTypesFor(assemblyIdB, new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "NsB.TypeB" });

        var results = await _reader.LoadPackageSnapshotsAsync(_serviceMock, packageId, [metadataA, metadataB], "MySolution");

        Assert.Equal(2, results.Count);
        var resultA = results.Single(r => r.Metadata.Name == "AssemblyA");
        var resultB = results.Single(r => r.Metadata.Name == "AssemblyB");

        Assert.NotNull(resultA.Snapshot);
        Assert.NotNull(resultB.Snapshot);
        Assert.Equal(["NsA.TypeA"], resultA.Snapshot!.PluginTypes.Keys);
        Assert.Equal(["NsB.TypeB"], resultB.Snapshot!.PluginTypes.Keys);

        // Requirement: publisher prefix is resolved ONCE per package and reused across all N
        // assemblies — not re-queried per assembly. A regression back to per-assembly resolution
        // (e.g. calling public LoadSnapshotAsync N times) would still pass every content assertion
        // above (the prefix value is identical either way) but would query "solution" twice here.
        await _serviceMock.Received(1).RetrieveMultipleAsync(
            Arg.Is<QueryExpression>(q => q.EntityName == "solution"),
            Arg.Any<CancellationToken>());
    }

    // -- KTD15 regression guard: an assembly's snapshot must never contain another assembly's plugin
    // types. A merged/shared-dictionary design (the bug KTD15 documents) would union both assemblies'
    // GetRegisteredPluginTypesAsync results into one dictionary reused by every returned snapshot — so
    // resultA.Snapshot.PluginTypes would contain "NsB.TypeB" too, and the DoesNotContain assertion below
    // would fail. Because SetupPluginTypesFor differentiates its mocked response by pluginassemblyid,
    // the correct per-assembly implementation naturally produces disjoint dictionaries here; a future
    // regression that reintroduces merging would flip this assertion. --

    [Fact]
    public async Task LoadPackageSnapshotsAsync_TwoAssembliesOneHasUniqueType_OtherAssemblySnapshotNeverContainsIt()
    {
        var packageId = Guid.NewGuid();
        var assemblyIdA = Guid.NewGuid();
        var assemblyIdB = Guid.NewGuid();
        var metadataA = Metadata("AssemblyA");
        var metadataB = Metadata("AssemblyB");

        SetupPackageAssembly(packageId, assemblyIdA, "AssemblyA");
        SetupPackageAssembly(packageId, assemblyIdB, "AssemblyB");
        // AssemblyA has a plugin type that AssemblyB does not have at all.
        SetupPluginTypesFor(assemblyIdA, new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "NsA.OnlyInA" });
        SetupPluginTypesFor(assemblyIdB); // no plugin types registered for B

        var results = await _reader.LoadPackageSnapshotsAsync(_serviceMock, packageId, [metadataA, metadataB], "MySolution");

        var resultA = results.Single(r => r.Metadata.Name == "AssemblyA");
        var resultB = results.Single(r => r.Metadata.Name == "AssemblyB");

        Assert.Contains("NsA.OnlyInA", resultA.Snapshot!.PluginTypes.Keys);
        // The genuinely load-bearing assertion: B's snapshot must not merely be "correctly attributed"
        // but must not contain A's type key AT ALL.
        Assert.DoesNotContain("NsA.OnlyInA", resultB.Snapshot!.PluginTypes.Keys);
        Assert.Empty(resultB.Snapshot!.PluginTypes);
    }

    // -- Edge case: one reflected DLL has no matching auto-created pluginassembly yet --

    [Fact]
    public async Task LoadPackageSnapshotsAsync_OneAssemblyNotYetPresent_ReportsNotPresentWithoutBlockingOthers()
    {
        var packageId = Guid.NewGuid();
        var assemblyIdA = Guid.NewGuid();
        var metadataA = Metadata("AssemblyA");
        var metadataMissing = Metadata("AssemblyMissing");

        SetupPackageAssembly(packageId, assemblyIdA, "AssemblyA");
        SetupPluginTypesFor(assemblyIdA, new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "NsA.TypeA" });
        // No SetupPackageAssembly for "AssemblyMissing" — default mock returns empty EntityCollection,
        // simulating mid-poll: reflected locally but not yet auto-created in Dataverse.

        var results = await _reader.LoadPackageSnapshotsAsync(_serviceMock, packageId, [metadataA, metadataMissing], "MySolution");

        Assert.Equal(2, results.Count);
        var resultA = results.Single(r => r.Metadata.Name == "AssemblyA");
        var resultMissing = results.Single(r => r.Metadata.Name == "AssemblyMissing");

        Assert.NotNull(resultA.Assembly);
        Assert.NotNull(resultA.Snapshot);
        Assert.Contains("NsA.TypeA", resultA.Snapshot!.PluginTypes.Keys);

        Assert.Null(resultMissing.Assembly);
        Assert.Null(resultMissing.Snapshot);
    }
}
