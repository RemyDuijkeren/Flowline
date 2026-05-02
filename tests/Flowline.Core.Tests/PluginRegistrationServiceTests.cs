using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using NSubstitute;
using Flowline.Core.Services;
using Flowline.Core.Models;
using Flowline.Core;

namespace Flowline.Core.Tests;

public class PluginRegistrationServiceTests
{
    private readonly IOrganizationServiceAsync2 _serviceMock;
    private readonly IFlowlineOutput _outputMock;
    private readonly PluginRegistrationService _service;

    public PluginRegistrationServiceTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _outputMock = Substitute.For<IFlowlineOutput>();
        _service = new PluginRegistrationService(_outputMock);

        // Default empty results for all queries
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>())
            .Returns(Task.FromResult(new EntityCollection()));
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

        var defaultMessage = new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" };
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessage"))
            .Returns(Task.FromResult(new EntityCollection([defaultMessage])));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessage"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([defaultMessage])));

        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter"))
            .Returns(Task.FromResult(new EntityCollection()));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

        var defaultSolution = new Entity("solution")
        {
            ["pub.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", "abc")
        };
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "solution"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { defaultSolution })));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "solutioncomponent" && q.Criteria.Conditions.Any(c => c.AttributeName == "objectid")),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var query = callInfo.Arg<QueryExpression>();
                var objectId = GetGuidConditionValue(query, "objectid");
                if (!objectId.HasValue)
                    return Task.FromResult(new EntityCollection());

                return Task.FromResult(new EntityCollection(new List<Entity>
                {
                    new("solutioncomponent")
                    {
                        ["componenttype"] = new OptionSetValue(ResolveComponentTypeFromObjectId(objectId.Value))
                    }
                }));
            });
    }

    private static int ResolveComponentTypeFromObjectId(Guid objectId)
    {
        var text = objectId.ToString();
        if (text.EndsWith("67", StringComparison.OrdinalIgnoreCase))
            return 10067;
        if (text.EndsWith("68", StringComparison.OrdinalIgnoreCase))
            return 10068;

        return 10066;
    }

    private static Guid? GetGuidConditionValue(QueryExpression query, string attribute)
    {
        var condition = query.Criteria.Conditions.FirstOrDefault(c =>
            string.Equals(c.AttributeName, attribute, StringComparison.OrdinalIgnoreCase));

        if (condition?.Values == null || condition.Values.Count == 0)
            return null;

        return condition.Values[0] as Guid?;
    }

    // -- Helpers --

    private Entity ExistingAssembly(Guid id, string version = "1.0.0.0", string? hash = null, string? pkt = null, string culture = "neutral")
    {
        var e = new Entity("pluginassembly", id);
        e["name"] = "MyPlugin";
        e["version"] = version;
        e["culture"] = culture;
        if (pkt != null)
            e["publickeytoken"] = pkt;
        if (hash != null)
            e["description"] = $"[flowline] sha256={hash}";
        return e;
    }

    private void SetupAssembly(Entity? existing = null)
    {
        if (existing == null)
        {
            _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "pluginassembly"))
                .Returns(Task.FromResult(new EntityCollection()));
            _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "pluginassembly"), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new EntityCollection()));
            var createResponse = new CreateResponse();
            createResponse.Results["id"] = Guid.NewGuid();
            _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>())
                .Returns(Task.FromResult<OrganizationResponse>(createResponse));
            _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<OrganizationResponse>(createResponse));
        }
        else
        {
            _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "pluginassembly"))
                .Returns(Task.FromResult(new EntityCollection(new List<Entity> { existing })));
            _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "pluginassembly"), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new EntityCollection(new List<Entity> { existing })));
        }
    }

    private void SetupPluginTypes(params Entity[] types)
    {
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "plugintype"))
            .Returns(Task.FromResult(new EntityCollection(types.ToList())));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "plugintype"), Arg.Any<CancellationToken>())
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
        // Mirror the real Dataverse query: GetRegisteredStepsAsync excludes stage=30 (internal CustomAPI steps)
        var queryableSteps = steps.Where(s => s.GetAttributeValue<OptionSetValue>("stage")?.Value != 30).ToList();
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep"))
            .Returns(Task.FromResult(new EntityCollection(queryableSteps)));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(queryableSteps)));
    }

    private void SetupImages(params Entity[] images)
    {
        foreach (var i in images)
        {
            if (!i.Contains("sdkmessageprocessingstepid"))
                i["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", Guid.NewGuid());
        }
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstepimage"))
            .Returns(Task.FromResult(new EntityCollection(images.ToList())));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstepimage"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(images.ToList())));
    }

    private PluginAssemblyMetadata Metadata(string name = "MyPlugin", string version = "1.0.0.0", string hash = "deadbeef", string? pkt = null, string culture = "neutral", params PluginTypeMetadata[] plugins) =>
        new(name, $"{name}, Version={version}", new byte[] { 1, 2, 3 }, hash, version, pkt, culture, plugins.ToList());

    private static bool HasCondition(QueryExpression query, string attributeName, object value)
    {
        return query.Criteria.Conditions.Any(c =>
            string.Equals(c.AttributeName, attributeName, StringComparison.OrdinalIgnoreCase) &&
            c.Values.Count > 0 &&
            Equals(c.Values[0], value));
    }

    // -- Assembly create/update --

    [Fact]
    public async Task SyncSolutionAsync_NewAssembly_CreatesWithSolutionName()
    {
        SetupAssembly();
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock, Metadata(), "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<CreateRequest>(r =>
            r.Target.LogicalName == "pluginassembly" &&
            r.Target.GetAttributeValue<string>("name") == "MyPlugin" &&
            r["SolutionUniqueName"].ToString() == "MySolution"
        ), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_ExistingAssembly_UpdatesVersion()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, "1.0.0.0"));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock, Metadata(version: "1.0.0.1"), "MySolution");

        await _serviceMock.Received(1).UpdateAsync(Arg.Is<Entity>(e =>
            e.LogicalName == "pluginassembly" &&
            e.Id == assemblyId &&
            e.GetAttributeValue<string>("version") == "1.0.0.1"
        ), Arg.Any<CancellationToken>());
    }

    // -- Plugin type creation --

    [Fact]
    public async Task SyncSolutionAsync_NewPluginType_CreatesPluginType()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();
        SetupSteps();

        await _service.SyncAsync(_serviceMock, Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], null, false)), "MySolution");

        await _serviceMock.Received(1).CreateAsync(Arg.Is<Entity>(e =>
            e.LogicalName == "plugintype" &&
            e.GetAttributeValue<string>("typename") == "MyNamespace.MyPlugin" &&
            !e.Contains("workflowactivitygroupname")
        ), Arg.Any<CancellationToken>());
    }

    // -- Workflow type creation --

    [Fact]
    public async Task SyncSolutionAsync_NewWorkflowType_SetsWorkflowActivityGroupName()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock, Metadata(plugins: new PluginTypeMetadata("MyActivity", "MyNamespace.MyActivity", [], null, true)), "MySolution");

        await _serviceMock.Received(1).CreateAsync(Arg.Is<Entity>(e =>
            e.LogicalName == "plugintype" &&
            e.GetAttributeValue<string>("typename") == "MyNamespace.MyActivity" &&
            e.GetAttributeValue<string>("workflowactivitygroupname") == "MyPlugin (1.0.0.0)"
        ), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_WorkflowType_SnapshotAlwaysQueriesSteps()
    {
        // Snapshot-based design always loads steps upfront regardless of assembly content
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock, Metadata(plugins: new PluginTypeMetadata("MyActivity", "MyNamespace.MyActivity", [], null, true)), "MySolution");

        await _serviceMock.Received(1).RetrieveMultipleAsync(
            Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep"),
            Arg.Any<CancellationToken>());
    }

    // -- Deletion of obsolete types --

    [Fact]
    public async Task SyncSolutionAsync_ObsoletePluginType_DeletesStepsThenType()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));

        var obsoleteTypeId = Guid.NewGuid();
        var obsoleteType = new Entity("plugintype", obsoleteTypeId)
        {
            ["typename"] = "Obsolete.Plugin",
            ["isworkflowactivity"] = false
        };
        SetupPluginTypes(obsoleteType);

        var stepId = Guid.NewGuid();
        var obsoleteStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"] = "Obsolete.Plugin: Update of account",
            ["plugintypeid"] = new EntityReference("plugintype", obsoleteTypeId)
        };
        SetupSteps(obsoleteStep);

        await _service.SyncAsync(_serviceMock, Metadata(), "MySolution"); // no plugins in assembly

        await _serviceMock.Received(1).DeleteAsync("sdkmessageprocessingstep", stepId, Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).DeleteAsync("plugintype", obsoleteTypeId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_ObsoleteWorkflowType_DeletesType()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));

        var obsoleteTypeId = Guid.NewGuid();
        var obsoleteType = new Entity("plugintype", obsoleteTypeId)
        {
            ["typename"] = "Obsolete.Activity",
            ["isworkflowactivity"] = true
        };
        SetupPluginTypes(obsoleteType);

        await _service.SyncAsync(_serviceMock, Metadata(), "MySolution");

        await _serviceMock.Received(1).DeleteAsync("plugintype", obsoleteTypeId, Arg.Any<CancellationToken>());
    }

    // -- DLL as source of truth: all orphaned steps deleted --

    [Fact]
    public async Task SyncSolutionAsync_PluginWithNoSteps_DeletesAllExistingSteps()
    {
        // [Entity] removed to disable a plugin — Flowline deletes all steps for that type
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));

        var pluginType = new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "MyNamespace.MyPlugin", ["isworkflowactivity"] = false };
        SetupPluginTypes(pluginType);

        var stepId = Guid.NewGuid();
        var existingStep = new Entity("sdkmessageprocessingstep", stepId) { ["name"] = "Old step", ["plugintypeid"] = pluginType.ToEntityReference() };
        SetupSteps(existingStep);

        await _service.SyncAsync(_serviceMock, Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], null, false)), "MySolution");

        await _serviceMock.Received(1).DeleteAsync("sdkmessageprocessingstep", stepId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_PluginWithSteps_DeletesOrphanedSteps()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));

        var pluginType = new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "MyNamespace.MyPlugin", ["isworkflowactivity"] = false };
        SetupPluginTypes(pluginType);

        var orphanId = Guid.NewGuid();
        SetupSteps(new Entity("sdkmessageprocessingstep", orphanId) { ["name"] = "Orphaned step", ["plugintypeid"] = pluginType.ToEntityReference() });
        SetupImages();

        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessage"))
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } })));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessage"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } })));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter"))
            .Returns(Task.FromResult(new EntityCollection()));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of contact", "Update", "contact", 20, 0, 1, null, null, [], []);

        await _service.SyncAsync(_serviceMock, Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], null, false)), "MySolution");

        await _serviceMock.Received(1).DeleteAsync("sdkmessageprocessingstep", orphanId, Arg.Any<CancellationToken>());
    }

    // -- Hash-based change detection --

    [Fact]
    public async Task SyncAsync_UnchangedAssembly_SkipsUpload()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "abc123"));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock, Metadata(hash: "abc123"), "MySolution");

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Is<Entity>(e => e.LogicalName == "pluginassembly"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_ChangedAssembly_UploadsNewContent()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock, Metadata(hash: "newhash"), "MySolution");

        await _serviceMock.Received(1).UpdateAsync(Arg.Is<Entity>(e =>
            e.LogicalName == "pluginassembly" &&
            e.GetAttributeValue<string>("description") == "[flowline] sha256=newhash"
        ), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_ExistingAssemblyInOtherSolutions_EmitsWarningWithSolutionNames()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));
        SetupPluginTypes();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "solutioncomponent" &&
                                            HasCondition(q, "componenttype", 91) &&
                                            HasCondition(q, "objectid", assemblyId)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity>
            {
                new("solutioncomponent") { ["sol.uniquename"] = new AliasedValue("solution", "uniquename", "OtherSolutionA") },
                new("solutioncomponent") { ["sol.uniquename"] = new AliasedValue("solution", "uniquename", "MySolution") },
                new("solutioncomponent") { ["sol.uniquename"] = new AliasedValue("solution", "uniquename", "OtherSolutionB") }
            })));

        await _service.SyncAsync(_serviceMock, Metadata(hash: "newhash"), "MySolution");

        _outputMock.Received(1).Warning(Arg.Is<string>(s =>
            s.Contains("Updating assembly") &&
            s.Contains("OtherSolutionA") &&
            s.Contains("OtherSolutionB") &&
            !s.Contains("MySolution")));
    }

    [Fact]
    public async Task SyncAsync_ExistingStepInOtherSolutions_EmitsWarning()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));

        var pluginType = new Entity("plugintype", Guid.NewGuid())
        {
            ["typename"] = "MyNamespace.MyPlugin",
            ["isworkflowactivity"] = false
        };
        SetupPluginTypes(pluginType);

        var existingStepId = Guid.NewGuid();
        SetupSteps(new Entity("sdkmessageprocessingstep", existingStepId)
        {
            ["name"] = "MyNamespace.MyPlugin: Update of contact",
            ["plugintypeid"] = pluginType.ToEntityReference()
        });
        SetupImages();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "solutioncomponent" &&
                                            HasCondition(q, "componenttype", 92) &&
                                            HasCondition(q, "objectid", existingStepId)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity>
            {
                new("solutioncomponent") { ["sol.uniquename"] = new AliasedValue("solution", "uniquename", "SharedSolution") }
            })));

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of contact", "Update", "contact", 20, 0, 1, null, null, [], []);
        await _service.SyncAsync(_serviceMock, Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], null, false)), "MySolution");

        _outputMock.Received(1).Warning(Arg.Is<string>(s =>
            s.Contains("Updating sdkmessageprocessingstep") &&
            s.Contains("SharedSolution")));
    }

    [Fact]
    public async Task SyncAsync_ExistingCustomApiWithoutOtherSolutions_DoesNotEmitCrossSolutionWarning()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));

        var pluginTypeEntity = new Entity("plugintype", Guid.NewGuid())
        {
            ["typename"] = "MyNamespace.MyPlugin",
            ["isworkflowactivity"] = false
        };
        SetupPluginTypes(pluginTypeEntity);

        var solutionEntity = new Entity("solution")
        {
            ["pub.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", "abc")
        };
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "solution"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { solutionEntity })));

        var existingApiId = Guid.NewGuid();
        var existingApi = new Entity("customapi", existingApiId)
        {
            ["uniquename"] = "abc_MyApi",
            ["bindingtype"] = new OptionSetValue(0),
            ["boundentitylogicalname"] = null,
            ["isfunction"] = false,
            ["allowedcustomprocessingsteptype"] = new OptionSetValue(0),
            ["displayname"] = "My Api",
            ["description"] = "desc",
            ["isprivate"] = false,
            ["executeprivilegename"] = null,
            ["plugintypeid"] = pluginTypeEntity.ToEntityReference()
        };
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "customapi"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { existingApi })));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "solutioncomponent" &&
                                            HasCondition(q, "componenttype", 10066) &&
                                            HasCondition(q, "objectid", existingApiId)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity>
            {
                new("solutioncomponent") { ["sol.uniquename"] = new AliasedValue("solution", "uniquename", "Default") },
                new("solutioncomponent") { ["sol.uniquename"] = new AliasedValue("solution", "uniquename", "MySolution") }
            })));

        var customApi = new CustomApiMetadata("MyApi", "My Api", "desc", 0, null, false, false, 0, null, "MyNamespace.MyPlugin", [], []);
        var pluginTypeMetadata = new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], [customApi], false, true);
        var metadata = new PluginAssemblyMetadata("MyPlugin", "MyPlugin, Version=1.0.0.0", new byte[] { 1, 2, 3 }, "hash", "1.0.0.0", null, "neutral", [pluginTypeMetadata]);

        await _service.SyncAsync(_serviceMock, metadata, "MySolution");

        _outputMock.DidNotReceive().Info(Arg.Is<string>(s => s.Contains("Updating customapi")));
    }

    // -- Save mode: report skipped deletions --

    [Fact]
    public async Task SyncSolutionAsync_SaveMode_ReportsSkippedStepAndTypeDeletions()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));

        var obsoleteTypeId = Guid.NewGuid();
        SetupPluginTypes(new Entity("plugintype", obsoleteTypeId)
        {
            ["typename"] = "Obsolete.Plugin",
            ["isworkflowactivity"] = false
        });

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of contact", "Update", "contact", 20, 0, 1, null, null, [], []);
        var plugin = new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], null, false);

        var existingStepId = Guid.NewGuid();
        SetupSteps(new Entity("sdkmessageprocessingstep", existingStepId) { ["name"] = "Orphaned step", ["plugintypeid"] = new EntityReference("plugintype", obsoleteTypeId) });
        SetupImages();

        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessage"))
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } })));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessage"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } })));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter"))
            .Returns(Task.FromResult(new EntityCollection()));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

        await _service.SyncAsync(_serviceMock, Metadata(plugins: plugin), "MySolution", RunMode.Save);

        await _serviceMock.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _outputMock.Received().Skip(Arg.Is<string>(s => s.Contains("Orphaned step")));
        _outputMock.Received().Skip(Arg.Is<string>(s => s.Contains("Obsolete.Plugin")));
    }

    [Fact]
    public async Task SyncAsync_DeletePhaseCompletesBeforeAssemblyUpdate()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));

        var obsoleteTypeId = Guid.NewGuid();
        SetupPluginTypes(new Entity("plugintype", obsoleteTypeId)
        {
            ["typename"] = "Obsolete.Plugin",
            ["isworkflowactivity"] = false
        });

        var obsoleteStepId = Guid.NewGuid();
        SetupSteps(new Entity("sdkmessageprocessingstep", obsoleteStepId)
        {
            ["name"] = "Obsolete.Step",
            ["plugintypeid"] = new EntityReference("plugintype", obsoleteTypeId)
        });

        var callOrder = new List<string>();
        _serviceMock.DeleteAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callOrder.Add($"delete:{callInfo.Arg<string>()}");
                return Task.CompletedTask;
            });
        _serviceMock.UpdateAsync(Arg.Is<Entity>(e => e.LogicalName == "pluginassembly"), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callOrder.Add("update:pluginassembly");
                return Task.CompletedTask;
            });

        await _service.SyncAsync(_serviceMock, Metadata(hash: "newhash"), "MySolution");

        var updateIndex = callOrder.IndexOf("update:pluginassembly");
        Assert.True(updateIndex > 0);
        Assert.DoesNotContain(callOrder.Skip(updateIndex + 1), c => c.StartsWith("delete:", StringComparison.Ordinal));
    }

    // -- Identity change: delete + recreate --

    private void SetupIdentityChangeExecuteAsync()
    {
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = Guid.NewGuid();
        _serviceMock.ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<OrganizationResponse>(createResponse));
    }

    [Fact]
    public async Task SyncAsync_PktChanged_DeletesAndRecreatesAssembly()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, pkt: "df889c1cc53657b7"));
        SetupPluginTypes();
        SetupIdentityChangeExecuteAsync();

        await _service.SyncAsync(_serviceMock, Metadata(pkt: "a4d07ffa42de325f"), "MySolution");

        await _serviceMock.Received(1).DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<CreateRequest>(r => r.Target.LogicalName == "pluginassembly"), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Is<Entity>(e => e.LogicalName == "pluginassembly"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_CultureChanged_DeletesAndRecreatesAssembly()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, culture: "neutral"));
        SetupPluginTypes();
        SetupIdentityChangeExecuteAsync();

        await _service.SyncAsync(_serviceMock, Metadata(culture: "en"), "MySolution");

        await _serviceMock.Received(1).DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<CreateRequest>(r => r.Target.LogicalName == "pluginassembly"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_MajorVersionChanged_DeletesAndRecreatesAssembly()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "1.0.0.0"));
        SetupPluginTypes();
        SetupIdentityChangeExecuteAsync();

        await _service.SyncAsync(_serviceMock, Metadata(version: "2.0.0.0"), "MySolution");

        await _serviceMock.Received(1).DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<CreateRequest>(r => r.Target.LogicalName == "pluginassembly"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_MinorVersionChanged_DeletesAndRecreatesAssembly()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "1.0.0.0"));
        SetupPluginTypes();
        SetupIdentityChangeExecuteAsync();

        await _service.SyncAsync(_serviceMock, Metadata(version: "1.1.0.0"), "MySolution");

        await _serviceMock.Received(1).DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<CreateRequest>(r => r.Target.LogicalName == "pluginassembly"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_BuildVersionChanged_DoesNotDeleteAssembly()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "1.0.0.0", hash: "oldhash"));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock, Metadata(version: "1.0.5.0", hash: "newhash"), "MySolution");

        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).UpdateAsync(Arg.Is<Entity>(e => e.LogicalName == "pluginassembly"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_SaveMode_IdentityChanged_ThrowsAndDoesNotDelete()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, pkt: "df889c1cc53657b7"));
        SetupPluginTypes();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncAsync(_serviceMock, Metadata(pkt: "a4d07ffa42de325f"), "MySolution", RunMode.Save));

        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        _outputMock.Received(1).Error(Arg.Is<string>(s => s.Contains("Re-run without --save")));
    }

    [Fact]
    public async Task SyncAsync_BothPktsNull_DoesNotDeleteAssembly()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "abc123"));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock, Metadata(hash: "abc123", pkt: null), "MySolution");

        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_MultipleIdentityFieldsChanged_ReasonListsAllFields()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "1.0.0.0", pkt: "aabbccdd11223344"));
        SetupPluginTypes();
        SetupIdentityChangeExecuteAsync();

        await _service.SyncAsync(_serviceMock, Metadata(version: "2.0.0.0", pkt: "1122334455667788"), "MySolution");

        _outputMock.Received(1).Warning(Arg.Is<string>(s =>
            s.Contains("public key token") &&
            s.Contains("major/minor version")));
    }

    // -- HasMajorOrMinorVersionChange unit tests --

    [Theory]
    [InlineData(null, "1.0.0.0", false)]
    [InlineData("", "1.0.0.0", false)]
    [InlineData("not-a-version", "1.0.0.0", false)]
    [InlineData("1.0.0.0", "1.0.0.0", false)]
    [InlineData("1.0.0.0", "1.0.5.0", false)]
    [InlineData("1.0.0.0", "1.0.0.3", false)]
    [InlineData("1.0.0.0", "2.0.0.0", true)]
    [InlineData("1.0.0.0", "1.1.0.0", true)]
    [InlineData("2.3.0.0", "3.3.0.0", true)]
    [InlineData("2.3.0.0", "2.4.0.0", true)]
    public void HasMajorOrMinorVersionChange_ReturnsExpected(string? registered, string local, bool expected)
    {
        Assert.Equal(expected, PluginRegistrationService.HasMajorOrMinorVersionChange(registered, local));
    }

    [Fact]
    public async Task SyncAsync_DeletePhase_SkipsNonModifiableStageSteps()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));

        var obsoleteTypeId = Guid.NewGuid();
        SetupPluginTypes(new Entity("plugintype", obsoleteTypeId)
        {
            ["typename"] = "Obsolete.Plugin",
            ["isworkflowactivity"] = false
        });

        var protectedStepId = Guid.NewGuid();
        SetupSteps(new Entity("sdkmessageprocessingstep", protectedStepId)
        {
            ["name"] = "Protected.Step",
            ["plugintypeid"] = new EntityReference("plugintype", obsoleteTypeId),
            ["stage"] = new OptionSetValue(30)
        });

        await _service.SyncAsync(_serviceMock, Metadata(hash: "newhash"), "MySolution");

        // Stage=30 (internal) steps are excluded by the Dataverse query — never directly deleted by Flowline
        await _serviceMock.DidNotReceive().DeleteAsync("sdkmessageprocessingstep", protectedStepId, Arg.Any<CancellationToken>());
        // The plugin type itself is obsolete (not in assembly) and is correctly deleted; its stage=30 step cascades
        await _serviceMock.Received(1).DeleteAsync("plugintype", obsoleteTypeId, Arg.Any<CancellationToken>());
    }

    // -- Dry-run mode --

    [Fact]
    public async Task SyncAsync_DryRun_NewAssembly_NoCreateCalled()
    {
        SetupAssembly();
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock, Metadata(), "MySolution", RunMode.DryRun);

        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>());
        _outputMock.Received(1).Info(Arg.Is<string>(s => s.Contains("would create")));
    }

    [Fact]
    public async Task SyncAsync_DryRun_ExistingUnchanged_NoUpdateCalled()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "abc123"));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock, Metadata(hash: "abc123"), "MySolution", RunMode.DryRun);

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_DryRun_ExistingChanged_NoUpdateCalled()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock, Metadata(hash: "newhash"), "MySolution", RunMode.DryRun);

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        _outputMock.Received(1).Skip(Arg.Is<string>(s => s.Contains("would update content")));
    }

    [Fact]
    public async Task SyncAsync_DryRun_WithDeletesInPlan_NoDeleteCalled()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));

        var obsoleteTypeId = Guid.NewGuid();
        SetupPluginTypes(new Entity("plugintype", obsoleteTypeId) { ["typename"] = "Obsolete.Plugin", ["isworkflowactivity"] = false });
        SetupSteps(new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["name"] = "Obsolete.Plugin: Update of account",
            ["plugintypeid"] = new EntityReference("plugintype", obsoleteTypeId)
        });

        await _service.SyncAsync(_serviceMock, Metadata(), "MySolution", RunMode.DryRun);

        await _serviceMock.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        _outputMock.Received().Skip(Arg.Is<string>(s => s.Contains("would delete")));
    }

    [Fact]
    public async Task SyncAsync_DryRun_IdentityChanged_NoDeleteNoThrow()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, pkt: "df889c1cc53657b7"));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock, Metadata(pkt: "a4d07ffa42de325f"), "MySolution", RunMode.DryRun);

        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        _outputMock.Received(1).Skip(Arg.Is<string>(s => s.Contains("identity changed") && s.Contains("would delete and recreate")));
    }

    [Fact]
    public async Task SyncAsync_DryRun_OutputsSummaryLine()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));

        var obsoleteTypeId = Guid.NewGuid();
        SetupPluginTypes(new Entity("plugintype", obsoleteTypeId) { ["typename"] = "Obsolete.Plugin", ["isworkflowactivity"] = false });
        SetupSteps(new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["name"] = "Obsolete.Plugin: Update of account",
            ["plugintypeid"] = new EntityReference("plugintype", obsoleteTypeId)
        });

        await _service.SyncAsync(_serviceMock, Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], null, false)), "MySolution", RunMode.DryRun);

        _outputMock.Received(1).Info(Arg.Is<string>(s => s.Contains("Dry-run summary:")));
    }
}
