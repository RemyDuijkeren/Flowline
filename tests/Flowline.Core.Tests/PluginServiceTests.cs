using System.Security.Cryptography;
using Flowline;
using Flowline.Core;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests;

public class PluginServiceTests
{
    private readonly IOrganizationServiceAsync2 _serviceMock;
    private readonly TestConsole _console;
    private readonly FlowlineRuntimeOptions _runtimeOptions;
    private readonly PluginService _service;
    private readonly Guid _defaultMessageId;
    private readonly Guid _defaultFilterId;

    public PluginServiceTests()
    {
        _serviceMock = Substitute.For<IOrganizationServiceAsync2>();
        _console = new TestConsole();
        _console.Profile.Width = 400; // avoid word-wrap splitting longer assertion substrings across lines
        _runtimeOptions = new FlowlineRuntimeOptions();
        _console.Pipeline.Attach(new VerboseFilterHook(_runtimeOptions)); // matches Program.cs wiring — required for verbose-only output to be suppressed
        _service = new PluginService(_console, NullLogger<PluginService>.Instance);

        // Default empty results for all queries
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>())
            .Returns(Task.FromResult(new EntityCollection()));
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

        var defaultMessage = new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" };
        _defaultMessageId = defaultMessage.Id;
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessage"))
            .Returns(Task.FromResult(new EntityCollection([defaultMessage])));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessage"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([defaultMessage])));

        var defaultFilter = new Entity("sdkmessagefilter", Guid.NewGuid());
        _defaultFilterId = defaultFilter.Id;
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter"))
            .Returns(Task.FromResult(new EntityCollection([defaultFilter])));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([defaultFilter])));

        var defaultSolution = new Entity("solution")
        {
            ["pub.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", "abc"),
            ["publisher.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", "abc")
        };
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "solution"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { defaultSolution })));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "solutioncomponent" && q.Criteria.Conditions.Any(c => c.AttributeName == "objectid")),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var query = callInfo.Arg<QueryExpression>();
                var ids = GetAllGuidConditionValues(query, "objectid");
                var entities = ids.Select(id => SolutionComponentEntity(id, ResolveComponentTypeFromObjectId(id), "MySolution")).ToList();
                return Task.FromResult(new EntityCollection(entities));
            });
    }

    [Fact]
    public async Task SyncSolutionAsync_PatchSolution_ShouldThrowBeforeMutating()
    {
        var patchSolution = new Entity("solution", Guid.NewGuid())
        {
            ["pub.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", "abc"),
            ["publisher.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", "abc"),
            ["parentsolutionid"] = new EntityReference("solution", Guid.NewGuid())
        };
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "solution"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([patchSolution])));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution"));

        Assert.Contains("patch solution", ex.Message);
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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

    private static List<Guid> GetAllGuidConditionValues(QueryExpression query, string attribute)
    {
        var condition = query.Criteria.Conditions.FirstOrDefault(c =>
            string.Equals(c.AttributeName, attribute, StringComparison.OrdinalIgnoreCase));
        return condition?.Values.OfType<Guid>().ToList() ?? [];
    }

    private static Entity SolutionComponentEntity(Guid objectId, int componentType, string solutionName) =>
        new("solutioncomponent")
        {
            ["objectid"]       = objectId,
            ["componenttype"]  = new OptionSetValue(componentType),
            ["sol.uniquename"] = new AliasedValue("solution", "uniquename", solutionName)
        };

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
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "plugintype" && q.LinkEntities.Count == 0))
            .Returns(Task.FromResult(new EntityCollection(types.ToList())));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "plugintype" && q.LinkEntities.Count == 0), Arg.Any<CancellationToken>())
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

    private void SetupCustomApis(params Entity[] customApis)
    {
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "customapi"))
            .Returns(Task.FromResult(new EntityCollection(customApis.ToList())));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "customapi"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(customApis.ToList())));
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

        await _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution");

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

        await _service.SyncSolutionAsync(_serviceMock, Metadata(version: "1.0.0.1"), "MySolution");

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

        await _service.SyncSolutionAsync(_serviceMock, Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], [], false)), "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<CreateRequest>(r =>
            r.Target.LogicalName == "plugintype" &&
            r.Target.GetAttributeValue<string>("typename") == "MyNamespace.MyPlugin" &&
            !r.Target.Contains("workflowactivitygroupname") &&
            r["SolutionUniqueName"].ToString() == "MySolution"
        ), Arg.Any<CancellationToken>());
    }

    // -- Workflow type creation --

    [Fact]
    public async Task SyncSolutionAsync_NewWorkflowType_SetsWorkflowActivityGroupName()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(plugins: new PluginTypeMetadata("MyActivity", "MyNamespace.MyActivity", [], [], true)), "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<CreateRequest>(r =>
            r.Target.LogicalName == "plugintype" &&
            r.Target.GetAttributeValue<string>("typename") == "MyNamespace.MyActivity" &&
            r.Target.GetAttributeValue<string>("workflowactivitygroupname") == "MyPlugin (1.0.0.0)" &&
            r["SolutionUniqueName"].ToString() == "MySolution"
        ), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_WorkflowType_SnapshotAlwaysQueriesSteps()
    {
        // Snapshot-based design always loads steps upfront regardless of assembly content
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(plugins: new PluginTypeMetadata("MyActivity", "MyNamespace.MyActivity", [], [], true)), "MySolution");

        // at least once; orphan snapshot also queries steps (mock returns same assembly for all queries)
        await _serviceMock.Received().RetrieveMultipleAsync(
            Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_NonVerbose_DoesNotOutputSnapshotContents()
    {
        var assemblyId = Guid.NewGuid();
        var pluginTypeId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes(new Entity("plugintype", pluginTypeId)
        {
            ["typename"] = "MyNamespace.MyPlugin",
            ["isworkflowactivity"] = false
        });
        SetupSteps(new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["name"] = "MyNamespace.MyPlugin: Update of account",
            ["plugintypeid"] = new EntityReference("plugintype", pluginTypeId)
        });

        await _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution", RunMode.DryRun);

        Assert.DoesNotContain("Dataverse snapshot", _console.Output);
        Assert.DoesNotContain("Plugin types (1)", _console.Output);
        Assert.DoesNotContain("Summary:", _console.Output);
    }

    [Fact]
    public async Task SyncSolutionAsync_Verbose_OutputsSnapshotContentsAsHierarchy()
    {
        _runtimeOptions.IsVerbose = true;
        var assemblyId = Guid.NewGuid();
        var pluginTypeId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var customApiId = Guid.NewGuid();

        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes(new Entity("plugintype", pluginTypeId)
        {
            ["typename"] = "MyNamespace.MyPlugin",
            ["isworkflowactivity"] = false
        });
        SetupSteps(new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"] = "MyNamespace.MyPlugin: Update of account",
            ["description"] = "Existing update step",
            ["plugintypeid"] = new EntityReference("plugintype", pluginTypeId),
            ["stage"] = new OptionSetValue(20),
            ["mode"] = new OptionSetValue(0),
            ["rank"] = 1,
            ["filteringattributes"] = "name,emailaddress1"
        });
        SetupImages(new Entity("sdkmessageprocessingstepimage", Guid.NewGuid())
        {
            ["name"] = "PreImage",
            ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", stepId),
            ["entityalias"] = "pre",
            ["imagetype"] = new OptionSetValue(0),
            ["attributes"] = "name"
        });
        SetupCustomApis(new Entity("customapi", customApiId)
        {
            ["uniquename"] = "abc_MyApi",
            ["plugintypeid"] = new EntityReference("plugintype", pluginTypeId),
            ["bindingtype"] = new OptionSetValue(0),
            ["isfunction"] = false,
            ["isprivate"] = false
        });
        SetupRequestParameters(new Entity("customapirequestparameter", Guid.NewGuid())
        {
            ["uniquename"] = "abc_Input",
            ["customapiid"] = new EntityReference("customapi", customApiId),
            ["type"] = new OptionSetValue(10),
            ["isoptional"] = true,
            ["logicalentityname"] = "account"
        });
        SetupResponseProperties(new Entity("customapiresponseproperty", Guid.NewGuid())
        {
            ["uniquename"] = "abc_Output",
            ["customapiid"] = new EntityReference("customapi", customApiId),
            ["type"] = new OptionSetValue(10),
            ["logicalentityname"] = "account"
        });

        await _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution", RunMode.DryRun);

        Assert.Contains("Dataverse snapshot", _console.Output);
        Assert.Contains("Publisher prefix: abc", _console.Output);
        Assert.Contains("Plugin types (1)", _console.Output);
        Assert.Contains("MyNamespace.MyPlugin", _console.Output);
        Assert.Contains("Steps (1)", _console.Output);
        Assert.Contains("MyNamespace.MyPlugin: Update of account", _console.Output);
        Assert.Contains("Images (1)", _console.Output);
        Assert.Contains("PreImage", _console.Output);
        Assert.Contains("Custom APIs (1)", _console.Output);
        Assert.Contains("abc_MyApi", _console.Output);
        Assert.Contains("Request parameters (1)", _console.Output);
        Assert.Contains("abc_Input", _console.Output);
        Assert.Contains("Response properties (1)", _console.Output);
        Assert.Contains("abc_Output", _console.Output);
    }

    [Fact]
    public async Task SyncSolutionAsync_Verbose_OutputsPlanContentsAsHierarchy()
    {
        _runtimeOptions.IsVerbose = true;
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();

        var metadata = Metadata(plugins: new PluginTypeMetadata(
            "MyPlugin",
            "MyNamespace.MyPlugin",
            [],
            [],
            false));

        await _service.SyncSolutionAsync(_serviceMock, metadata, "MySolution", RunMode.DryRun);

        // Option A tree: type nodes are labelled by asmPluginType.Name (short name), not full name
        Assert.Contains("MyPlugin", _console.Output);
        Assert.Contains("would create", _console.Output);
    }

    [Fact]
    public async Task SyncSolutionAsync_StepWithMissingRunAsUser_ThrowsClearException()
    {
        var assemblyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();
        SetupSteps();

        var step = new PluginStepMetadata(
            "MyNamespace.MyPlugin: Update of account",
            "Update",
            "account",
            20,
            0,
            1,
            null,
            null,
            [],
            [],
            RunAs: userId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncSolutionAsync(
                _serviceMock,
                Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)),
                "MySolution"));

        Assert.Contains("RunAs", ex.Message);
        Assert.Contains(userId.ToString(), ex.Message);
        Assert.Contains("system user", ex.Message);
    }

    // -- Orphan steps from renamed/foreign plugin assemblies --

    private void SetupOrphanStepFromForeignAssembly(Guid stepId, string stepName, string foreignAssemblyName)
    {
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "solutioncomponent" && q.Criteria.Conditions.Any(c => c.AttributeName == "componenttype")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity>
            {
                new Entity("solutioncomponent")
                {
                    ["objectid"] = stepId,
                    ["step.name"] = new AliasedValue("sdkmessageprocessingstep", "name", stepName),
                    ["asm.name"] = new AliasedValue("pluginassembly", "name", foreignAssemblyName)
                }
            })));
    }

    [Fact]
    public async Task SyncSolutionAsync_OrphanStepFromForeignAssembly_WarnsWithoutForce()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();
        SetupSteps();

        var orphanStepId = Guid.NewGuid();
        SetupOrphanStepFromForeignAssembly(orphanStepId, "Extensions.MyFirst2PostUpdatePlugin: Update of account", "Extensions");

        await _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution");

        Assert.Contains("Extensions.MyFirst2PostUpdatePlugin: Update of account", _console.Output);
        Assert.Contains("Extensions.dll", _console.Output);
        Assert.Contains("--force", _console.Output);
        await _serviceMock.DidNotReceive().DeleteAsync("sdkmessageprocessingstep", orphanStepId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_OrphanStepFromForeignAssembly_WithForce_Deletes()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();
        SetupSteps();

        var orphanStepId = Guid.NewGuid();
        SetupOrphanStepFromForeignAssembly(orphanStepId, "Extensions.MyFirst2PostUpdatePlugin: Update of account", "Extensions");

        await _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution", RunMode.Normal, forceDeleteOrphans: true, forceRecreateAssembly: false);

        await _serviceMock.Received(1).DeleteAsync("sdkmessageprocessingstep", orphanStepId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_ForceRecreateAssemblyOnly_DoesNotDeleteOrphanStep()
    {
        // Proves the two hazards are independently gated: approving recreate-assembly must not
        // also silently approve delete-orphans for an unrelated orphan step in the same run.
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, pkt: "aabbccdd11223344"));
        SetupPluginTypes();
        SetupIdentityChangeExecuteAsync();

        var orphanStepId = Guid.NewGuid();
        SetupOrphanStepFromForeignAssembly(orphanStepId, "Extensions.MyFirst2PostUpdatePlugin: Update of account", "Extensions");

        await _service.SyncSolutionAsync(_serviceMock, Metadata(pkt: "1122334455667788"), "MySolution", RunMode.Normal, forceDeleteOrphans: false, forceRecreateAssembly: true);

        await _serviceMock.Received().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("sdkmessageprocessingstep", orphanStepId, Arg.Any<CancellationToken>());
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

        await _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution"); // no plugins in assembly

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

        await _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution");

        await _serviceMock.Received(1).DeleteAsync("plugintype", obsoleteTypeId, Arg.Any<CancellationToken>());
    }

    // -- DLL as source of truth: all orphaned steps deleted --

    [Fact]
    public async Task SyncSolutionAsync_PluginWithNoSteps_DeletesAllExistingSteps()
    {
        // [Step] removed to disable a plugin — Flowline deletes all steps for that type
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));

        var pluginType = new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "MyNamespace.MyPlugin", ["isworkflowactivity"] = false };
        SetupPluginTypes(pluginType);

        var stepId = Guid.NewGuid();
        var existingStep = new Entity("sdkmessageprocessingstep", stepId) { ["name"] = "Old step", ["plugintypeid"] = pluginType.ToEntityReference() };
        SetupSteps(existingStep);

        await _service.SyncSolutionAsync(_serviceMock, Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], [], false)), "MySolution");

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
            .Returns(Task.FromResult(new EntityCollection([new Entity("sdkmessagefilter", Guid.NewGuid())])));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([new Entity("sdkmessagefilter", Guid.NewGuid())])));

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of contact", "Update", "contact", 20, 0, 1, null, null, [], []);

        await _service.SyncSolutionAsync(_serviceMock, Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), "MySolution");

        await _serviceMock.Received(1).DeleteAsync("sdkmessageprocessingstep", orphanId, Arg.Any<CancellationToken>());
    }

    // -- Hash-based change detection --

    [Fact]
    public async Task SyncAsync_UnchangedAssembly_SkipsUpload()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "abc123"));
        SetupPluginTypes();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(hash: "abc123"), "MySolution");

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Is<Entity>(e => e.LogicalName == "pluginassembly"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_ChangedAssembly_UploadsNewContent()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));
        SetupPluginTypes();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(hash: "newhash"), "MySolution");

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
                Arg.Is<QueryExpression>(q => q.EntityName == "solutioncomponent" && q.Criteria.Conditions.Any(c => c.AttributeName == "objectid")),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = GetAllGuidConditionValues(callInfo.Arg<QueryExpression>(), "objectid");
                var entities = ids.SelectMany(id => id == assemblyId
                    ? (IEnumerable<Entity>)
                    [
                        SolutionComponentEntity(id, 91, "MySolution"),
                        SolutionComponentEntity(id, 91, "OtherSolutionA"),
                        SolutionComponentEntity(id, 91, "OtherSolutionB")
                    ]
                    : [SolutionComponentEntity(id, ResolveComponentTypeFromObjectId(id), "MySolution")]).ToList();
                return Task.FromResult(new EntityCollection(entities));
            });

        await _service.SyncSolutionAsync(_serviceMock, Metadata(hash: "newhash"), "MySolution");

        Assert.Contains("Updating assembly", _console.Output);
        Assert.Contains("OtherSolutionA", _console.Output);
        Assert.Contains("OtherSolutionB", _console.Output);
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
            ["plugintypeid"] = pluginType.ToEntityReference(),
            ["sdkmessageid"] = new EntityReference("sdkmessage", _defaultMessageId),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", _defaultFilterId),
            ["stage"] = new OptionSetValue(20),
            ["mode"] = new OptionSetValue(0)
        });
        SetupImages();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "solutioncomponent" && q.Criteria.Conditions.Any(c => c.AttributeName == "objectid")),
                Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ids = GetAllGuidConditionValues(callInfo.Arg<QueryExpression>(), "objectid");
                var entities = ids.SelectMany(id => id == existingStepId
                    ? (IEnumerable<Entity>)
                    [
                        SolutionComponentEntity(id, 92, "MySolution"),
                        SolutionComponentEntity(id, 92, "SharedSolution")
                    ]
                    : [SolutionComponentEntity(id, ResolveComponentTypeFromObjectId(id), "MySolution")]).ToList();
                return Task.FromResult(new EntityCollection(entities));
            });

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of contact", "Update", "contact", 20, 0, 1, null, null, [], []);
        await _service.SyncSolutionAsync(_serviceMock, Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), "MySolution");

        Assert.Contains("Updating sdkmessageprocessingstep", _console.Output);
        Assert.Contains("SharedSolution", _console.Output);
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
            ["pub.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", "abc"),
            ["publisher.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", "abc")
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

        // Default mock returns only MySolution — no cross-solution warning expected

        var customApi = new CustomApiMetadata("MyApi", "My Api", "desc", 0, null, false, false, 0, null, "MyNamespace.MyPlugin", [], []);
        var pluginTypeMetadata = new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], [customApi], false, true);
        var metadata = new PluginAssemblyMetadata("MyPlugin", "MyPlugin, Version=1.0.0.0", new byte[] { 1, 2, 3 }, "hash", "1.0.0.0", null, "neutral", [pluginTypeMetadata]);

        await _service.SyncSolutionAsync(_serviceMock, metadata, "MySolution");

        Assert.DoesNotContain("Updating customapi", _console.Output);
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
        var plugin = new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false);

        var existingStepId = Guid.NewGuid();
        SetupSteps(new Entity("sdkmessageprocessingstep", existingStepId) { ["name"] = "Orphaned step", ["plugintypeid"] = new EntityReference("plugintype", obsoleteTypeId) });
        SetupImages();

        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessage"))
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } })));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessage"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } })));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter"))
            .Returns(Task.FromResult(new EntityCollection([new Entity("sdkmessagefilter", Guid.NewGuid())])));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessagefilter"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([new Entity("sdkmessagefilter", Guid.NewGuid())])));

        await _service.SyncSolutionAsync(_serviceMock, Metadata(plugins: plugin), "MySolution", RunMode.NoDelete);

        await _serviceMock.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        Assert.Contains("Orphaned step", _console.Output);
        Assert.Contains("Obsolete.Plugin", _console.Output);
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

        await _service.SyncSolutionAsync(_serviceMock, Metadata(hash: "newhash"), "MySolution");

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

        await _service.SyncSolutionAsync(_serviceMock, Metadata(pkt: "a4d07ffa42de325f"), "MySolution", forceRecreateAssembly: true);

        // The mock returns the existing assembly for ALL pluginassembly queries (including the orphan check),
        // so DeleteAsync may be called more than once — verify at least the identity-change delete happened
        await _serviceMock.Received().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
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

        await _service.SyncSolutionAsync(_serviceMock, Metadata(culture: "en"), "MySolution", forceRecreateAssembly: true);

        await _serviceMock.Received().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<CreateRequest>(r => r.Target.LogicalName == "pluginassembly"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_MajorVersionChanged_DeletesAndRecreatesAssembly()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "1.0.0.0"));
        SetupPluginTypes();
        SetupIdentityChangeExecuteAsync();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(version: "2.0.0.0"), "MySolution", forceRecreateAssembly: true);

        await _serviceMock.Received().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<CreateRequest>(r => r.Target.LogicalName == "pluginassembly"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_MinorVersionChanged_DeletesAndRecreatesAssembly()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "1.0.0.0"));
        SetupPluginTypes();
        SetupIdentityChangeExecuteAsync();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(version: "1.1.0.0"), "MySolution", forceRecreateAssembly: true);

        await _serviceMock.Received().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<CreateRequest>(r => r.Target.LogicalName == "pluginassembly"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_MajorVersionChanged_NoForce_ThrowsFlowlineException()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "1.0.0.0"));
        SetupPluginTypes();

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionAsync(_serviceMock, Metadata(version: "2.0.0.0"), "MySolution", RunMode.Normal));

        Assert.Equal(ExitCode.ForceRequired, ex.ExitCode);
        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        Assert.Contains("--force", _console.Output);
    }

    [Fact]
    public async Task SyncAsync_BuildVersionChanged_DoesNotDeleteAssembly()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "1.0.0.0", hash: "oldhash"));
        SetupPluginTypes();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(version: "1.0.5.0", hash: "newhash"), "MySolution");

        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).UpdateAsync(Arg.Is<Entity>(e => e.LogicalName == "pluginassembly"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_NoDeleteMode_IdentityChanged_ThrowsAndDoesNotDelete()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, pkt: "df889c1cc53657b7"));
        SetupPluginTypes();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncSolutionAsync(_serviceMock, Metadata(pkt: "a4d07ffa42de325f"), "MySolution", RunMode.NoDelete));

        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        Assert.Contains("--no-delete", _console.Output);
    }

    [Fact]
    public async Task SyncAsync_BothPktsNull_DoesNotDeleteAssembly()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "abc123"));
        SetupPluginTypes();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(hash: "abc123", pkt: null), "MySolution");

        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_MultipleIdentityFieldsChanged_ReasonListsAllFields()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "1.0.0.0", pkt: "aabbccdd11223344"));
        SetupPluginTypes();
        SetupIdentityChangeExecuteAsync();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(version: "2.0.0.0", pkt: "1122334455667788"), "MySolution", forceRecreateAssembly: true);

        Assert.Contains("public key token", _console.Output);
        Assert.Contains("major/minor version", _console.Output);
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
        Assert.Equal(expected, PluginService.HasMajorOrMinorVersionChange(registered, local));
    }

    // -- Version downgrade blocking --

    [Fact]
    public async Task SyncAsync_VersionDowngrade_NoForce_ThrowsFlowlineException()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "3.4.0.0"));
        SetupPluginTypes();

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionAsync(_serviceMock, Metadata(version: "1.0.0.0"), "MySolution", RunMode.Normal));

        Assert.Equal(ExitCode.ForceRequired, ex.ExitCode);
        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        Assert.Contains("--force", _console.Output);
    }

    [Fact]
    public async Task SyncAsync_VersionDowngrade_WithForce_DeletesAndRecreates()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "3.4.0.0"));
        SetupPluginTypes();
        SetupIdentityChangeExecuteAsync();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(version: "1.0.0.0"), "MySolution", RunMode.Normal, forceRecreateAssembly: true);

        // The mock returns the existing assembly for ALL pluginassembly queries (including the orphan check),
        // so DeleteAsync may be called more than once — verify at least the identity-change delete happened
        await _serviceMock.Received().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<CreateRequest>(r => r.Target.LogicalName == "pluginassembly"), Arg.Any<CancellationToken>());
        Assert.Contains("version downgrade", _console.Output);
        Assert.Contains("recreated", _console.Output);
    }

    [Fact]
    public async Task SyncAsync_DryRun_VersionDowngrade_ShowsBlockedNote()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "3.4.0.0"));
        SetupPluginTypes();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(version: "1.0.0.0"), "MySolution", RunMode.DryRun);

        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        Assert.Contains("would be blocked without --force", _console.Output);
        Assert.Contains("would delete and recreate", _console.Output);
    }

    [Fact]
    public async Task SyncAsync_VersionUpgrade_NoForce_ThrowsFlowlineException()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "1.0.0.0"));
        SetupPluginTypes();

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionAsync(_serviceMock, Metadata(version: "3.4.0.0"), "MySolution", RunMode.Normal));

        Assert.Equal(ExitCode.ForceRequired, ex.ExitCode);
        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_VersionUpgrade_WithForce_DeletesAndRecreates()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "1.0.0.0"));
        SetupPluginTypes();
        SetupIdentityChangeExecuteAsync();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(version: "3.4.0.0"), "MySolution", RunMode.Normal, forceRecreateAssembly: true);

        await _serviceMock.Received().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_DryRun_VersionUpgrade_ShowsBlockedNote()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "1.0.0.0"));
        SetupPluginTypes();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(version: "3.4.0.0"), "MySolution", RunMode.DryRun);

        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        Assert.Contains("would be blocked without --force", _console.Output);
        Assert.Contains("would delete and recreate", _console.Output);
    }

    [Fact]
    public async Task SyncAsync_DryRun_IdentityChanged_ShowsCascadeItems()
    {
        var assemblyId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, pkt: "aabbccdd11223344"));
        SetupPluginTypes(new Entity("plugintype", typeId) { ["typename"] = "MyPlugin.Handler", ["isworkflowactivity"] = false });
        SetupSteps(new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["name"] = "MyPlugin.Handler: Create of contact",
            ["plugintypeid"] = new EntityReference("plugintype", typeId)
        });

        await _service.SyncSolutionAsync(_serviceMock, Metadata(pkt: "1122334455667788"), "MySolution", RunMode.DryRun);

        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        Assert.Contains("would delete (cascade)", _console.Output);
        Assert.Contains("MyPlugin.Handler", _console.Output);
        Assert.Contains("MyPlugin.Handler: Create of contact", _console.Output);
    }

    [Fact]
    public async Task SyncAsync_Normal_IdentityChanged_ShowsCascadeItems()
    {
        var assemblyId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, pkt: "aabbccdd11223344"));
        SetupPluginTypes(new Entity("plugintype", typeId) { ["typename"] = "MyPlugin.Handler", ["isworkflowactivity"] = false });
        SetupSteps(new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["name"] = "MyPlugin.Handler: Create of contact",
            ["plugintypeid"] = new EntityReference("plugintype", typeId)
        });
        SetupIdentityChangeExecuteAsync();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(pkt: "1122334455667788"), "MySolution", RunMode.Normal, forceRecreateAssembly: true);

        Assert.Contains("cascade delete", _console.Output);
        Assert.Contains("MyPlugin.Handler", _console.Output);
    }

    [Fact]
    public async Task SyncAsync_DryRun_IdentityChanged_CascadeCountIncludedInSummary()
    {
        var assemblyId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, pkt: "aabbccdd11223344"));
        SetupPluginTypes(new Entity("plugintype", typeId) { ["typename"] = "MyPlugin.Handler", ["isworkflowactivity"] = false });
        SetupSteps(new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["name"] = "MyPlugin.Handler: Create of contact",
            ["plugintypeid"] = new EntityReference("plugintype", typeId)
        });

        await _service.SyncSolutionAsync(_serviceMock, Metadata(pkt: "1122334455667788"), "MySolution", RunMode.DryRun);

        // The mock returns identical types/steps for both the cascade snapshot (old assembly)
        // and the planning snapshot (fake new assembly), so the delete count is doubled vs production.
        // In production, the fake-entity snapshot is empty and only cascadeDeleteCount contributes.
        // Just verify that the summary line contains a non-zero delete count.
        Assert.Contains("delete(s)", _console.Output);
        Assert.DoesNotContain("0 delete(s)", _console.Output);
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

        await _service.SyncSolutionAsync(_serviceMock, Metadata(hash: "newhash"), "MySolution");

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

        await _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution", RunMode.DryRun);

        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>());
        Assert.Contains("would create", _console.Output);
    }

    [Fact]
    public async Task SyncAsync_DryRun_ExistingUnchanged_NoUpdateCalled()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "abc123"));
        SetupPluginTypes();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(hash: "abc123"), "MySolution", RunMode.DryRun);

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_DryRun_ExistingChanged_NoUpdateCalled()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));
        SetupPluginTypes();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(hash: "newhash"), "MySolution", RunMode.DryRun);

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        Assert.Contains("would update content", _console.Output);
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

        await _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution", RunMode.DryRun);

        await _serviceMock.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        Assert.Contains("would delete", _console.Output);
    }

    [Fact]
    public async Task SyncAsync_DryRun_IdentityChanged_NoDeleteNoThrow()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, pkt: "df889c1cc53657b7"));
        SetupPluginTypes();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(pkt: "a4d07ffa42de325f"), "MySolution", RunMode.DryRun);

        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        Assert.Contains("identity changed", _console.Output);
        Assert.Contains("would delete and recreate", _console.Output);
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

        await _service.SyncSolutionAsync(_serviceMock, Metadata(plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], [], false)), "MySolution", RunMode.DryRun);

        Assert.Contains("Dry run:", _console.Output);
    }

    // -- SyncAssemblyOnlyAsync --

    [Fact]
    public async Task SyncAssemblyOnlyAsync_AssemblyNotFound_Throws()
    {
        SetupAssembly(); // no existing assembly

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncAssemblyOnlyAsync(_serviceMock, Metadata(), "MySolution"));

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAssemblyOnlyAsync_HashUnchanged_Skips()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "abc123"));

        await _service.SyncAssemblyOnlyAsync(_serviceMock, Metadata(hash: "abc123"), "MySolution");

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        Assert.Contains("already up to date", _console.Output);
    }

    [Fact]
    public async Task SyncAssemblyOnlyAsync_HashChanged_UpdatesContent()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));

        await _service.SyncAssemblyOnlyAsync(_serviceMock, Metadata(hash: "newhash"), "MySolution");

        await _serviceMock.Received(1).UpdateAsync(Arg.Is<Entity>(e =>
            e.LogicalName == "pluginassembly" &&
            e.Id == assemblyId &&
            e.GetAttributeValue<string>("description") == "[flowline] sha256=newhash"
        ), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAssemblyOnlyAsync_IdentityChanged_Throws()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, pkt: "df889c1cc53657b7"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncAssemblyOnlyAsync(_serviceMock, Metadata(pkt: "a4d07ffa42de325f"), "MySolution"));

        Assert.Contains("identity changed", ex.Message);
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAssemblyOnlyAsync_DryRun_HashChanged_NoUpdateCalled()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));

        await _service.SyncAssemblyOnlyAsync(_serviceMock, Metadata(hash: "newhash"), "MySolution", RunMode.DryRun);

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        Assert.Contains("would update content", _console.Output);
        Assert.Contains("Dry run:", _console.Output);
    }

    [Fact]
    public async Task SyncAssemblyOnlyAsync_DoesNotQueryStepsOrImages()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));

        await _service.SyncAssemblyOnlyAsync(_serviceMock, Metadata(hash: "newhash"), "MySolution");

        await _serviceMock.DidNotReceive().RetrieveMultipleAsync(
            Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep"), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().RetrieveMultipleAsync(
            Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstepimage"), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().RetrieveMultipleAsync(
            Arg.Is<QueryExpression>(q => q.EntityName == "customapi"), Arg.Any<CancellationToken>());
    }

    // -- SyncSolutionFromPackageAsync (pluginpackage / NuGet path) --

    private static readonly byte[] NupkgBytes = [1, 2, 3, 4, 5];
    private static string NupkgHash => Convert.ToHexString(SHA256.HashData(NupkgBytes));

    private static List<PluginAssemblyMetadata> PackageAssemblies(string name = "MyPlugin", string version = "1.0.0.0") =>
        [new(name, $"{name}, Version={version}", new byte[] { 9, 9, 9 }, "dll-hash-unused", version, null, "neutral", [])];

    private static Entity PackageOwnedAssembly(Guid id, string? hash = null, string version = "1.0.0.0")
    {
        var e = new Entity("pluginassembly", id);
        e["name"] = "MyPlugin";
        e["version"] = version;
        e["packageid"] = new EntityReference("pluginpackage", Guid.NewGuid());
        if (hash != null)
            e["description"] = $"[flowline] sha256={hash}";
        return e;
    }

    private static Entity ClassicAssemblyNoPackage(Guid id)
    {
        var e = new Entity("pluginassembly", id);
        e["name"] = "MyPlugin";
        e["version"] = "1.0.0.0";
        return e;
    }

    private static Entity ExistingPluginPackage(Guid id, string uniqueName = "abc_MyPlugin", string version = "1.0.0.0")
    {
        var e = new Entity("pluginpackage", id);
        e["name"] = uniqueName;
        e["uniquename"] = uniqueName;
        e["version"] = version;
        return e;
    }

    private void SetupPluginPackage(Entity? existing = null)
    {
        var col = existing == null ? new EntityCollection() : new EntityCollection(new List<Entity> { existing });
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "pluginpackage"))
            .Returns(Task.FromResult(col));
        _serviceMock.RetrieveMultipleAsync(Arg.Is<QueryExpression>(q => q.EntityName == "pluginpackage"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(col));
    }

    // U6: FindPackageAssemblyAsync's query (used by LoadPackageSnapshotsAsync) is scoped by BOTH
    // packageid and name, unlike the top-level R9 detect-and-block query (name only) that SetupAssembly
    // configures. Registered after SetupAssembly() so its more specific match wins for any query that
    // carries a packageid condition — NSubstitute uses the most-recently-configured matching return.
    private void SetupPackageAssemblyByName(Guid assemblyId, string assemblyName, string version = "1.0.0.0")
    {
        var entity = new Entity("pluginassembly", assemblyId) { ["name"] = assemblyName, ["version"] = version };
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "pluginassembly"
                    && q.Criteria.Conditions.Any(c => c.AttributeName == "packageid")
                    && HasCondition(q, "name", assemblyName)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { entity })));
    }

    // Convenience wrapper for the common single-assembly package case: the same assembly the create
    // call registers is "found" by the post-create/post-update existence check (R6), rather than the
    // empty-by-default result SetupAssembly() configured for the earlier detect-and-block check.
    private void SetupPackageAssemblyFoundAfterCreate(string assemblyName, string version = "1.0.0.0") =>
        SetupPackageAssemblyByName(Guid.NewGuid(), assemblyName, version);

    // GetRegisteredPluginTypesAsync/GetRegisteredStepsAsync scope by pluginassemblyid — top-level
    // condition for plugin types, LinkEntity criteria for steps (joined through plugintype). These
    // per-assembly-scoped variants mirror PluginReaderTests' helpers so a multi-assembly package test
    // never lets one assembly's mocked types/steps leak into another's snapshot (KTD15).
    private void SetupPluginTypesForAssembly(Guid assemblyId, params Entity[] types)
    {
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "plugintype" && q.LinkEntities.Count == 0 && HasCondition(q, "pluginassemblyid", assemblyId)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(types.ToList())));
    }

    private static bool HasLinkCondition(QueryExpression query, string attributeName, object value) =>
        query.LinkEntities.Any(le => le.LinkCriteria.Conditions.Any(c =>
            string.Equals(c.AttributeName, attributeName, StringComparison.OrdinalIgnoreCase) &&
            c.Values.Count > 0 && Equals(c.Values[0], value)));

    private void SetupStepsForAssembly(Guid assemblyId, params Entity[] steps)
    {
        foreach (var s in steps)
        {
            if (!s.Contains("stage"))
                s["stage"] = new OptionSetValue(20);
        }
        var queryableSteps = steps.Where(s => s.GetAttributeValue<OptionSetValue>("stage")?.Value != 30).ToList();
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep" && HasLinkCondition(q, "pluginassemblyid", assemblyId)),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(queryableSteps)));
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_NoExistingPackage_CreatesWithPrefixedNameAndNuspecVersion()
    {
        SetupAssembly(); // no existing pluginassembly -> no classic conflict; also wires the CreateResponse mock
        SetupPackageAssemblyFoundAfterCreate("MyPlugin", "2.3.1.0"); // R6: found by the post-create existence check
        SetupPluginPackage(); // no existing package

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, PackageAssemblies(version: "2.3.1.0"), NupkgBytes, "C:/pkg/MyPlugin.nupkg", "MyPlugin", "MySolution");

        Assert.True(result);
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is<CreateRequest>(r =>
            r.Target.LogicalName == "pluginpackage" &&
            r.Target.GetAttributeValue<string>("name") == "abc_MyPlugin" &&
            r.Target.GetAttributeValue<string>("uniquename") == "abc_MyPlugin" &&
            r.Target.GetAttributeValue<string>("version") == "2.3.1.0" &&
            r.Target.Contains("content") &&
            r["SolutionUniqueName"].ToString() == "MySolution"
        ), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_DryRun_NewPackage_DoesNotCreate()
    {
        SetupAssembly();
        SetupPluginPackage();

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution", RunMode.DryRun);

        Assert.True(result);
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>());
        Assert.Contains("would create", _console.Output);
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_DryRun_ExistingPackageChanged_DoesNotUpdate()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId, hash: "stalehash"));
        SetupPluginPackage(ExistingPluginPackage(Guid.NewGuid()));

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution", RunMode.DryRun);

        Assert.True(result);
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        Assert.Contains("would update content", _console.Output);
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_ExistingPackageStaleHash_UpdatesContentOnlyOmitsVersion()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId, hash: "stalehash"));
        var packageId = Guid.NewGuid();
        SetupPluginPackage(ExistingPluginPackage(packageId));

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        Assert.True(result);
        await _serviceMock.Received(1).UpdateAsync(Arg.Is<Entity>(e =>
            e.LogicalName == "pluginpackage" &&
            e.Id == packageId &&
            e.Contains("content") &&
            !e.Contains("version")
        ), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_ExistingPackageMatchingHash_SkipsUpdateEntirely()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId, hash: NupkgHash));
        SetupPluginPackage(ExistingPluginPackage(Guid.NewGuid()));

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        Assert.False(result);
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_ExistingClassicAssembly_ThrowsBeforeAnyDataverseWrite()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ClassicAssemblyNoPackage(assemblyId));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncSolutionFromPackageAsync(_serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution"));

        Assert.Contains("MyPlugin", ex.Message);
        Assert.Contains("classic", ex.Message, StringComparison.OrdinalIgnoreCase);
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_ExistingPackageOwnedAssembly_IsNotAConflict_ProceedsAsUpdate()
    {
        // packageid populated -> already package-owned from a prior push, not a classic conflict (R9 edge case)
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId, hash: "oldhash"));
        var packageId = Guid.NewGuid();
        SetupPluginPackage(ExistingPluginPackage(packageId));

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        Assert.True(result);
        await _serviceMock.Received(1).UpdateAsync(
            Arg.Is<Entity>(e => e.LogicalName == "pluginpackage" && e.Id == packageId), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_NoPluginBearingAssemblies_ThrowsBeforeAnyDataverseCall()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncSolutionFromPackageAsync(_serviceMock, new List<PluginAssemblyMetadata>(), NupkgBytes, "empty.nupkg", "MyPlugin", "MySolution"));

        Assert.Contains("empty.nupkg", ex.Message);
        await _serviceMock.DidNotReceive().RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
    }

    // -- U6: end-to-end orchestration and ordering (KD4/KTD13, R11/KTD16) --

    [Fact]
    public async Task SyncSolutionFromPackageAsync_NewPackage_FullFlow_ChecksMarkerAndRegistersSteps()
    {
        // F1: new package, no existing steps — full create -> check -> marker-write -> register-all-steps.
        SetupAssembly(); // no existing pluginassembly -> no classic conflict; wires the CreateResponse mock
        SetupPackageAssemblyFoundAfterCreate("MyPlugin", "2.3.1.0");
        SetupPluginPackage(); // no existing package
        SetupPluginTypes(); // no existing plugin types
        SetupSteps(); // no existing steps

        var plugin = new PluginTypeMetadata("MyPluginType", "Ns.MyPluginType",
            [new PluginStepMetadata("Ns.MyPluginType: Update of account", "Update", "account", 20, 0, 1, null, null, [], [])],
            []);
        var assemblies = new List<PluginAssemblyMetadata>
        {
            new("MyPlugin", "MyPlugin, Version=2.3.1.0", new byte[] { 9, 9, 9 }, "dll-hash-unused", "2.3.1.0", null, "neutral", [plugin])
        };

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, assemblies, NupkgBytes, "C:/pkg/MyPlugin.nupkg", "MyPlugin", "MySolution");

        Assert.True(result);

        Received.InOrder(() =>
        {
            _serviceMock.ExecuteAsync(Arg.Is<CreateRequest>(r => r.Target.LogicalName == "pluginpackage"), Arg.Any<CancellationToken>());
            _serviceMock.UpdateAsync(Arg.Is<Entity>(e =>
                e.LogicalName == "pluginassembly" &&
                (e.GetAttributeValue<string>("description") ?? "").Contains("sha256=")),
                Arg.Any<CancellationToken>());
            _serviceMock.ExecuteAsync(Arg.Is<CreateRequest>(r => r.Target.LogicalName == "plugintype"), Arg.Any<CancellationToken>());
            _serviceMock.ExecuteAsync(Arg.Is<CreateRequest>(r => r.Target.LogicalName == "sdkmessageprocessingstep"), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_RemovedClass_StepsDeletedBeforeUpdate_SurvivingTypeUpsertedAfter()
    {
        // F2: existing package, a step's plugin type is being removed — steps deleted first, package
        // content updated second, no type-delete call ever issued, surviving-type steps upserted last.
        var assemblyId = Guid.NewGuid();
        var removedTypeId = Guid.NewGuid();
        var removedStepId = Guid.NewGuid();

        SetupAssembly(PackageOwnedAssembly(assemblyId, hash: "stalehash"));
        var packageId = Guid.NewGuid();
        SetupPluginPackage(ExistingPluginPackage(packageId));
        SetupPluginTypes(new Entity("plugintype", removedTypeId) { ["typename"] = "Ns.Removed" });
        SetupSteps(new Entity("sdkmessageprocessingstep", removedStepId)
        {
            ["name"] = "Ns.Removed: Update of account",
            ["plugintypeid"] = new EntityReference("plugintype", removedTypeId),
            ["stage"] = new OptionSetValue(20)
        });

        var survivingType = new PluginTypeMetadata("Surviving", "Ns.Surviving",
            [new PluginStepMetadata("Ns.Surviving: Update of account", "Update", "account", 20, 0, 1, null, null, [], [])],
            []);
        var assemblies = new List<PluginAssemblyMetadata>
        {
            new("MyPlugin", "MyPlugin, Version=1.0.0.1", new byte[] { 9, 9, 9 }, "dll-hash-unused", "1.0.0.1", null, "neutral", [survivingType])
        };

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, assemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        Assert.True(result);

        // KD4: the removed type's step is deleted, but the type record itself is never targeted —
        // Dataverse's package sync removes it automatically once the content update lands.
        await _serviceMock.Received(1).DeleteAsync("sdkmessageprocessingstep", removedStepId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("plugintype", Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        Received.InOrder(() =>
        {
            _serviceMock.DeleteAsync("sdkmessageprocessingstep", removedStepId, Arg.Any<CancellationToken>());
            _serviceMock.UpdateAsync(Arg.Is<Entity>(e => e.LogicalName == "pluginpackage" && e.Id == packageId), Arg.Any<CancellationToken>());
            _serviceMock.ExecuteAsync(Arg.Is<CreateRequest>(r =>
                r.Target.LogicalName == "plugintype" && r.Target.GetAttributeValue<string>("typename") == "Ns.Surviving"), Arg.Any<CancellationToken>());
            _serviceMock.ExecuteAsync(Arg.Is<CreateRequest>(r => r.Target.LogicalName == "sdkmessageprocessingstep"), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_TwoAssemblies_OnlyAffectedAssemblyStepsDeletedAndRecreated()
    {
        // Integration (KD5/KTD15): two assemblies, only one has a removed class — only its steps are
        // deleted-then-recreated; the unaffected assembly's identical, unmatched step is left alone.
        var packageId = Guid.NewGuid();
        var primaryAssemblyId = Guid.NewGuid();
        var secondaryAssemblyId = Guid.NewGuid();
        var primaryTypeId = Guid.NewGuid();
        var primaryStepId = Guid.NewGuid();
        var secondaryTypeId = Guid.NewGuid();
        var secondaryStepId = Guid.NewGuid();

        var primaryEntity = new Entity("pluginassembly", primaryAssemblyId)
        {
            ["name"] = "Primary",
            ["version"] = "1.0.0.0",
            ["packageid"] = new EntityReference("pluginpackage", packageId),
            ["description"] = "[flowline] sha256=oldhash"
        };

        // Top-level detect-and-block/hash-compare query — name only, no packageid condition.
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "pluginassembly"
                    && !q.Criteria.Conditions.Any(c => c.AttributeName == "packageid")
                    && HasCondition(q, "name", "Primary")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { primaryEntity })));

        SetupPackageAssemblyByName(primaryAssemblyId, "Primary");
        SetupPackageAssemblyByName(secondaryAssemblyId, "Secondary");
        SetupPluginPackage(ExistingPluginPackage(packageId));

        SetupPluginTypesForAssembly(primaryAssemblyId, new Entity("plugintype", primaryTypeId) { ["typename"] = "Ns.PrimaryOldType" });
        SetupStepsForAssembly(primaryAssemblyId, new Entity("sdkmessageprocessingstep", primaryStepId)
        {
            ["name"] = "Ns.PrimaryOldType: Update of account",
            ["plugintypeid"] = new EntityReference("plugintype", primaryTypeId),
            ["stage"] = new OptionSetValue(20)
        });

        SetupPluginTypesForAssembly(secondaryAssemblyId, new Entity("plugintype", secondaryTypeId) { ["typename"] = "Ns.SecondaryType" });
        SetupStepsForAssembly(secondaryAssemblyId, new Entity("sdkmessageprocessingstep", secondaryStepId)
        {
            ["name"] = "Ns.SecondaryType: Update of contact",
            ["plugintypeid"] = new EntityReference("plugintype", secondaryTypeId),
            ["sdkmessageid"] = new EntityReference("sdkmessage", _defaultMessageId),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", _defaultFilterId),
            ["stage"] = new OptionSetValue(20),
            ["mode"] = new OptionSetValue(0),
            ["rank"] = 1
        });

        // Primary's new metadata declares zero plugin classes — its previously-registered type is now removed.
        var primaryMetadata = new PluginAssemblyMetadata("Primary", "Primary, Version=1.0.0.1",
            new byte[] { 9, 9, 9 }, "dll-hash-unused", "1.0.0.1", null, "neutral", []);

        // Secondary declares the exact same type/step it already has registered — no drift at all.
        var secondaryPlugin = new PluginTypeMetadata("SecondaryType", "Ns.SecondaryType",
            [new PluginStepMetadata("Ns.SecondaryType: Update of contact", "Update", "contact", 20, 0, 1, null, null, [], [])],
            []);
        var secondaryMetadata = new PluginAssemblyMetadata("Secondary", "Secondary, Version=1.0.0.0",
            new byte[] { 8, 8, 8 }, "dll-hash-unused", "1.0.0.0", null, "neutral", [secondaryPlugin]);

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, [primaryMetadata, secondaryMetadata], NupkgBytes, "pkg.nupkg", "Primary", "MySolution");

        Assert.True(result);

        await _serviceMock.Received(1).DeleteAsync("sdkmessageprocessingstep", primaryStepId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("sdkmessageprocessingstep", secondaryStepId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("plugintype", Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_TwoAssemblies_UnchangedHash_NoDeletesForEitherAssembly()
    {
        // Regression guard (AE12, R11/KTD16): a no-op push on an existing two-assembly package must not
        // flag or delete either assembly's steps as orphaned just because the other assembly exists.
        var packageId = Guid.NewGuid();
        var assemblyAId = Guid.NewGuid();
        var assemblyBId = Guid.NewGuid();
        var typeAId = Guid.NewGuid();
        var typeBId = Guid.NewGuid();
        var stepAId = Guid.NewGuid();
        var stepBId = Guid.NewGuid();

        var assemblyAEntity = new Entity("pluginassembly", assemblyAId)
        {
            ["name"] = "AssemblyA",
            ["version"] = "1.0.0.0",
            ["packageid"] = new EntityReference("pluginpackage", packageId),
            ["description"] = $"[flowline] sha256={NupkgHash}"
        };

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is<QueryExpression>(q => q.EntityName == "pluginassembly"
                    && !q.Criteria.Conditions.Any(c => c.AttributeName == "packageid")
                    && HasCondition(q, "name", "AssemblyA")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { assemblyAEntity })));

        SetupPackageAssemblyByName(assemblyAId, "AssemblyA");
        SetupPackageAssemblyByName(assemblyBId, "AssemblyB");
        SetupPluginPackage(ExistingPluginPackage(packageId));

        SetupPluginTypesForAssembly(assemblyAId, new Entity("plugintype", typeAId) { ["typename"] = "Ns.TypeA" });
        SetupStepsForAssembly(assemblyAId, new Entity("sdkmessageprocessingstep", stepAId)
        {
            ["name"] = "Ns.TypeA: Update of contact",
            ["plugintypeid"] = new EntityReference("plugintype", typeAId),
            ["sdkmessageid"] = new EntityReference("sdkmessage", _defaultMessageId),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", _defaultFilterId),
            ["stage"] = new OptionSetValue(20),
            ["mode"] = new OptionSetValue(0),
            ["rank"] = 1
        });

        SetupPluginTypesForAssembly(assemblyBId, new Entity("plugintype", typeBId) { ["typename"] = "Ns.TypeB" });
        SetupStepsForAssembly(assemblyBId, new Entity("sdkmessageprocessingstep", stepBId)
        {
            ["name"] = "Ns.TypeB: Update of account",
            ["plugintypeid"] = new EntityReference("plugintype", typeBId),
            ["sdkmessageid"] = new EntityReference("sdkmessage", _defaultMessageId),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", _defaultFilterId),
            ["stage"] = new OptionSetValue(20),
            ["mode"] = new OptionSetValue(0),
            ["rank"] = 1
        });

        var pluginA = new PluginTypeMetadata("TypeA", "Ns.TypeA",
            [new PluginStepMetadata("Ns.TypeA: Update of contact", "Update", "contact", 20, 0, 1, null, null, [], [])], []);
        var pluginB = new PluginTypeMetadata("TypeB", "Ns.TypeB",
            [new PluginStepMetadata("Ns.TypeB: Update of account", "Update", "account", 20, 0, 1, null, null, [], [])], []);

        var metadataA = new PluginAssemblyMetadata("AssemblyA", "AssemblyA, Version=1.0.0.0", new byte[] { 9, 9, 9 }, "dll-hash-unused", "1.0.0.0", null, "neutral", [pluginA]);
        var metadataB = new PluginAssemblyMetadata("AssemblyB", "AssemblyB, Version=1.0.0.0", new byte[] { 8, 8, 8 }, "dll-hash-unused", "1.0.0.0", null, "neutral", [pluginB]);

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, [metadataA, metadataB], NupkgBytes, "pkg.nupkg", "AssemblyA", "MySolution");

        Assert.False(result);
        await _serviceMock.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Is<Entity>(e => e.LogicalName == "pluginpackage"), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>());
    }

    // -- WritePackageAssemblyMarkerAsync (standalone marker write, part of R6) --

    [Fact]
    public async Task WritePackageAssemblyMarkerAsync_UpdatesDescriptionAndIncludesVersionInSameCall()
    {
        var assemblyId = Guid.NewGuid();
        var assembly = new Entity("pluginassembly", assemblyId) { ["version"] = "1.2.3.4" };

        await _service.WritePackageAssemblyMarkerAsync(_serviceMock, assembly, "newhash123");

        await _serviceMock.Received(1).UpdateAsync(Arg.Is<Entity>(e =>
            e.LogicalName == "pluginassembly" &&
            e.Id == assemblyId &&
            e.GetAttributeValue<string>("description") == "[flowline] sha256=newhash123" &&
            e.GetAttributeValue<string>("version") == "1.2.3.4" &&
            !e.Contains("content")
        ), Arg.Any<CancellationToken>());
    }
}
