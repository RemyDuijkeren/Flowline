using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;
using Moq;
using Flowline.Core.Services;
using Flowline.Core.Models;
using Flowline.Core;

namespace Flowline.Core.Tests;

public class PluginRegistrationServiceTests
{
    private readonly Mock<IOrganizationServiceAsync2> _serviceMock;
    private readonly Mock<IFlowlineOutput> _outputMock;
    private readonly PluginRegistrationService _service;

    public PluginRegistrationServiceTests()
    {
        _serviceMock = new Mock<IOrganizationServiceAsync2>();
        _outputMock = new Mock<IFlowlineOutput>();
        _service = new PluginRegistrationService(_outputMock.Object);

        // Default empty results for all queries
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.IsAny<QueryExpression>()))
            .ReturnsAsync(new EntityCollection());
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.IsAny<QueryExpression>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());

        var defaultMessage = new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" };
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessage")))
            .ReturnsAsync(new EntityCollection([defaultMessage]));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessage"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection([defaultMessage]));

        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter")))
            .ReturnsAsync(new EntityCollection());
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());

        var defaultSolution = new Entity("solution")
        {
            ["pub.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", "abc")
        };
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "solution"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { defaultSolution }));

        _serviceMock.Setup(x => x.RetrieveMultipleAsync(
                It.Is<QueryExpression>(q => q.EntityName == "solutioncomponent" && q.Criteria.Conditions.Any(c => c.AttributeName == "objectid")),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((QueryExpression query, CancellationToken _) =>
            {
                var objectId = GetGuidConditionValue(query, "objectid");
                if (!objectId.HasValue)
                    return new EntityCollection();

                return new EntityCollection(new List<Entity>
                {
                    new("solutioncomponent")
                    {
                        ["componenttype"] = new OptionSetValue(ResolveComponentTypeFromObjectId(objectId.Value))
                    }
                });
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
            _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "pluginassembly")))
                .ReturnsAsync(new EntityCollection());
            _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "pluginassembly"), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EntityCollection());
            var createResponse = new CreateResponse();
            createResponse.Results["id"] = Guid.NewGuid();
            _serviceMock.Setup(x => x.ExecuteAsync(It.IsAny<CreateRequest>()))
                .ReturnsAsync(createResponse);
            _serviceMock.Setup(x => x.ExecuteAsync(It.IsAny<CreateRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(createResponse);
        }
        else
        {
            _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "pluginassembly")))
                .ReturnsAsync(new EntityCollection(new List<Entity> { existing }));
            _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "pluginassembly"), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new EntityCollection(new List<Entity> { existing }));
        }
    }

    private void SetupPluginTypes(params Entity[] types)
    {
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "plugintype")))
            .ReturnsAsync(new EntityCollection(types.ToList()));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "plugintype"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(types.ToList()));
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
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep")))
            .ReturnsAsync(new EntityCollection(queryableSteps));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(queryableSteps));
    }

    private void SetupImages(params Entity[] images)
    {
        foreach (var i in images)
        {
            if (!i.Contains("sdkmessageprocessingstepid"))
                i["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", Guid.NewGuid());
        }
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstepimage")))
            .ReturnsAsync(new EntityCollection(images.ToList()));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstepimage"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(images.ToList()));
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

        await _service.SyncAsync(_serviceMock.Object, Metadata(), "MySolution");

        _serviceMock.Verify(x => x.ExecuteAsync(It.Is<CreateRequest>(r =>
            r.Target.LogicalName == "pluginassembly" &&
            r.Target.GetAttributeValue<string>("name") == "MyPlugin" &&
            r["SolutionUniqueName"].ToString() == "MySolution"
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncSolutionAsync_ExistingAssembly_UpdatesVersion()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, "1.0.0.0"));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock.Object, Metadata(version: "1.0.0.1"), "MySolution");

        _serviceMock.Verify(x => x.UpdateAsync(It.Is<Entity>(e =>
            e.LogicalName == "pluginassembly" &&
            e.Id == assemblyId &&
            e.GetAttributeValue<string>("version") == "1.0.0.1"
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    // -- Plugin type creation --

    [Fact]
    public async Task SyncSolutionAsync_NewPluginType_CreatesPluginType()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();
        SetupSteps();

        await _service.SyncAsync(_serviceMock.Object, Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], null, false)), "MySolution");

        _serviceMock.Verify(x => x.CreateAsync(It.Is<Entity>(e =>
            e.LogicalName == "plugintype" &&
            e.GetAttributeValue<string>("typename") == "MyNamespace.MyPlugin" &&
            !e.Contains("workflowactivitygroupname")
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    // -- Workflow type creation --

    [Fact]
    public async Task SyncSolutionAsync_NewWorkflowType_SetsWorkflowActivityGroupName()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock.Object, Metadata(plugins: new PluginTypeMetadata("MyActivity", "MyNamespace.MyActivity", [], null, true)), "MySolution");

        _serviceMock.Verify(x => x.CreateAsync(It.Is<Entity>(e =>
            e.LogicalName == "plugintype" &&
            e.GetAttributeValue<string>("typename") == "MyNamespace.MyActivity" &&
            e.GetAttributeValue<string>("workflowactivitygroupname") == "MyPlugin (1.0.0.0)"
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncSolutionAsync_WorkflowType_SnapshotAlwaysQueriesSteps()
    {
        // Snapshot-based design always loads steps upfront regardless of assembly content
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock.Object, Metadata(plugins: new PluginTypeMetadata("MyActivity", "MyNamespace.MyActivity", [], null, true)), "MySolution");

        _serviceMock.Verify(x => x.RetrieveMultipleAsync(
            It.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep"),
            It.IsAny<CancellationToken>()), Times.Once);
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

        await _service.SyncAsync(_serviceMock.Object, Metadata(), "MySolution"); // no plugins in assembly

        _serviceMock.Verify(x => x.DeleteAsync("sdkmessageprocessingstep", stepId, It.IsAny<CancellationToken>()), Times.Once);
        _serviceMock.Verify(x => x.DeleteAsync("plugintype", obsoleteTypeId, It.IsAny<CancellationToken>()), Times.Once);
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

        await _service.SyncAsync(_serviceMock.Object, Metadata(), "MySolution");

        _serviceMock.Verify(x => x.DeleteAsync("plugintype", obsoleteTypeId, It.IsAny<CancellationToken>()), Times.Once);
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

        await _service.SyncAsync(_serviceMock.Object, Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], null, false)), "MySolution");

        _serviceMock.Verify(x => x.DeleteAsync("sdkmessageprocessingstep", stepId, It.IsAny<CancellationToken>()), Times.Once);
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

        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessage")))
            .ReturnsAsync(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } }));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessage"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } }));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter")))
            .ReturnsAsync(new EntityCollection());
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of contact", "Update", "contact", 20, 0, 1, null, null, [], []);

        await _service.SyncAsync(_serviceMock.Object, Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], null, false)), "MySolution");

        _serviceMock.Verify(x => x.DeleteAsync("sdkmessageprocessingstep", orphanId, It.IsAny<CancellationToken>()), Times.Once);
    }

    // -- Hash-based change detection --

    [Fact]
    public async Task SyncAsync_UnchangedAssembly_SkipsUpload()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "abc123"));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock.Object, Metadata(hash: "abc123"), "MySolution");

        _serviceMock.Verify(x => x.UpdateAsync(It.Is<Entity>(e => e.LogicalName == "pluginassembly"), It.IsAny<CancellationToken>()), Times.Never);
        _outputMock.Verify(x => x.Skip(It.Is<string>(s => s.Contains("unchanged"))), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_ChangedAssembly_UploadsNewContent()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock.Object, Metadata(hash: "newhash"), "MySolution");

        _serviceMock.Verify(x => x.UpdateAsync(It.Is<Entity>(e =>
            e.LogicalName == "pluginassembly" &&
            e.GetAttributeValue<string>("description") == "[flowline] sha256=newhash"
        ), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_ExistingAssemblyInOtherSolutions_EmitsWarningWithSolutionNames()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));
        SetupPluginTypes();

        _serviceMock.Setup(x => x.RetrieveMultipleAsync(
                It.Is<QueryExpression>(q => q.EntityName == "solutioncomponent" &&
                                            HasCondition(q, "componenttype", 91) &&
                                            HasCondition(q, "objectid", assemblyId)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>
            {
                new("solutioncomponent") { ["sol.uniquename"] = new AliasedValue("solution", "uniquename", "OtherSolutionA") },
                new("solutioncomponent") { ["sol.uniquename"] = new AliasedValue("solution", "uniquename", "MySolution") },
                new("solutioncomponent") { ["sol.uniquename"] = new AliasedValue("solution", "uniquename", "OtherSolutionB") }
            }));

        await _service.SyncAsync(_serviceMock.Object, Metadata(hash: "newhash"), "MySolution");

        _outputMock.Verify(x => x.Info(It.Is<string>(s =>
            s.Contains("Updating assembly") &&
            s.Contains("OtherSolutionA") &&
            s.Contains("OtherSolutionB") &&
            !s.Contains("MySolution"))), Times.Once);
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

        _serviceMock.Setup(x => x.RetrieveMultipleAsync(
                It.Is<QueryExpression>(q => q.EntityName == "solutioncomponent" &&
                                            HasCondition(q, "componenttype", 92) &&
                                            HasCondition(q, "objectid", existingStepId)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>
            {
                new("solutioncomponent") { ["sol.uniquename"] = new AliasedValue("solution", "uniquename", "SharedSolution") }
            }));

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of contact", "Update", "contact", 20, 0, 1, null, null, [], []);
        await _service.SyncAsync(_serviceMock.Object, Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], null, false)), "MySolution");

        _outputMock.Verify(x => x.Info(It.Is<string>(s =>
            s.Contains("Updating sdkmessageprocessingstep") &&
            s.Contains("SharedSolution"))), Times.Once);
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
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "solution"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { solutionEntity }));

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
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "customapi"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { existingApi }));

        _serviceMock.Setup(x => x.RetrieveMultipleAsync(
                It.Is<QueryExpression>(q => q.EntityName == "solutioncomponent" &&
                                            HasCondition(q, "componenttype", 10066) &&
                                            HasCondition(q, "objectid", existingApiId)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity>
            {
                new("solutioncomponent") { ["sol.uniquename"] = new AliasedValue("solution", "uniquename", "Default") },
                new("solutioncomponent") { ["sol.uniquename"] = new AliasedValue("solution", "uniquename", "MySolution") }
            }));

        var customApi = new CustomApiMetadata("MyApi", "My Api", "desc", 0, null, false, false, 0, null, "MyNamespace.MyPlugin", [], []);
        var pluginTypeMetadata = new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], [customApi], false, true);
        var metadata = new PluginAssemblyMetadata("MyPlugin", "MyPlugin, Version=1.0.0.0", new byte[] { 1, 2, 3 }, "hash", "1.0.0.0", null, "neutral", [pluginTypeMetadata]);

        await _service.SyncAsync(_serviceMock.Object, metadata, "MySolution");

        _outputMock.Verify(x => x.Info(It.Is<string>(s => s.Contains("Updating customapi"))), Times.Never);
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

        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessage")))
            .ReturnsAsync(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } }));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessage"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } }));
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter")))
            .ReturnsAsync(new EntityCollection());
        _serviceMock.Setup(x => x.RetrieveMultipleAsync(It.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EntityCollection());

        await _service.SyncAsync(_serviceMock.Object, Metadata(plugins: plugin), "MySolution", save: true);

        _serviceMock.Verify(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        _outputMock.Verify(x => x.Skip(It.Is<string>(s => s.Contains("Orphaned step"))), Times.AtLeastOnce);
        _outputMock.Verify(x => x.Skip(It.Is<string>(s => s.Contains("Obsolete.Plugin"))), Times.AtLeastOnce);
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
        _serviceMock.Setup(x => x.DeleteAsync(It.IsAny<string>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Callback<string, Guid, CancellationToken>((logicalName, _, _) => callOrder.Add($"delete:{logicalName}"))
            .Returns(Task.CompletedTask);
        _serviceMock.Setup(x => x.UpdateAsync(It.Is<Entity>(e => e.LogicalName == "pluginassembly"), It.IsAny<CancellationToken>()))
            .Callback<Entity, CancellationToken>((_, _) => callOrder.Add("update:pluginassembly"))
            .Returns(Task.CompletedTask);

        await _service.SyncAsync(_serviceMock.Object, Metadata(hash: "newhash"), "MySolution");

        var updateIndex = callOrder.IndexOf("update:pluginassembly");
        Assert.True(updateIndex > 0);
        Assert.DoesNotContain(callOrder.Skip(updateIndex + 1), c => c.StartsWith("delete:", StringComparison.Ordinal));
    }

    // -- FQN change: delete + recreate --

    private void SetupFqnChangeExecuteAsync()
    {
        var createResponse = new CreateResponse();
        createResponse.Results["id"] = Guid.NewGuid();
        _serviceMock.Setup(x => x.ExecuteAsync(It.IsAny<CreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(createResponse);
    }

    [Fact]
    public async Task SyncAsync_PktChanged_DeletesAndRecreatesAssembly()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, pkt: "df889c1cc53657b7"));
        SetupPluginTypes();
        SetupFqnChangeExecuteAsync();

        await _service.SyncAsync(_serviceMock.Object, Metadata(pkt: "a4d07ffa42de325f"), "MySolution");

        _serviceMock.Verify(x => x.DeleteAsync("pluginassembly", assemblyId, It.IsAny<CancellationToken>()), Times.Once);
        _serviceMock.Verify(x => x.ExecuteAsync(It.Is<CreateRequest>(r => r.Target.LogicalName == "pluginassembly"), It.IsAny<CancellationToken>()), Times.Once);
        _serviceMock.Verify(x => x.UpdateAsync(It.Is<Entity>(e => e.LogicalName == "pluginassembly"), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_CultureChanged_DeletesAndRecreatesAssembly()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, culture: "neutral"));
        SetupPluginTypes();
        SetupFqnChangeExecuteAsync();

        await _service.SyncAsync(_serviceMock.Object, Metadata(culture: "en"), "MySolution");

        _serviceMock.Verify(x => x.DeleteAsync("pluginassembly", assemblyId, It.IsAny<CancellationToken>()), Times.Once);
        _serviceMock.Verify(x => x.ExecuteAsync(It.Is<CreateRequest>(r => r.Target.LogicalName == "pluginassembly"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_MajorVersionChanged_DeletesAndRecreatesAssembly()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "1.0.0.0"));
        SetupPluginTypes();
        SetupFqnChangeExecuteAsync();

        await _service.SyncAsync(_serviceMock.Object, Metadata(version: "2.0.0.0"), "MySolution");

        _serviceMock.Verify(x => x.DeleteAsync("pluginassembly", assemblyId, It.IsAny<CancellationToken>()), Times.Once);
        _serviceMock.Verify(x => x.ExecuteAsync(It.Is<CreateRequest>(r => r.Target.LogicalName == "pluginassembly"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_MinorVersionChanged_DeletesAndRecreatesAssembly()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "1.0.0.0"));
        SetupPluginTypes();
        SetupFqnChangeExecuteAsync();

        await _service.SyncAsync(_serviceMock.Object, Metadata(version: "1.1.0.0"), "MySolution");

        _serviceMock.Verify(x => x.DeleteAsync("pluginassembly", assemblyId, It.IsAny<CancellationToken>()), Times.Once);
        _serviceMock.Verify(x => x.ExecuteAsync(It.Is<CreateRequest>(r => r.Target.LogicalName == "pluginassembly"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_BuildVersionChanged_DoesNotDeleteAssembly()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "1.0.0.0", hash: "oldhash"));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock.Object, Metadata(version: "1.0.5.0", hash: "newhash"), "MySolution");

        _serviceMock.Verify(x => x.DeleteAsync("pluginassembly", assemblyId, It.IsAny<CancellationToken>()), Times.Never);
        _serviceMock.Verify(x => x.UpdateAsync(It.Is<Entity>(e => e.LogicalName == "pluginassembly"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_SaveMode_FqnChanged_ThrowsAndDoesNotDelete()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, pkt: "df889c1cc53657b7"));
        SetupPluginTypes();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncAsync(_serviceMock.Object, Metadata(pkt: "a4d07ffa42de325f"), "MySolution", save: true));

        _serviceMock.Verify(x => x.DeleteAsync("pluginassembly", assemblyId, It.IsAny<CancellationToken>()), Times.Never);
        _outputMock.Verify(x => x.Info(It.Is<string>(s => s.Contains("[red]") && s.Contains("Re-run without --save"))), Times.Once);
    }

    [Fact]
    public async Task SyncAsync_BothPktsNull_DoesNotDeleteAssembly()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "abc123"));
        SetupPluginTypes();

        await _service.SyncAsync(_serviceMock.Object, Metadata(hash: "abc123", pkt: null), "MySolution");

        _serviceMock.Verify(x => x.DeleteAsync("pluginassembly", assemblyId, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SyncAsync_MultipleFqnFieldsChanged_ReasonListsAllFields()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "1.0.0.0", pkt: "aabbccdd11223344"));
        SetupPluginTypes();
        SetupFqnChangeExecuteAsync();

        await _service.SyncAsync(_serviceMock.Object, Metadata(version: "2.0.0.0", pkt: "1122334455667788"), "MySolution");

        _outputMock.Verify(x => x.Info(It.Is<string>(s =>
            s.Contains("public key token") &&
            s.Contains("major/minor version"))), Times.Once);
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

        await _service.SyncAsync(_serviceMock.Object, Metadata(hash: "newhash"), "MySolution");

        // Stage=30 (internal) steps are excluded by the Dataverse query — never directly deleted by Flowline
        _serviceMock.Verify(x => x.DeleteAsync("sdkmessageprocessingstep", protectedStepId, It.IsAny<CancellationToken>()), Times.Never);
        // The plugin type itself is obsolete (not in assembly) and is correctly deleted; its stage=30 step cascades
        _serviceMock.Verify(x => x.DeleteAsync("plugintype", obsoleteTypeId, It.IsAny<CancellationToken>()), Times.Once);
    }
}
