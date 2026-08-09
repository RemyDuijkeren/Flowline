using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Flowline;
using Flowline.Attributes;
using Flowline.Core;
using Flowline.Core.Console;
using Flowline.Core.Models;
using Flowline.Core.Services;
using Flowline.Core.Plugins;
using FluentAssertions;
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
        _service = new PluginService(_console);

        // Default empty results for all queries
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>())
            .Returns(Task.FromResult(new EntityCollection()));
        _serviceMock.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

        var defaultMessage = new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" };
        _defaultMessageId = defaultMessage.Id;
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessage")))
            .Returns(Task.FromResult(new EntityCollection([defaultMessage])));
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessage")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([defaultMessage])));

        var defaultFilter = new Entity("sdkmessagefilter", Guid.NewGuid());
        _defaultFilterId = defaultFilter.Id;
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessagefilter")))
            .Returns(Task.FromResult(new EntityCollection([defaultFilter])));
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessagefilter")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection([defaultFilter])));

        var defaultSolution = new Entity("solution")
        {
            ["pub.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", "abc"),
            ["publisher.customizationprefix"] = new AliasedValue("publisher", "customizationprefix", "abc")
        };
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "solution")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { defaultSolution })));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "solutioncomponent" && q.Criteria.Conditions.Any(c => c.AttributeName == "objectid"))),
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
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "solution")), Arg.Any<CancellationToken>())
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
            _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly")))
                .Returns(Task.FromResult(new EntityCollection()));
            _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly")), Arg.Any<CancellationToken>())
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
            _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly")))
                .Returns(Task.FromResult(new EntityCollection(new List<Entity> { existing })));
            _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly")), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new EntityCollection(new List<Entity> { existing })));
        }
    }

    private void SetupPluginTypes(params Entity[] types)
    {
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "plugintype" && q.LinkEntities.Count == 0)))
            .Returns(Task.FromResult(new EntityCollection(types.ToList())));
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "plugintype" && q.LinkEntities.Count == 0)), Arg.Any<CancellationToken>())
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
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep")))
            .Returns(Task.FromResult(new EntityCollection(queryableSteps)));
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(queryableSteps)));
    }

    private void SetupImages(params Entity[] images)
    {
        foreach (var i in images)
        {
            if (!i.Contains("sdkmessageprocessingstepid"))
                i["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", Guid.NewGuid());
        }
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstepimage")))
            .Returns(Task.FromResult(new EntityCollection(images.ToList())));
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstepimage")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(images.ToList())));
    }

    private void SetupCustomApis(params Entity[] customApis)
    {
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "customapi")))
            .Returns(Task.FromResult(new EntityCollection(customApis.ToList())));
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "customapi")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(customApis.ToList())));
    }

    private void SetupRequestParameters(params Entity[] parameters)
    {
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "customapirequestparameter")))
            .Returns(Task.FromResult(new EntityCollection(parameters.ToList())));
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "customapirequestparameter")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(parameters.ToList())));
    }

    private void SetupResponseProperties(params Entity[] properties)
    {
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "customapiresponseproperty")))
            .Returns(Task.FromResult(new EntityCollection(properties.ToList())));
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "customapiresponseproperty")), Arg.Any<CancellationToken>())
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

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<CreateRequest>(r =>
            r.Target.LogicalName == "pluginassembly" &&
            r.Target.GetAttributeValue<string>("name") == "MyPlugin" &&
            r["SolutionUniqueName"].ToString() == "MySolution"
        )), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_ExistingPackageOwnedAssembly_ThrowsBeforeAnyDataverseWrite()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId));

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution"));

        Assert.Contains("MyPlugin", ex.Message);
        Assert.Contains("package", ex.Message, StringComparison.OrdinalIgnoreCase);
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_ExistingAssembly_UpdatesVersion()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, "1.0.0.0"));
        SetupPluginTypes();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(version: "1.0.0.1"), "MySolution");

        await _serviceMock.Received(1).UpdateAsync(Arg.Is(Matching<Entity>(e =>
            e.LogicalName == "pluginassembly" &&
            e.Id == assemblyId &&
            e.GetAttributeValue<string>("version") == "1.0.0.1"
        )), Arg.Any<CancellationToken>());
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

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<CreateRequest>(r =>
            r.Target.LogicalName == "plugintype" &&
            r.Target.GetAttributeValue<string>("typename") == "MyNamespace.MyPlugin" &&
            !r.Target.Contains("workflowactivitygroupname") &&
            r["SolutionUniqueName"].ToString() == "MySolution"
        )), Arg.Any<CancellationToken>());
    }

    // -- Workflow type creation --

    [Fact]
    public async Task SyncSolutionAsync_NewWorkflowType_SetsWorkflowActivityGroupName()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId));
        SetupPluginTypes();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(plugins: new PluginTypeMetadata("MyActivity", "MyNamespace.MyActivity", [], [], true)), "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<CreateRequest>(r =>
            r.Target.LogicalName == "plugintype" &&
            r.Target.GetAttributeValue<string>("typename") == "MyNamespace.MyActivity" &&
            r.Target.GetAttributeValue<string>("workflowactivitygroupname") == "MyPlugin (1.0.0.0)" &&
            r["SolutionUniqueName"].ToString() == "MySolution"
        )), Arg.Any<CancellationToken>());
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
            Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep")),
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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "solutioncomponent" && q.Criteria.Conditions.Any(c => c.AttributeName == "componenttype"))),
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

    // -- U4/R5: sibling plugin projects in one push are not each other's orphans --

    [Fact]
    public void ExcludePushedAssemblies_WithNoPushedNames_ShouldUseNotEqualOnTheSyncedAssembly()
    {
        // R7 regression guard: a single-plugin-project solution must issue the query it always did.
        var condition = PluginService.ExcludePushedAssemblies("name", "Plugins", null);

        condition.AttributeName.Should().Be("name");
        condition.Operator.Should().Be(ConditionOperator.NotEqual);
        condition.Values.Should().Equal(["Plugins"]);
    }

    [Fact]
    public void ExcludePushedAssemblies_WithOnlyTheSyncedAssemblyPushed_ShouldStillUseNotEqual()
    {
        var condition = PluginService.ExcludePushedAssemblies("name", "Plugins", ["Plugins"]);

        condition.Operator.Should().Be(ConditionOperator.NotEqual);
        condition.Values.Should().Equal(["Plugins"]);
    }

    [Fact]
    public void ExcludePushedAssemblies_WithSiblingProjects_ShouldExcludeEveryPushedAssembly()
    {
        var condition = PluginService.ExcludePushedAssemblies("name", "Sales", ["Sales", "Support"]);

        condition.Operator.Should().Be(ConditionOperator.NotIn);
        condition.Values.Should().BeEquivalentTo(["Sales", "Support"]);
    }

    [Fact]
    public void SiblingAssemblyNames_WithTheSyncedAssemblyOnly_ShouldBeEmpty() =>
        PluginService.SiblingAssemblyNames("Sales", ["Sales", "SALES"]).Should().BeEmpty();

    [Fact]
    public async Task SyncSolutionAsync_WithSiblingProjectInThePush_ShouldNotQueryItsAssemblyAsAnOrphan()
    {
        // Without this, pushing Sales flags Support's assembly as "no local source" and, under
        // --force delete-orphans, cascade-deletes it — then Support's own push recreates it and
        // deletes Sales'. The exclusion is server-side, so the query is the behaviour.
        SetupAssembly(ExistingAssembly(Guid.NewGuid()));
        SetupPluginTypes();
        SetupSteps();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(name: "Sales"), "MySolution", RunMode.Normal,
            forceDeleteOrphans: true, forceRecreateAssembly: false, pushedAssemblyNames: ["Sales", "Support"]);

        var orphanQuery = _serviceMock.ReceivedCalls()
            .Select(c => c.GetArguments().OfType<QueryExpression>().FirstOrDefault())
            .OfType<QueryExpression>()
            .Single(q => q.EntityName == "pluginassembly"
                      && q.Criteria.Conditions.Any(c => c.Operator is ConditionOperator.NotEqual or ConditionOperator.NotIn));

        orphanQuery.Criteria.Conditions.Single().Values.Should().BeEquivalentTo(["Sales", "Support"]);
    }

    [Fact]
    public async Task SyncSolutionAsync_WithSiblingProjectInThePush_ShouldStillFlagAnAssemblyNoProjectProduces()
    {
        // The sibling exclusion narrows the orphan set; it must not switch cleanup off. An assembly in
        // the solution that no discovered project builds is still an orphan under the existing rules.
        SetupAssembly(ExistingAssembly(Guid.NewGuid()));
        SetupPluginTypes();
        SetupSteps();
        SetupOrphanAssembly(Guid.NewGuid(), "Legacy");

        await _service.SyncSolutionAsync(_serviceMock, Metadata(name: "Sales"), "MySolution", RunMode.Normal,
            forceDeleteOrphans: false, forceRecreateAssembly: false, pushedAssemblyNames: ["Sales", "Support"]);

        _console.Output.Should().Contain("Legacy.dll").And.Contain("--force delete-orphans");
    }

    [Fact]
    public async Task SyncSolutionAsync_WithoutSiblingProjects_ShouldNotQueryPluginTypesOfOtherAssemblies()
    {
        // R7: the single-project push makes no extra round-trip and plans exactly as it did before U4.
        SetupAssembly(ExistingAssembly(Guid.NewGuid()));
        SetupPluginTypes();
        SetupSteps();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(name: "Plugins"), "MySolution");

        _serviceMock.ReceivedCalls()
            .Select(c => c.GetArguments().OfType<QueryExpression>().FirstOrDefault())
            .OfType<QueryExpression>()
            .Should().NotContain(q => q.EntityName == "plugintype"
                                   && q.LinkEntities.Any(l => l.LinkToEntityName == "pluginassembly"));
    }

    private void SetupOrphanAssembly(Guid assemblyId, string assemblyName) =>
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly"
                                          && q.Criteria.Conditions.Any(c => c.Operator == ConditionOperator.NotEqual || c.Operator == ConditionOperator.NotIn))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity>
            {
                new Entity("pluginassembly", assemblyId) { ["name"] = assemblyName }
            })));

    // -- Package-owned orphan assemblies --

    // Stubs the orphan query, the org-wide "what else does this package own" query, and the package
    // name lookup. alsoOwnedAssemblyIds are assemblies the package owns that this solution can't see.
    private void SetupPackageOwnedOrphan(Guid assemblyId, string assemblyName, Guid packageId, string packageUniqueName, params Guid[] alsoOwnedAssemblyIds)
    {
        EntityReference PackageRef() => new("pluginpackage", packageId);

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly"
                                          && q.Criteria.Conditions.Any(c => c.Operator == ConditionOperator.NotEqual || c.Operator == ConditionOperator.NotIn))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity>
            {
                new Entity("pluginassembly", assemblyId) { ["name"] = assemblyName, ["packageid"] = PackageRef() }
            })));

        var owned = alsoOwnedAssemblyIds.Prepend(assemblyId)
                                        .Select(id => new Entity("pluginassembly", id) { ["packageid"] = PackageRef() })
                                        .ToList();
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly"
                                          && q.Criteria.Conditions.Any(c => c.AttributeName == "packageid" && c.Operator == ConditionOperator.In))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(owned)));

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginpackage")),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity>
            {
                new Entity("pluginpackage", packageId) { ["uniquename"] = packageUniqueName }
            })));
    }

    [Fact]
    public async Task SyncSolutionAsync_OrphanAssemblyWithNoPackage_WithForce_DeletesTheAssembly()
    {
        // Baseline for the two package cases below: the classic orphan still deletes as it always did.
        var orphanId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(Guid.NewGuid()));
        SetupPluginTypes();
        SetupSteps();
        SetupOrphanAssembly(orphanId, "Legacy");

        await _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution", RunMode.Normal, forceDeleteOrphans: true, forceRecreateAssembly: false);

        await _serviceMock.Received(1).DeleteAsync("pluginassembly", orphanId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("pluginpackage", Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionAsync_PackageOwnedOrphanThePackageFullyOwns_WithForce_DeletesThePackage()
    {
        // Dataverse refuses a direct pluginassembly delete while packageid is set — deleting the package
        // is the only lever, and it's safe here because the package owns nothing else.
        var orphanId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(Guid.NewGuid()));
        SetupPluginTypes();
        SetupSteps();
        SetupPackageOwnedOrphan(orphanId, "Plugins", packageId, "av_Plugins");

        await _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution", RunMode.Normal, forceDeleteOrphans: true, forceRecreateAssembly: false);

        await _serviceMock.Received(1).DeleteAsync("pluginpackage", packageId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", orphanId, Arg.Any<CancellationToken>());
        _console.Output.Should().Contain("av_Plugins");
    }

    [Fact]
    public async Task SyncSolutionAsync_PackageOwnedOrphanSharingItsPackage_WithForce_DeletesNothing()
    {
        // The package also owns an assembly the solution-scoped orphan query can't see. Deleting the
        // package would take that live assembly with it, so nothing is touched — not even the orphan's
        // children, which the old code destroyed before hitting the refused assembly delete.
        var orphanId = Guid.NewGuid();
        var packageId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(Guid.NewGuid()));
        SetupPluginTypes();
        SetupSteps();
        SetupPackageOwnedOrphan(orphanId, "Plugins", packageId, "av_Shared", Guid.NewGuid());

        await _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution", RunMode.Normal, forceDeleteOrphans: true, forceRecreateAssembly: false);

        await _serviceMock.DidNotReceive().DeleteAsync("pluginpackage", Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", orphanId, Arg.Any<CancellationToken>());
        _console.Output.Should().Contain("av_Shared").And.Contain("aren't orphans");
    }

    [Fact]
    public async Task SyncSolutionAsync_PackageOwnedOrphanSharingItsPackage_WithForce_LeavesItsStepsAlone()
    {
        // The orphan-step pass keys off "assembly not in this push", which a refused package-owned
        // orphan is by definition — so it would delete the steps the assembly pass just declined to
        // touch, re-creating the half-cleaned state, in the same run that printed "remove the package
        // yourself". The exclusion is server-side, so the query is the behaviour.
        var orphanId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(Guid.NewGuid()));
        SetupPluginTypes();
        SetupSteps();
        SetupPackageOwnedOrphan(orphanId, "Plugins", Guid.NewGuid(), "av_Shared", Guid.NewGuid());

        await _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution", RunMode.Normal, forceDeleteOrphans: true, forceRecreateAssembly: false);

        var assemblyLink = _serviceMock.ReceivedCalls()
            .Select(c => c.GetArguments().OfType<QueryExpression>().FirstOrDefault())
            .OfType<QueryExpression>()
            .Single(q => q.EntityName == "solutioncomponent"
                      && q.LinkEntities.Any(l => l.LinkToEntityName == "sdkmessageprocessingstep"))
            .LinkEntities.SelectMany(l => l.LinkEntities).SelectMany(l => l.LinkEntities)
            .Single(l => l.LinkToEntityName == "pluginassembly");

        assemblyLink.LinkCriteria.Conditions
            .Should().ContainSingle(c => c.AttributeName == "pluginassemblyid" && c.Operator == ConditionOperator.NotIn)
            .Which.Values.Should().Equal([orphanId]);
    }

    [Fact]
    public async Task SyncSolutionAsync_DeletingAnOrphanAssembly_LeavesACustomApiItNeverImplemented()
    {
        // A snapshot's PluginTypes/Steps/Images are assembly-scoped, but its CustomApis are resolved
        // publisher-prefix-wide — every API under the prefix, across every project and repo sharing it.
        // Deleting the whole list took out other people's live public APIs, silently, on one orphan.
        var orphanId = Guid.NewGuid();
        var orphanTypeId = Guid.NewGuid();
        var ownApiId = Guid.NewGuid();
        var foreignApiId = Guid.NewGuid();
        var ownParamId = Guid.NewGuid();
        var foreignParamId = Guid.NewGuid();
        var ownPropId = Guid.NewGuid();
        var foreignPropId = Guid.NewGuid();

        SetupAssembly(ExistingAssembly(Guid.NewGuid()));
        SetupPluginTypes();
        SetupSteps();
        SetupOrphanAssembly(orphanId, "Legacy");
        SetupPluginTypesForAssembly(orphanId, new Entity("plugintype", orphanTypeId) { ["typename"] = "Legacy.MyPlugin" });
        SetupCustomApis(
            new Entity("customapi", ownApiId) { ["uniquename"] = "abc_LegacyApi", ["plugintypeid"] = new EntityReference("plugintype", orphanTypeId) },
            new Entity("customapi", foreignApiId) { ["uniquename"] = "abc_ForeignApi", ["plugintypeid"] = new EntityReference("plugintype", Guid.NewGuid()) });
        // Parameters and properties come back prefix-wide too, and they carry only customapiid — if that
        // read ever breaks, the filter silently drops every one and a real orphan's children survive.
        SetupRequestParameters(
            new Entity("customapirequestparameter", ownParamId) { ["uniquename"] = "abc_LegacyParam", ["customapiid"] = new EntityReference("customapi", ownApiId) },
            new Entity("customapirequestparameter", foreignParamId) { ["uniquename"] = "abc_ForeignParam", ["customapiid"] = new EntityReference("customapi", foreignApiId) });
        SetupResponseProperties(
            new Entity("customapiresponseproperty", ownPropId) { ["uniquename"] = "abc_LegacyProp", ["customapiid"] = new EntityReference("customapi", ownApiId) },
            new Entity("customapiresponseproperty", foreignPropId) { ["uniquename"] = "abc_ForeignProp", ["customapiid"] = new EntityReference("customapi", foreignApiId) });

        await _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution", RunMode.Normal, forceDeleteOrphans: true, forceRecreateAssembly: false);

        await _serviceMock.Received(1).DeleteAsync("customapi", ownApiId, Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).DeleteAsync("customapirequestparameter", ownParamId, Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).DeleteAsync("customapiresponseproperty", ownPropId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("customapi", foreignApiId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("customapirequestparameter", foreignParamId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("customapiresponseproperty", foreignPropId, Arg.Any<CancellationToken>());
        // Deleting a public API surface must not be invisible — the cascade lists it like any other child.
        _console.Output.Should().Contain("abc_LegacyApi").And.NotContain("abc_ForeignApi");
    }

    [Fact]
    public async Task SyncSolutionAsync_PackageOwnedOrphanSharingItsPackage_DryRun_PromisesNoCascade()
    {
        // Nothing will be deleted here, so listing children as "would delete" would be a lie in the
        // preview the user is about to trust. The orphan is given a plugin type precisely so the
        // cascade lines would print if the blocked branch didn't stop first.
        var orphanId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(Guid.NewGuid()));
        SetupPluginTypes();
        SetupSteps();
        SetupPackageOwnedOrphan(orphanId, "Plugins", Guid.NewGuid(), "av_Shared", Guid.NewGuid());
        SetupPluginTypesForAssembly(orphanId, new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "Plugins.MyPlugin", ["isworkflowactivity"] = false });

        await _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution", RunMode.DryRun, forceDeleteOrphans: true, forceRecreateAssembly: false);

        _console.Output.Should().Contain("av_Shared").And.NotContain("would delete");
    }

    [Fact]
    public async Task SyncSolutionAsync_PackageOwnedOrphan_WithoutForce_NamesThePackageInTheHint()
    {
        // "Use --force delete-orphans to delete" alone was a lie for a package-owned orphan — say what
        // the flag actually removes.
        SetupAssembly(ExistingAssembly(Guid.NewGuid()));
        SetupPluginTypes();
        SetupSteps();
        SetupPackageOwnedOrphan(Guid.NewGuid(), "Plugins", Guid.NewGuid(), "av_Plugins");

        await _service.SyncSolutionAsync(_serviceMock, Metadata(), "MySolution");

        _console.Output.Should().Contain("--force delete-orphans").And.Contain("av_Plugins");
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

        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessage")))
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } })));
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessage")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } })));
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessagefilter")))
            .Returns(Task.FromResult(new EntityCollection([new Entity("sdkmessagefilter", Guid.NewGuid())])));
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessagefilter")), Arg.Any<CancellationToken>())
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

        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Is(Matching<Entity>(e => e.LogicalName == "pluginassembly")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_ChangedAssembly_UploadsNewContent()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));
        SetupPluginTypes();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(hash: "newhash"), "MySolution");

        await _serviceMock.Received(1).UpdateAsync(Arg.Is(Matching<Entity>(e =>
            e.LogicalName == "pluginassembly" &&
            e.GetAttributeValue<string>("description") == "[flowline] sha256=newhash"
        )), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncAsync_ExistingAssemblyInOtherSolutions_EmitsWarningWithSolutionNames()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, hash: "oldhash"));
        SetupPluginTypes();

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "solutioncomponent" && q.Criteria.Conditions.Any(c => c.AttributeName == "objectid"))),
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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "solutioncomponent" && q.Criteria.Conditions.Any(c => c.AttributeName == "objectid"))),
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
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "solution")), Arg.Any<CancellationToken>())
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
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "customapi")), Arg.Any<CancellationToken>())
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

        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessage")))
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } })));
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessage")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { new Entity("sdkmessage", Guid.NewGuid()) { ["name"] = "Update" } })));
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessagefilter")))
            .Returns(Task.FromResult(new EntityCollection([new Entity("sdkmessagefilter", Guid.NewGuid())])));
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessagefilter")), Arg.Any<CancellationToken>())
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
        _serviceMock.UpdateAsync(Arg.Is(Matching<Entity>(e => e.LogicalName == "pluginassembly")), Arg.Any<CancellationToken>())
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
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<CreateRequest>(r => r.Target.LogicalName == "pluginassembly")), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Is(Matching<Entity>(e => e.LogicalName == "pluginassembly")), Arg.Any<CancellationToken>());
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
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<CreateRequest>(r => r.Target.LogicalName == "pluginassembly")), Arg.Any<CancellationToken>());
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
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<CreateRequest>(r => r.Target.LogicalName == "pluginassembly")), Arg.Any<CancellationToken>());
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
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<CreateRequest>(r => r.Target.LogicalName == "pluginassembly")), Arg.Any<CancellationToken>());
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

    // The message is escaped before it reaches the console (it can carry arbitrary Dataverse text), so
    // Spectre markup in the exception itself renders as literal "[bold]…[/]" in the error line.
    [Fact]
    public async Task SyncAsync_IdentityChanged_NoForce_ExceptionMessageHasNoSpectreMarkup()
    {
        SetupAssembly(ExistingAssembly(Guid.NewGuid(), pkt: "df889c1cc53657b7"));
        SetupPluginTypes();

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionAsync(_serviceMock, Metadata(pkt: "a4d07ffa42de325f"), "MySolution", RunMode.Normal));

        Assert.DoesNotContain("[bold]", ex.Message);
        Assert.DoesNotContain("[/]", ex.Message);
        Assert.Contains("MyPlugin", ex.Message);
        Assert.Contains("--force recreate-assembly", ex.Message);
    }

    [Fact]
    public async Task SyncAsync_IdentityChanged_NoDeleteMode_ExceptionMessageHasNoSpectreMarkup()
    {
        SetupAssembly(ExistingAssembly(Guid.NewGuid(), pkt: "df889c1cc53657b7"));
        SetupPluginTypes();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.SyncSolutionAsync(_serviceMock, Metadata(pkt: "a4d07ffa42de325f"), "MySolution", RunMode.NoDelete));

        Assert.DoesNotContain("[bold]", ex.Message);
        Assert.DoesNotContain("[/]", ex.Message);
        Assert.Contains("MyPlugin", ex.Message);
    }

    [Fact]
    public async Task SyncAsync_BuildVersionChanged_DoesNotDeleteAssembly()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(ExistingAssembly(assemblyId, version: "1.0.0.0", hash: "oldhash"));
        SetupPluginTypes();

        await _service.SyncSolutionAsync(_serviceMock, Metadata(version: "1.0.5.0", hash: "newhash"), "MySolution");

        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", assemblyId, Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).UpdateAsync(Arg.Is(Matching<Entity>(e => e.LogicalName == "pluginassembly")), Arg.Any<CancellationToken>());
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
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<CreateRequest>(r => r.Target.LogicalName == "pluginassembly")), Arg.Any<CancellationToken>());
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
    public async Task SyncAssemblyOnlyAsync_ExistingPackageOwnedAssembly_ThrowsBeforeAnyDataverseWrite()
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId));

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncAssemblyOnlyAsync(_serviceMock, Metadata(), "MySolution"));

        Assert.Contains("MyPlugin", ex.Message);
        Assert.Contains("package", ex.Message, StringComparison.OrdinalIgnoreCase);
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

        await _serviceMock.Received(1).UpdateAsync(Arg.Is(Matching<Entity>(e =>
            e.LogicalName == "pluginassembly" &&
            e.Id == assemblyId &&
            e.GetAttributeValue<string>("description") == "[flowline] sha256=newhash"
        )), Arg.Any<CancellationToken>());
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
            Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep")), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().RetrieveMultipleAsync(
            Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstepimage")), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().RetrieveMultipleAsync(
            Arg.Is(Matching<QueryExpression>(q => q.EntityName == "customapi")), Arg.Any<CancellationToken>());
    }

    // -- SyncSolutionFromPackageAsync (pluginpackage / NuGet path) --

    // A real zip, not arbitrary bytes: change detection reads the package's lib/ payload out of the
    // container, so a stand-in that isn't a valid archive can't reach the code under test.
    private static readonly byte[] NupkgBytes =
        BuildNupkg("fixed-for-tests", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), "1.0.0", ("lib/net462/MyPlugin.dll", [1, 2, 3, 4, 5]));
    private static string NupkgHash => PluginService.ComputeNupkgPayloadHash(NupkgBytes);

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

    private static Entity ClassicAssemblyNoPackage(Guid id, string name = "MyPlugin")
    {
        var e = new Entity("pluginassembly", id);
        e["name"] = name;
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
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginpackage")))
            .Returns(Task.FromResult(col));
        _serviceMock.RetrieveMultipleAsync(Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginpackage")), Arg.Any<CancellationToken>())
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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly"
                    && q.Criteria.Conditions.Any(c => c.AttributeName == "packageid")
                    && HasCondition(q, "name", assemblyName))),
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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "plugintype" && q.LinkEntities.Count == 0 && HasCondition(q, "pluginassemblyid", assemblyId))),
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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "sdkmessageprocessingstep" && HasLinkCondition(q, "pluginassemblyid", assemblyId))),
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
        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<CreateRequest>(r =>
            r.Target.LogicalName == "pluginpackage" &&
            r.Target.GetAttributeValue<string>("name") == "abc_MyPlugin" &&
            r.Target.GetAttributeValue<string>("uniquename") == "abc_MyPlugin" &&
            r.Target.GetAttributeValue<string>("version") == "2.3.1.0" &&
            r.Target.Contains("content") &&
            r["SolutionUniqueName"].ToString() == "MySolution"
        )), Arg.Any<CancellationToken>());
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

    // -- Dry-run assembly-set preview (U2, R2) --

    [Fact]
    public async Task SyncSolutionFromPackageAsync_DryRun_PendingAdd_NamesItInThePreview()
    {
        var packageId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId));
        SetupPluginPackage(ExistingPluginPackage(packageId));
        SetupPackageAssemblyByName(assemblyId, "MyPlugin");
        SetupRegisteredPackageAssemblies(packageId, "MyPlugin"); // only MyPlugin registered -> Extra is pending

        List<PluginAssemblyMetadata> assemblies =
        [
            .. PackageAssemblies("MyPlugin"),
            .. PackageAssemblies("Extra"),
        ];

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, assemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution", RunMode.DryRun);

        Assert.True(result);
        _console.Output.Should().Contain("Extra.dll").And.Contain("would add");
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_DryRun_PendingDrop_NamesItInThePreview()
    {
        var packageId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId));
        SetupPluginPackage(ExistingPluginPackage(packageId));
        SetupPackageAssemblyByName(assemblyId, "MyPlugin");
        SetupDroppedPackageAssembly(packageId, "GoneAssembly");

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution", RunMode.DryRun);

        Assert.True(result);
        _console.Output.Should().Contain("GoneAssembly.dll").And.Contain("would drop from the package");
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_DryRun_PendingAddAndDrop_NoDeleteAndNoContentWrite()
    {
        var packageId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId));
        SetupPluginPackage(ExistingPluginPackage(packageId));
        SetupPackageAssemblyByName(assemblyId, "MyPlugin");
        SetupDroppedPackageAssembly(packageId, "GoneAssembly");

        List<PluginAssemblyMetadata> assemblies =
        [
            .. PackageAssemblies("MyPlugin"),
            .. PackageAssemblies("Extra"),
        ];

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, assemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution", RunMode.DryRun);

        Assert.True(result);
        _console.Output.Should().Contain("Extra.dll").And.Contain("GoneAssembly.dll");
        await _serviceMock.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<UpdateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_DryRun_NoAssemblySetChange_PreviewsWithoutAddOrDropNoise()
    {
        var packageId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId));
        SetupPluginPackage(ExistingPluginPackage(packageId));
        SetupPackageAssemblyByName(assemblyId, "MyPlugin");
        SetupRegisteredPackageAssemblies(packageId, "MyPlugin"); // matches the one reflected assembly exactly

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution", RunMode.DryRun);

        Assert.True(result);
        _console.Output.Should().Contain("would update content").And.NotContain("would add").And.NotContain("would drop");
    }

    // -- Dry-run summary counts (the package/assembly content write is an update, and a package push
    //    reports ONE total, not one summary per assembly it owns) --

    static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_DryRun_ExistingPackageChanged_CountsPackageUpdateInSummary()
    {
        SetupAssembly(PackageOwnedAssembly(Guid.NewGuid(), hash: "stalehash"));
        SetupPluginPackage(ExistingPluginPackage(Guid.NewGuid()));

        await _service.SyncSolutionFromPackageAsync(
            _serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution", RunMode.DryRun);

        // Was "0 update(s)" printed directly above its own "would update content" line.
        Assert.Contains("would update content", _console.Output);
        Assert.Contains("Dry run: 0 delete(s), 0 create(s), 1 update(s)", _console.Output);
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_DryRun_NewPackage_CountsPackageCreateInSummary()
    {
        SetupAssembly();
        SetupPluginPackage();

        await _service.SyncSolutionFromPackageAsync(
            _serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution", RunMode.DryRun);

        Assert.Contains("Package", _console.Output);
        Assert.Contains("would create", _console.Output);
        Assert.Contains("1 create(s)", _console.Output);
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_DryRun_MultipleAssemblies_WritesOneSummaryForThePackage()
    {
        SetupAssembly(PackageOwnedAssembly(Guid.NewGuid(), hash: "stalehash"));
        SetupPluginPackage(ExistingPluginPackage(Guid.NewGuid()));

        List<PluginAssemblyMetadata> assemblies =
        [
            .. PackageAssemblies("MyPlugin"),
            .. PackageAssemblies("MyPlugin.Secondary"),
        ];

        await _service.SyncSolutionFromPackageAsync(
            _serviceMock, assemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution", RunMode.DryRun);

        Assert.Equal(1, CountOccurrences(_console.Output, "Dry run:"));
    }

    [Fact]
    public async Task SyncAsync_DryRun_AssemblyContentChangedOnly_ReportsOneUpdate()
    {
        // Classic (non-package) assembly: hash differs, nothing else does. The content write runs its
        // own execute phase, so it has to appear in the summary.
        SetupAssembly(ExistingAssembly(Guid.NewGuid(), hash: "oldhash"));
        SetupPluginTypes(new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "MyNamespace.MyPlugin", ["isworkflowactivity"] = false });

        await _service.SyncSolutionAsync(
            _serviceMock,
            Metadata(hash: "newhash", plugins: new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], [], false)),
            "MySolution",
            RunMode.DryRun);

        Assert.Contains("would update content", _console.Output);
        Assert.Contains("Dry run: 0 delete(s), 0 create(s), 1 update(s)", _console.Output);
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
        await _serviceMock.Received(1).UpdateAsync(Arg.Is(Matching<Entity>(e =>
            e.LogicalName == "pluginpackage" &&
            e.Id == packageId &&
            e.Contains("content") &&
            !e.Contains("version")
        )), Arg.Any<CancellationToken>());
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

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionFromPackageAsync(_serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution"));

        Assert.Contains("MyPlugin", ex.Message);
        Assert.Contains("classic", ex.Message, StringComparison.OrdinalIgnoreCase);
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_SecondaryAssemblyIsClassic_ThrowsBeforeAnyDataverseWrite()
    {
        var secondaryId = Guid.NewGuid();
        SetupAssembly(); // primary "MyPlugin" not registered yet -> no conflict for the primary itself
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly")), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity> { ClassicAssemblyNoPackage(secondaryId, "SecondaryPlugin") })));

        var assemblies = new List<PluginAssemblyMetadata>
        {
            new("MyPlugin", "MyPlugin, Version=1.0.0.0", new byte[] { 9, 9, 9 }, "dll-hash-unused", "1.0.0.0", null, "neutral", []),
            new("SecondaryPlugin", "SecondaryPlugin, Version=1.0.0.0", new byte[] { 9, 9, 9 }, "dll-hash-unused", "1.0.0.0", null, "neutral", [])
        };

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionFromPackageAsync(_serviceMock, assemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution"));

        Assert.Contains("SecondaryPlugin", ex.Message);
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
            Arg.Is(Matching<Entity>(e => e.LogicalName == "pluginpackage" && e.Id == packageId)), Arg.Any<CancellationToken>());
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
            _serviceMock.ExecuteAsync(Arg.Is(Matching<CreateRequest>(r => r.Target.LogicalName == "pluginpackage")), Arg.Any<CancellationToken>());
            _serviceMock.UpdateAsync(Arg.Is(Matching<Entity>(e =>
                e.LogicalName == "pluginassembly" &&
                (e.GetAttributeValue<string>("description") ?? "").Contains("sha256="))),
                Arg.Any<CancellationToken>());
            _serviceMock.ExecuteAsync(Arg.Is(Matching<CreateRequest>(r => r.Target.LogicalName == "plugintype")), Arg.Any<CancellationToken>());
            _serviceMock.ExecuteAsync(Arg.Is(Matching<CreateRequest>(r => r.Target.LogicalName == "sdkmessageprocessingstep")), Arg.Any<CancellationToken>());
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
            _serviceMock.UpdateAsync(Arg.Is(Matching<Entity>(e => e.LogicalName == "pluginpackage" && e.Id == packageId)), Arg.Any<CancellationToken>());
            _serviceMock.ExecuteAsync(Arg.Is(Matching<CreateRequest>(r =>
                r.Target.LogicalName == "plugintype" && r.Target.GetAttributeValue<string>("typename") == "Ns.Surviving")), Arg.Any<CancellationToken>());
            _serviceMock.ExecuteAsync(Arg.Is(Matching<CreateRequest>(r => r.Target.LogicalName == "sdkmessageprocessingstep")), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_StepRemovedFromSurvivingType_DeletedAfterUpdate()
    {
        // Correctness regression: the plugin TYPE survives (still declared locally, so PluginTypes.Deletes
        // is empty) but its only step was removed from source. The pre-update delete gate only fires on
        // PluginTypes.Deletes (KD4's ordering constraint), so without a post-update delete pass this
        // obsolete step would never be cleaned up.
        var assemblyId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var removedStepId = Guid.NewGuid();

        SetupAssembly(PackageOwnedAssembly(assemblyId, hash: "stalehash"));
        var packageId = Guid.NewGuid();
        SetupPluginPackage(ExistingPluginPackage(packageId));
        SetupPluginTypes(new Entity("plugintype", typeId) { ["typename"] = "Ns.Surviving" });
        SetupSteps(new Entity("sdkmessageprocessingstep", removedStepId)
        {
            ["name"] = "Ns.Surviving: Update of account",
            ["plugintypeid"] = new EntityReference("plugintype", typeId),
            ["stage"] = new OptionSetValue(20)
        });

        // Locally, the type still exists but now declares zero steps — the step registration was removed
        // from source while the class itself stayed.
        var survivingType = new PluginTypeMetadata("Surviving", "Ns.Surviving", [], []);
        var assemblies = new List<PluginAssemblyMetadata>
        {
            new("MyPlugin", "MyPlugin, Version=1.0.0.1", new byte[] { 9, 9, 9 }, "dll-hash-unused", "1.0.0.1", null, "neutral", [survivingType])
        };

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, assemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        Assert.True(result);
        await _serviceMock.Received(1).DeleteAsync("sdkmessageprocessingstep", removedStepId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("plugintype", Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    // -- Self-registration fallback on confirm expiry (U3, R4-R8) --
    //
    // KTD2: Dataverse never auto-registers an assembly added to an existing package's content — measured
    // live, not a latency issue — so the confirm-retry's expiry is the trigger for registering it
    // directly instead of failing. PackageAssemblyCheckMaxAttempts/-Delay are dropped to avoid paying the
    // real ~4s the production budget costs to reach expiry.

    // Wires the mocks so "Extra" (the second, not-yet-registered assembly) is invisible to
    // FindPackageAssemblyAsync's packageid+name query until the given CreateRequest predicate has fired
    // once — the same shape Dataverse itself would produce: absent, then present only after a create.
    void SetupPackageAssemblySelfRegisteredOnCreate(Guid packageId, string assemblyName)
    {
        Guid? selfRegisteredId = null;

        _serviceMock.ExecuteAsync(
                Arg.Is(Matching<CreateRequest>(r => r.Target.LogicalName == "pluginassembly" && r.Target.GetAttributeValue<string>("name") == assemblyName)),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                selfRegisteredId = Guid.NewGuid();
                var response = new CreateResponse();
                response.Results["id"] = selfRegisteredId.Value;
                return Task.FromResult<OrganizationResponse>(response);
            });

        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly"
                    && q.Criteria.Conditions.Any(c => c.AttributeName == "packageid")
                    && HasCondition(q, "name", assemblyName))),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(selfRegisteredId is { } id
                ? new EntityCollection(new List<Entity> { new Entity("pluginassembly", id) { ["name"] = assemblyName, ["version"] = "1.0.0.0" } })
                : new EntityCollection()));
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_MissingAfterConfirmExpiry_SelfRegistersAndNamesItInOutput()
    {
        // Covers R4, R5, R6, R7: the confirm-retry expires for "Extra" (never found), Flowline creates
        // the record itself with no --force involved (forceDeleteOrphans defaults to false), and names
        // the assembly in normal output.
        var packageId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId));
        SetupPluginPackage(ExistingPluginPackage(packageId));
        SetupPackageAssemblyByName(assemblyId, "MyPlugin");
        SetupRegisteredPackageAssemblies(packageId, "MyPlugin"); // "Extra" isn't registered yet -> pending add
        SetupPackageAssemblySelfRegisteredOnCreate(packageId, "Extra");
        _service.PackageAssemblyCheckMaxAttempts = 1;
        _service.PackageAssemblyCheckDelay = TimeSpan.Zero;

        List<PluginAssemblyMetadata> assemblies =
        [
            .. PackageAssemblies("MyPlugin"),
            .. PackageAssemblies("Extra"),
        ];

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, assemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        Assert.True(result);
        // Contiguous phrase, not two separately-satisfiable substrings — the multi-assembly info line
        // ("Package contains 2 plugin-bearing assemblies: MyPlugin, Extra") already contains "Extra" on
        // its own, so this has to be the R7 registration line specifically.
        _console.Output.Should().Contain("Assembly Extra registered directly");
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_SelfRegisteredAssembly_CarriesPackageAssociationAndFullIdentity()
    {
        // Covers R5: KTD3's field set, as settled against a live environment. Two rejections define it.
        // Without isolationmode: "not allowed to be registered in full-trust mode". With only
        // name/package/isolation: "Unable to load plug-in assembly" — a package-owned row has no content
        // of its own, so Dataverse resolves which DLL in the package the row means from the assembly's
        // full identity. Dropping version, culture or public key token from this assertion re-opens a
        // failure that only shows up against a real environment.
        var packageId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId));
        SetupPluginPackage(ExistingPluginPackage(packageId));
        SetupPackageAssemblyByName(assemblyId, "MyPlugin");
        SetupRegisteredPackageAssemblies(packageId, "MyPlugin");
        SetupPackageAssemblySelfRegisteredOnCreate(packageId, "Extra");
        _service.PackageAssemblyCheckMaxAttempts = 1;
        _service.PackageAssemblyCheckDelay = TimeSpan.Zero;

        List<PluginAssemblyMetadata> assemblies =
        [
            .. PackageAssemblies("MyPlugin"),
            .. PackageAssemblies("Extra"),
        ];

        await _service.SyncSolutionFromPackageAsync(
            _serviceMock, assemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        await _serviceMock.Received(1).ExecuteAsync(Arg.Is(Matching<CreateRequest>(r =>
            r.Target.LogicalName == "pluginassembly" &&
            r.Target.GetAttributeValue<string>("name") == "Extra" &&
            r.Target.GetAttributeValue<EntityReference>("packageid")!.Id == packageId &&
            r.Target.GetAttributeValue<EntityReference>("packageid")!.LogicalName == "pluginpackage" &&
            r.Target.GetAttributeValue<OptionSetValue>("isolationmode")!.Value == 2 &&
            r.Target.GetAttributeValue<string>("version") == "1.0.0.0" &&
            r.Target.GetAttributeValue<string>("culture") == "neutral" &&
            r.Target.Contains("publickeytoken") &&
            r["SolutionUniqueName"].ToString() == "MySolution"
        )), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_SelfRegistration_WritesContentAgainAndReloadsSnapshots()
    {
        // Covers R5/KTD6: the guard right after the confirm step (`if (assemblyEntity == null ||
        // snapshot == null) throw`) is dead code today because the retry always threw first. Once the
        // retry self-registers instead, that guard becomes reachable for the first time — this proves
        // "Extra" reaches it with a non-null entity and snapshot rather than tripping it. The two content
        // writes are the mechanism: the first is the normal update, the second is KTD6's re-write that
        // populates the freshly created assembly's plugin types.
        var packageId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId));
        SetupPluginPackage(ExistingPluginPackage(packageId));
        SetupPackageAssemblyByName(assemblyId, "MyPlugin");
        SetupRegisteredPackageAssemblies(packageId, "MyPlugin");
        SetupPackageAssemblySelfRegisteredOnCreate(packageId, "Extra");
        _service.PackageAssemblyCheckMaxAttempts = 1;
        _service.PackageAssemblyCheckDelay = TimeSpan.Zero;

        List<PluginAssemblyMetadata> assemblies =
        [
            .. PackageAssemblies("MyPlugin"),
            .. PackageAssemblies("Extra"),
        ];

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, assemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        // No InvalidOperationException from the post-confirm guard means "Extra" arrived with a snapshot.
        Assert.True(result);
        await _serviceMock.Received(2).UpdateAsync(
            Arg.Is(Matching<Entity>(e => e.LogicalName == "pluginpackage" && e.Id == packageId)), Arg.Any<CancellationToken>());

        // KTD6: the order is load-bearing — the create has to land before the second content write, not
        // just happen somewhere in the run. Two writes with no create between them registered nothing.
        // NSubstitute's InOrder checks the full realized sequence of every call matching either
        // predicate, so the first (pre-confirm) content write is listed too, ahead of the create.
        Received.InOrder(() =>
        {
            _serviceMock.UpdateAsync(Arg.Is(Matching<Entity>(e => e.LogicalName == "pluginpackage" && e.Id == packageId)), Arg.Any<CancellationToken>());
            _serviceMock.ExecuteAsync(Arg.Is(Matching<CreateRequest>(r => r.Target.LogicalName == "pluginassembly" && r.Target.GetAttributeValue<string>("name") == "Extra")), Arg.Any<CancellationToken>());
            _serviceMock.UpdateAsync(Arg.Is(Matching<Entity>(e => e.LogicalName == "pluginpackage" && e.Id == packageId)), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_AssemblyFoundOnFirstAttempt_IsNotSelfRegistered()
    {
        // Covers R4, R5: F3 — the platform doing its job means the fallback never fires and nothing is
        // reported as registered by Flowline.
        SetupAssembly(); // no existing pluginassembly -> no classic conflict; wires the CreateResponse mock
        SetupPackageAssemblyFoundAfterCreate("MyPlugin", "1.0.0.0"); // found on the very first confirm attempt
        SetupPluginPackage(); // brand-new package

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        Assert.True(result);
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r => r.Target.LogicalName == "pluginassembly")), Arg.Any<CancellationToken>());
        _console.Output.Should().NotContain("registered directly");
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_FoundOnSecondAttempt_WaitsInsteadOfSelfRegistering()
    {
        // Covers R4: the retry itself still gets a chance to succeed before the fallback fires. Every
        // other self-registration test uses MaxAttempts=1, so none of them exercises the loop's
        // continue-and-retry branch — this is the one case where the wait, not the fallback, is what
        // resolves it. Brand-new package: an existing package's preSnapshots would issue its own
        // FindPackageAssemblyAsync call and consume the first counted attempt before the confirm loop
        // even starts.
        SetupAssembly();
        SetupPluginPackage();
        _service.PackageAssemblyCheckMaxAttempts = 2;
        _service.PackageAssemblyCheckDelay = TimeSpan.Zero;

        var attempt = 0;
        var registeredId = Guid.NewGuid();
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly"
                    && q.Criteria.Conditions.Any(c => c.AttributeName == "packageid")
                    && HasCondition(q, "name", "MyPlugin"))),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                attempt++;
                return Task.FromResult(attempt >= 2
                    ? new EntityCollection(new List<Entity> { new Entity("pluginassembly", registeredId) { ["name"] = "MyPlugin", ["version"] = "1.0.0.0" } })
                    : new EntityCollection());
            });

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        Assert.True(result);
        Assert.Equal(2, attempt); // proves the loop actually waited for the second attempt, not a fluke
        await _serviceMock.DidNotReceive().ExecuteAsync(
            Arg.Is(Matching<CreateRequest>(r => r.Target.LogicalName == "pluginassembly")), Arg.Any<CancellationToken>());
        _console.Output.Should().NotContain("registered directly");
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_SelfRegistrationRejected_FailsNamingAssemblyAndPackage_NeverAsATimeout()
    {
        // Covers R8: when the direct create is itself rejected, the failure names the assembly and the
        // package, states the package content now holds an unregistered DLL, and never reads as a
        // timeout — the wait already ran to completion; this is a distinct, harder failure.
        var packageId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId));
        SetupPluginPackage(ExistingPluginPackage(packageId));
        SetupPackageAssemblyByName(assemblyId, "MyPlugin");
        SetupRegisteredPackageAssemblies(packageId, "MyPlugin");
        _service.PackageAssemblyCheckMaxAttempts = 1;
        _service.PackageAssemblyCheckDelay = TimeSpan.Zero;

        // "Extra" is never found under the package — without this override, SetupAssembly's broad
        // pluginassembly match (registered above) would wrongly hand back "MyPlugin"'s entity for it too.
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly"
                    && q.Criteria.Conditions.Any(c => c.AttributeName == "packageid")
                    && HasCondition(q, "name", "Extra"))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection()));

        _serviceMock.ExecuteAsync(
                Arg.Is(Matching<CreateRequest>(r => r.Target.LogicalName == "pluginassembly" && r.Target.GetAttributeValue<string>("name") == "Extra")),
                Arg.Any<CancellationToken>())
            .Returns<OrganizationResponse>(_ => throw new InvalidOperationException("not allowed to be registered in full-trust mode"));

        List<PluginAssemblyMetadata> assemblies =
        [
            .. PackageAssemblies("MyPlugin"),
            .. PackageAssemblies("Extra"),
        ];

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionFromPackageAsync(_serviceMock, assemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution"));

        Assert.Equal(ExitCode.ValidationFailed, ex.ExitCode);
        Assert.Contains("Extra", ex.Message);
        Assert.Contains("abc_MyPlugin", ex.Message); // packageUniqueName
        Assert.Contains("no registration", ex.Message);
        Assert.DoesNotContain("imed out", ex.Message); // never phrased as a timeout (R8)
        Assert.DoesNotContain("Unreachable", ex.Message);
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_OneOfTwoSelfRegistrationsRejected_StillWritesContentForTheOneThatLanded()
    {
        // A created record owns no plugin types until the content write runs. Aborting the loop on the
        // first rejection would leave the assembly that DID register sitting in Dataverse with no types
        // and no mention in the error — a worse state than the message admits.
        var packageId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId));
        SetupPluginPackage(ExistingPluginPackage(packageId));
        SetupPackageAssemblyByName(assemblyId, "MyPlugin");
        SetupRegisteredPackageAssemblies(packageId, "MyPlugin");
        _service.PackageAssemblyCheckMaxAttempts = 1;
        _service.PackageAssemblyCheckDelay = TimeSpan.Zero;

        foreach (var absent in new[] { "Good", "Bad" })
            _serviceMock.RetrieveMultipleAsync(
                    Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly"
                        && q.Criteria.Conditions.Any(c => c.AttributeName == "packageid")
                        && HasCondition(q, "name", absent))),
                    Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(new EntityCollection()));

        var goodCreated = false;
        _serviceMock.ExecuteAsync(
                Arg.Is(Matching<CreateRequest>(r => r.Target.LogicalName == "pluginassembly" && r.Target.GetAttributeValue<string>("name") == "Good")),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                goodCreated = true;
                var response = new CreateResponse();
                response.Results["id"] = Guid.NewGuid();
                return Task.FromResult<OrganizationResponse>(response);
            });

        _serviceMock.ExecuteAsync(
                Arg.Is(Matching<CreateRequest>(r => r.Target.LogicalName == "pluginassembly" && r.Target.GetAttributeValue<string>("name") == "Bad")),
                Arg.Any<CancellationToken>())
            .Returns<OrganizationResponse>(_ => throw new InvalidOperationException("Unable to load plug-in assembly."));

        List<PluginAssemblyMetadata> assemblies =
        [
            .. PackageAssemblies("MyPlugin"),
            .. PackageAssemblies("Good"),
            .. PackageAssemblies("Bad"),
        ];

        var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
            _service.SyncSolutionFromPackageAsync(_serviceMock, assemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution"));

        Assert.True(goodCreated, "the assembly that could register should still have been attempted");
        Assert.Contains("Bad", ex.Message);
        Assert.DoesNotContain("imed out", ex.Message);
        // The content write is what gives 'Good' its plugin types — it must run despite 'Bad' failing.
        await _serviceMock.Received(2).UpdateAsync(
            Arg.Is(Matching<Entity>(e => e.LogicalName == "pluginpackage")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_DryRun_BrandNewPackage_DoesNotListEveryAssemblyAsAnAdd()
    {
        // For a brand-new package every reflected assembly is "added", which the "would create" line
        // already conveys — listing each one again is noise. Deleting the guard that suppresses this
        // should fail here.
        SetupAssembly();
        SetupPluginPackage();

        List<PluginAssemblyMetadata> assemblies =
        [
            .. PackageAssemblies("MyPlugin"),
            .. PackageAssemblies("Extra"),
        ];

        await _service.SyncSolutionFromPackageAsync(
            _serviceMock, assemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution", RunMode.DryRun);

        _console.Output.Should().Contain("would create").And.NotContain("would add to the package");
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_NoDeleteMode_NeverIssuesDeleteAsync()
    {
        // Reliability/correctness regression: --no-delete was previously ignored on the package path
        // (hardcoded `false` passed to the executor's no-delete flag on both the pre- and post-update
        // delete calls), so a removed class's steps were deleted even under RunMode.NoDelete.
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

        var assemblies = new List<PluginAssemblyMetadata>
        {
            new("MyPlugin", "MyPlugin, Version=1.0.0.1", new byte[] { 9, 9, 9 }, "dll-hash-unused", "1.0.0.1", null, "neutral", [])
        };

        await _service.SyncSolutionFromPackageAsync(
            _serviceMock, assemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution", RunMode.NoDelete);

        await _serviceMock.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly"
                    && !q.Criteria.Conditions.Any(c => c.AttributeName == "packageid")
                    && HasCondition(q, "name", "Primary"))),
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
        Assert.Contains("Package contains 2 plugin-bearing assemblies", _console.Output);

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
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly"
                    && !q.Criteria.Conditions.Any(c => c.AttributeName == "packageid")
                    && HasCondition(q, "name", "AssemblyA"))),
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
        Assert.Contains("Package contains 2 plugin-bearing assemblies", _console.Output);
        await _serviceMock.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().UpdateAsync(Arg.Is(Matching<Entity>(e => e.LogicalName == "pluginpackage")), Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>());
    }

    // -- WritePlanTree carry-over for the package path (dry-run preview / verbose display) --

    [Fact]
    public async Task SyncSolutionFromPackageAsync_DryRun_NewPackage_ShowsPlanTree()
    {
        SetupAssembly(); // no existing pluginassembly -> no classic conflict
        SetupPluginPackage(); // no existing package
        SetupPluginTypes(); // no existing plugin types -> the dummy-snapshot fallback sees nothing
        SetupSteps(); // no existing steps

        var plugin = new PluginTypeMetadata("MyPluginType", "Ns.MyPluginType",
            [new PluginStepMetadata("Ns.MyPluginType: Update of account", "Update", "account", 20, 0, 1, null, null, [], [])],
            []);
        var assemblies = new List<PluginAssemblyMetadata>
        {
            new("MyPlugin", "MyPlugin, Version=1.0.0.0", new byte[] { 9, 9, 9 }, "dll-hash-unused", "1.0.0.0", null, "neutral", [plugin])
        };

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, assemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution", RunMode.DryRun);

        Assert.True(result);
        Assert.Contains("MyPluginType", _console.Output);
        Assert.Contains("Update of account", _console.Output);
        Assert.Contains("would create", _console.Output);
        Assert.Contains("Dry run:", _console.Output);
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_Verbose_NewPackage_ShowsPlanTree()
    {
        // Real (non-dry-run) execution must show the same per-assembly tree the dry-run preview shows —
        // WritePlanTree renders via console.Verbose(tree) when runMode != DryRun.
        _runtimeOptions.IsVerbose = true;
        SetupAssembly();
        SetupPackageAssemblyFoundAfterCreate("MyPlugin");
        SetupPluginPackage();
        SetupPluginTypes();
        SetupSteps();

        var plugin = new PluginTypeMetadata("MyPluginType", "Ns.MyPluginType",
            [new PluginStepMetadata("Ns.MyPluginType: Update of account", "Update", "account", 20, 0, 1, null, null, [], [])],
            []);
        var assemblies = new List<PluginAssemblyMetadata>
        {
            new("MyPlugin", "MyPlugin, Version=1.0.0.0", new byte[] { 9, 9, 9 }, "dll-hash-unused", "1.0.0.0", null, "neutral", [plugin])
        };

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, assemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        Assert.True(result);
        Assert.Contains("MyPluginType", _console.Output);
        Assert.Contains("Update of account", _console.Output);
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_DryRun_UnchangedHash_StepDrift_ShowsPlanTree()
    {
        // Steps-only path (hash unchanged): a step that drifted out of Dataverse (e.g. deleted manually)
        // must still preview via WritePlanTree under --dry-run, not just a generic "up to date" message.
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId, hash: NupkgHash));
        SetupPluginPackage(ExistingPluginPackage(Guid.NewGuid()));
        SetupPackageAssemblyByName(assemblyId, "MyPlugin");
        SetupPluginTypesForAssembly(assemblyId); // no existing types registered -> drift
        SetupStepsForAssembly(assemblyId);

        var plugin = new PluginTypeMetadata("MyPluginType", "Ns.MyPluginType",
            [new PluginStepMetadata("Ns.MyPluginType: Update of account", "Update", "account", 20, 0, 1, null, null, [], [])],
            []);
        var assemblies = new List<PluginAssemblyMetadata>
        {
            new("MyPlugin", "MyPlugin, Version=1.0.0.0", new byte[] { 9, 9, 9 }, "dll-hash-unused", "1.0.0.0", null, "neutral", [plugin])
        };

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, assemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution", RunMode.DryRun);

        Assert.True(result);
        Assert.Contains("MyPluginType", _console.Output);
        Assert.Contains("would create", _console.Output);
        await _serviceMock.DidNotReceive().ExecuteAsync(Arg.Any<CreateRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_DryRun_NewPackage_DoesNotFlagUnrelatedCustomApiAsOrphan()
    {
        // snapshot.CustomApis is queried by publisher prefix only (not per-assembly) — a brand-new
        // package's dummy snapshot must not mistake a sibling project's live Custom API for orphaned.
        SetupAssembly();
        SetupPluginPackage();
        SetupPluginTypes();
        SetupSteps();

        var siblingTypeId = Guid.NewGuid();
        SetupCustomApis(new Entity("customapi", Guid.NewGuid())
        {
            ["uniquename"] = "abc_SiblingApi",
            ["plugintypeid"] = new EntityReference("plugintype", siblingTypeId)
        });

        var plugin = new PluginTypeMetadata("MyPluginType", "Ns.MyPluginType", [], [], false);
        var assemblies = new List<PluginAssemblyMetadata>
        {
            new("MyPlugin", "MyPlugin, Version=1.0.0.0", new byte[] { 9, 9, 9 }, "dll-hash-unused", "1.0.0.0", null, "neutral", [plugin])
        };

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, assemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution", RunMode.DryRun);

        Assert.True(result);
        Assert.DoesNotContain("abc_SiblingApi", _console.Output);
    }

    // -- Package path + Custom APIs this push does not own --
    //
    // snapshot.CustomApis is queried publisher-prefix-wide, so a package push sees every Custom API the
    // publisher ever created: sibling projects in the same push, other repos, anything. A Custom API
    // REFERENCES a plugin type as its implementation (customapi.plugintypeid points down), so an API
    // whose plugin type this push doesn't own is not evidence of an orphan — it is evidence the API
    // isn't ours. It is never deleted. These tests previously passed because the sibling's plugin type
    // ids were fed into the planner; they now pass because attribution is required at all.

    // Existing package, unchanged hash: the drift-correction path, where a plan's deletes execute for
    // real. One package project ("MyPlugin") and one classic sibling project ("SupportPlugins") whose
    // Custom API is live in the prefix-wide snapshot. Nothing tells the planner about the sibling's
    // plugin types — that input no longer exists.
    private Guid ArrangeMixedPushWithClassicSiblingCustomApi()
    {
        var assemblyId = Guid.NewGuid();
        var siblingTypeId = Guid.NewGuid();
        var siblingApiId = Guid.NewGuid();

        SetupAssembly(PackageOwnedAssembly(assemblyId, hash: NupkgHash));
        SetupPluginPackage(ExistingPluginPackage(Guid.NewGuid()));
        SetupPackageAssemblyByName(assemblyId, "MyPlugin");
        SetupPluginTypesForAssembly(assemblyId, new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "Ns.MyPluginType" });
        SetupStepsForAssembly(assemblyId);
        SetupCustomApis(new Entity("customapi", siblingApiId)
        {
            ["uniquename"] = "abc_SiblingApi",
            ["plugintypeid"] = new EntityReference("plugintype", siblingTypeId)
        });

        return siblingApiId;
    }

    private static List<PluginAssemblyMetadata> MixedPushPackageAssemblies() =>
    [
        new("MyPlugin", "MyPlugin, Version=1.0.0.0", [9, 9, 9], "dll-hash-unused", "1.0.0.0", null, "neutral",
            [new PluginTypeMetadata("MyPluginType", "Ns.MyPluginType", [], [])])
    ];

    [Fact]
    public async Task SyncSolutionFromPackageAsync_WithClassicSiblingInThePush_DoesNotDeleteItsCustomApi()
    {
        var siblingApiId = ArrangeMixedPushWithClassicSiblingCustomApi();

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, MixedPushPackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        Assert.False(result);
        await _serviceMock.DidNotReceive().DeleteAsync("customapi", siblingApiId, Arg.Any<CancellationToken>());
        Assert.DoesNotContain("abc_SiblingApi", _console.Output);
    }

    [Fact]
    public async Task ACustomApiThisPushDoesNotOwnIsNeverDeleted_EvenUnderForceDeleteOrphans()
    {
        // The invariant, stated as the test name because that is what gets read at failure time:
        // no attribution, no delete — and --force does not buy attribution. This is the case that
        // used to delete another project's live public API on an ordinary push.
        var foreignApiId = ArrangeMixedPushWithClassicSiblingCustomApi();

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, MixedPushPackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution",
            RunMode.Normal, forceDeleteOrphans: true);

        Assert.False(result);
        await _serviceMock.DidNotReceive().DeleteAsync("customapi", foreignApiId, Arg.Any<CancellationToken>());
    }

    // -- The package path runs the orphan passes too (a nupkg-only solution has no classic pass) --

    private Guid ArrangeUnchangedPackagePush(params string[] extraAssemblyNames)
    {
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId, hash: NupkgHash));
        SetupPluginPackage(ExistingPluginPackage(Guid.NewGuid()));
        SetupPackageAssemblyByName(assemblyId, "MyPlugin");
        SetupPluginTypesForAssembly(assemblyId, new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "Ns.MyPluginType" });
        SetupStepsForAssembly(assemblyId);
        foreach (var name in extraAssemblyNames)
            SetupPackageAssemblyByName(Guid.NewGuid(), name);
        return assemblyId;
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_ReportsOrphanAssemblies()
    {
        // Without this the only orphan pass lives on the classic path, so a solution whose every plugin
        // project packs to a .nupkg never hears about its orphans — and deploy can't cover it, because
        // deploy targets a downstream environment and `deploy dev` is rejected outright.
        ArrangeUnchangedPackagePush();
        SetupOrphanAssembly(Guid.NewGuid(), "Legacy");

        await _service.SyncSolutionFromPackageAsync(
            _serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        _console.Output.Should().Contain("Legacy.dll").And.Contain("--force delete-orphans");
    }

    // Stubs a package that still has an assembly the local .nupkg no longer carries, plus that
    // assembly's blocking children. The dropped-assembly query is the packageid-Equal one, which no
    // other stub in this class matches.
    private (Guid AssemblyId, Guid StepId, Guid ApiId) SetupDroppedPackageAssembly(Guid packageId, string droppedName)
    {
        var assemblyId = Guid.NewGuid();
        var typeId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var apiId = Guid.NewGuid();

        // Must NOT also match FindPackageAssemblyAsync, which filters by packageid *and* name — without
        // the name exclusion this stub wins that query too (most-recently-configured), and the reflected
        // assembly's snapshot loads against the dropped assembly's id.
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly"
                                          && q.Criteria.Conditions.Any(c => c.AttributeName == "packageid" && c.Operator == ConditionOperator.Equal)
                                          && !q.Criteria.Conditions.Any(c => c.AttributeName == "name"))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity>
            {
                new Entity("pluginassembly", assemblyId) { ["name"] = droppedName }
            })));

        SetupPluginTypesForAssembly(assemblyId, new Entity("plugintype", typeId) { ["typename"] = $"{droppedName}.Gone" });
        SetupStepsForAssembly(assemblyId, new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"] = $"{droppedName}.Gone: Update of contact",
            ["plugintypeid"] = new EntityReference("plugintype", typeId)
        });
        SetupCustomApis(new Entity("customapi", apiId)
        {
            ["uniquename"] = "abc_GoneApi",
            ["plugintypeid"] = new EntityReference("plugintype", typeId)
        });

        return (assemblyId, stepId, apiId);
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_AssemblyDroppedFromPackage_ClearsItsStepsBeforeTheContentUpdate()
    {
        // Dataverse rejects a content update that drops an assembly whose types still have steps.
        // KD4 clears that for a class removed from a surviving assembly, but an assembly that vanishes
        // from the .nupkg has no plan of its own, so nothing cleared its steps and the push died on the
        // package write.
        var packageId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId));
        SetupPluginPackage(ExistingPluginPackage(packageId));
        SetupPackageAssemblyByName(assemblyId, "MyPlugin");
        var dropped = SetupDroppedPackageAssembly(packageId, "GoneAssembly");

        await _service.SyncSolutionFromPackageAsync(
            _serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        await _serviceMock.Received(1).DeleteAsync("sdkmessageprocessingstep", dropped.StepId, Arg.Any<CancellationToken>());
        await _serviceMock.Received(1).DeleteAsync("customapi", dropped.ApiId, Arg.Any<CancellationToken>());
        // The plugin type and the assembly record are Dataverse's to remove via the content update —
        // deleting them here would be redundant work that the update already does.
        await _serviceMock.DidNotReceive().DeleteAsync("pluginassembly", dropped.AssemblyId, Arg.Any<CancellationToken>());
        _console.Output.Should().Contain("GoneAssembly.dll").And.Contain("no longer in the package");
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_AssemblyStillInThePackage_IsNotClearedAsDropped()
    {
        // The guard that keeps this from wiping the assembly the push is actually registering.
        var packageId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId));
        SetupPluginPackage(ExistingPluginPackage(packageId));
        SetupPackageAssemblyByName(assemblyId, "MyPlugin");
        var stillThere = SetupDroppedPackageAssembly(packageId, "MyPlugin"); // same name the push reflects

        await _service.SyncSolutionFromPackageAsync(
            _serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        await _serviceMock.DidNotReceive().DeleteAsync("sdkmessageprocessingstep", stillThere.StepId, Arg.Any<CancellationToken>());
        _console.Output.Should().NotContain("no longer in the package");
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_AssemblyDroppedFromPackage_ForeignCustomApiSurvives()
    {
        // R10: only Custom APIs naming one of the DROPPED assembly's own plugin types are cleared — one
        // sharing the publisher prefix but implemented by another assembly's plugin type must survive.
        var packageId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId));
        SetupPluginPackage(ExistingPluginPackage(packageId));
        SetupPackageAssemblyByName(assemblyId, "MyPlugin");

        var droppedAssemblyId = Guid.NewGuid();
        var droppedTypeId = Guid.NewGuid();
        var droppedApiId = Guid.NewGuid();
        var foreignApiId = Guid.NewGuid();

        // Same dropped-assembly stub shape as SetupDroppedPackageAssembly — packageid-Equal, no name
        // condition, so it doesn't also match FindPackageAssemblyAsync.
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly"
                                          && q.Criteria.Conditions.Any(c => c.AttributeName == "packageid" && c.Operator == ConditionOperator.Equal)
                                          && !q.Criteria.Conditions.Any(c => c.AttributeName == "name"))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(new List<Entity>
            {
                new Entity("pluginassembly", droppedAssemblyId) { ["name"] = "GoneAssembly" }
            })));

        SetupPluginTypesForAssembly(droppedAssemblyId, new Entity("plugintype", droppedTypeId) { ["typename"] = "GoneAssembly.Gone" });
        SetupStepsForAssembly(droppedAssemblyId);
        SetupCustomApis(
            new Entity("customapi", droppedApiId) { ["uniquename"] = "abc_GoneApi", ["plugintypeid"] = new EntityReference("plugintype", droppedTypeId) },
            new Entity("customapi", foreignApiId) { ["uniquename"] = "abc_ForeignApi", ["plugintypeid"] = new EntityReference("plugintype", Guid.NewGuid()) });

        await _service.SyncSolutionFromPackageAsync(
            _serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        await _serviceMock.Received(1).DeleteAsync("customapi", droppedApiId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("customapi", foreignApiId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_AssemblyDroppedFromPackage_NoDeleteMode_WarnsWithoutClearing()
    {
        // R12: under --no-delete, push must not clear a dropped assembly's registrations — it can only
        // warn that Dataverse will reject the update while they remain.
        var packageId = Guid.NewGuid();
        var assemblyId = Guid.NewGuid();
        SetupAssembly(PackageOwnedAssembly(assemblyId));
        SetupPluginPackage(ExistingPluginPackage(packageId));
        SetupPackageAssemblyByName(assemblyId, "MyPlugin");
        var dropped = SetupDroppedPackageAssembly(packageId, "GoneAssembly");

        await _service.SyncSolutionFromPackageAsync(
            _serviceMock, PackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution", RunMode.NoDelete);

        await _serviceMock.DidNotReceive().DeleteAsync("sdkmessageprocessingstep", dropped.StepId, Arg.Any<CancellationToken>());
        await _serviceMock.DidNotReceive().DeleteAsync("customapi", dropped.ApiId, Arg.Any<CancellationToken>());
        _console.Output.Should().Contain("GoneAssembly.dll").And.Contain("will reject the update");
    }

    // -- CompareAssemblySetAsync (U1: shared assembly-set pre-flight, KTD1) --
    // Exercised directly rather than only through the drop path, per the plan's verification note —
    // the drop-path tests above cover what happens to a dropped assembly's children, these cover the
    // comparison itself.

    // Excludes queries carrying a name condition for the same reason SetupDroppedPackageAssembly does:
    // FindPackageAssemblyAsync filters by packageid *and* name, and NSubstitute lets the
    // most-recently-configured match win. Without this, the broad stub shadows
    // SetupPackageAssemblyByName and hands the primary assembly's snapshot a placeholder entity.
    private void SetupRegisteredPackageAssemblies(Guid packageId, params string[] names)
    {
        var entities = names.Select(n => new Entity("pluginassembly", Guid.NewGuid()) { ["name"] = n }).ToList();
        _serviceMock.RetrieveMultipleAsync(
                Arg.Is(Matching<QueryExpression>(q => q.EntityName == "pluginassembly"
                    && q.Criteria.Conditions.Any(c => c.AttributeName == "packageid" && c.Operator == ConditionOperator.Equal && Equals(c.Values[0], packageId))
                    && !q.Criteria.Conditions.Any(c => c.AttributeName == "name"))),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new EntityCollection(entities)));
    }

    [Fact]
    public async Task CompareAssemblySetAsync_OneRegisteredTwoReflected_YieldsOneAddedZeroDropped()
    {
        var packageId = Guid.NewGuid();
        SetupRegisteredPackageAssemblies(packageId, "MyPlugin");

        var (added, dropped) = await PluginService.CompareAssemblySetAsync(
            _serviceMock, packageId, [PackageAssemblies("MyPlugin")[0], PackageAssemblies("Extra")[0]], CancellationToken.None);

        added.Select(a => a.Name).Should().BeEquivalentTo(["Extra"]);
        dropped.Should().BeEmpty();
    }

    [Fact]
    public async Task CompareAssemblySetAsync_TwoRegisteredOneReflected_YieldsZeroAddedOneDropped()
    {
        var packageId = Guid.NewGuid();
        SetupRegisteredPackageAssemblies(packageId, "MyPlugin", "Gone");

        var (added, dropped) = await PluginService.CompareAssemblySetAsync(
            _serviceMock, packageId, [PackageAssemblies("MyPlugin")[0]], CancellationToken.None);

        added.Should().BeEmpty();
        dropped.Select(e => e.GetAttributeValue<string>("name")).Should().BeEquivalentTo(["Gone"]);
    }

    [Fact]
    public async Task CompareAssemblySetAsync_AssemblyInBoth_IsNeitherAddedNorDropped()
    {
        var packageId = Guid.NewGuid();
        SetupRegisteredPackageAssemblies(packageId, "MyPlugin");

        var (added, dropped) = await PluginService.CompareAssemblySetAsync(
            _serviceMock, packageId, [PackageAssemblies("MyPlugin")[0]], CancellationToken.None);

        added.Should().BeEmpty();
        dropped.Should().BeEmpty();
    }

    [Fact]
    public async Task CompareAssemblySetAsync_NoExistingPackage_EveryReflectedIsAddedWithoutQuerying()
    {
        var (added, dropped) = await PluginService.CompareAssemblySetAsync(
            _serviceMock, null, [PackageAssemblies("MyPlugin")[0], PackageAssemblies("Extra")[0]], CancellationToken.None);

        added.Select(a => a.Name).Should().BeEquivalentTo(["MyPlugin", "Extra"]);
        dropped.Should().BeEmpty();
        await _serviceMock.DidNotReceive().RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_OrphanQueryExcludesEveryAssemblyInThePackage()
    {
        // KTD16 — the original reason this path skipped the orphan pass. A package's secondary assemblies
        // must never read as orphans of the very push registering them, and that must hold even when the
        // caller passes no pushed-name set at all, so the exclusion can't depend on the caller.
        ArrangeUnchangedPackagePush("Extra");
        List<PluginAssemblyMetadata> twoAssemblies =
        [
            new("MyPlugin", "MyPlugin, Version=1.0.0.0", [9, 9, 9], "dll-hash-unused", "1.0.0.0", null, "neutral", []),
            new("Extra", "Extra, Version=1.0.0.0", [9, 9, 9], "dll-hash-unused", "1.0.0.0", null, "neutral", []),
        ];

        await _service.SyncSolutionFromPackageAsync(
            _serviceMock, twoAssemblies, NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        var orphanQuery = _serviceMock.ReceivedCalls()
            .Select(c => c.GetArguments().OfType<QueryExpression>().FirstOrDefault())
            .OfType<QueryExpression>()
            .Single(q => q.EntityName == "pluginassembly"
                      && q.Criteria.Conditions.Any(c => c.Operator is ConditionOperator.NotEqual or ConditionOperator.NotIn));

        orphanQuery.Criteria.Conditions.Single().Values.Should().BeEquivalentTo(["MyPlugin", "Extra"]);
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_NeverQueriesPluginTypesByAssemblyName()
    {
        // The removed SiblingPluginTypeIdsAsync lookup: a plugintype query joined through pluginassembly.
        // Positive attribution needs no such widening query, and reintroducing one is the exact shape of
        // the mistake this design removes.
        ArrangeMixedPushWithClassicSiblingCustomApi();

        await _service.SyncSolutionFromPackageAsync(
            _serviceMock, MixedPushPackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        await _serviceMock.DidNotReceive().RetrieveMultipleAsync(
            Arg.Is(Matching<QueryExpression>(q => q.EntityName == "plugintype" && q.LinkEntities.Count > 0)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SyncSolutionFromPackageAsync_ChangedPackageWithClassicSibling_DoesNotDeleteItsCustomApi()
    {
        // The other half of the package path: content changed, so the pre-update plan (and the post-update
        // re-plan that actually executes) run instead of the steps-only path.
        var assemblyId = Guid.NewGuid();
        var siblingTypeId = Guid.NewGuid();
        var siblingApiId = Guid.NewGuid();

        SetupAssembly(PackageOwnedAssembly(assemblyId, hash: "stalehash"));
        SetupPluginPackage(ExistingPluginPackage(Guid.NewGuid()));
        SetupPackageAssemblyByName(assemblyId, "MyPlugin");
        SetupPluginTypesForAssembly(assemblyId, new Entity("plugintype", Guid.NewGuid()) { ["typename"] = "Ns.MyPluginType" });
        SetupStepsForAssembly(assemblyId);
        SetupCustomApis(new Entity("customapi", siblingApiId)
        {
            ["uniquename"] = "abc_SiblingApi",
            ["plugintypeid"] = new EntityReference("plugintype", siblingTypeId)
        });

        var result = await _service.SyncSolutionFromPackageAsync(
            _serviceMock, MixedPushPackageAssemblies(), NupkgBytes, "pkg.nupkg", "MyPlugin", "MySolution");

        Assert.True(result);
        await _serviceMock.DidNotReceive().DeleteAsync("customapi", siblingApiId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void SiblingAssemblyNames_WithEveryPackagedAssemblyManaged_ExcludesThemAll()
    {
        // The package path syncs N assemblies in one pass, so "what this pass owns" is a set — passing
        // only the primary would read the package's own secondary assemblies as siblings.
        PluginService.SiblingAssemblyNames(["Sales", "Sales.Shared"], ["Sales", "Sales.Shared", "Support"])
                     .Should().BeEquivalentTo(["Support"]);
    }

    // -- WritePackageAssemblyMarkerAsync (standalone marker write, part of R6) --

    [Fact]
    public async Task WritePackageAssemblyMarkerAsync_UpdatesDescriptionAndIncludesVersionInSameCall()
    {
        var assemblyId = Guid.NewGuid();
        var assembly = new Entity("pluginassembly", assemblyId) { ["version"] = "1.2.3.4" };

        await _service.WritePackageAssemblyMarkerAsync(_serviceMock, assembly, "newhash123");

        await _serviceMock.Received(1).UpdateAsync(Arg.Is(Matching<Entity>(e =>
            e.LogicalName == "pluginassembly" &&
            e.Id == assemblyId &&
            e.GetAttributeValue<string>("description") == "[flowline] sha256=newhash123" &&
            e.GetAttributeValue<string>("version") == "1.2.3.4" &&
            !e.Contains("content")
        )), Arg.Any<CancellationToken>());
    }

    // -- AnalyzeAssembly exception routing (dllPath overload only — goes through the real reflection
    // pipeline, unlike every other test above which hands PluginService a pre-built PluginAssemblyMetadata) --

    [Fact]
    public async Task SyncAssemblyOnlyAsync_InvalidCustomApiUniqueNameFormat_ThrowsFlowlineExceptionWithOriginalMessage()
    {
        var dir = Directory.CreateTempSubdirectory("flowline-plugin-service-test-").FullName;
        try
        {
            var dllPath = BuildPluginDllWithBadCustomApiUniqueName(dir, "BadCustomApiAssembly", "BadUniqueNameApi", "NoUnderscoreAtAll");

            var ex = await Assert.ThrowsAsync<FlowlineException>(() =>
                _service.SyncAssemblyOnlyAsync(_serviceMock, dllPath, "MySolution"));

            Assert.Equal(ExitCode.ValidationFailed, ex.ExitCode);
            Assert.Contains("[CustomApi] UniqueName 'NoUnderscoreAtAll'", ex.Message);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // Builds a minimal real assembly on disk with one public class implementing IPlugin, decorated with
    // [CustomApi(UniqueName = badUniqueName)] — used to drive an actual InvalidOperationException out of
    // PluginTypeMetadataScanner.ValidateCustomApiUniqueNameFormat through the real Analyze() reflection
    // pipeline, so the AnalyzeAssembly rewrap is proven end-to-end rather than assumed from the source.
    // Microsoft.Xrm.Sdk.dll and Flowline.Attributes.dll are copied next to the built DLL so Analyze()'s
    // PathAssemblyResolver (which scans the directory containing dllPath) can resolve IPlugin and
    // CustomApiAttribute — mirroring PluginAssemblyReaderTests' CopyXrmSdkDllNextTo pattern.
    static string BuildPluginDllWithBadCustomApiUniqueName(string dir, string assemblyName, string pluginTypeName, string badUniqueName)
    {
        var ab = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
        var mb = ab.DefineDynamicModule("MainModule");

        var pluginTb = mb.DefineType(pluginTypeName, TypeAttributes.Public | TypeAttributes.Class, typeof(object), [typeof(IPlugin)]);
        var executeMethod = typeof(IPlugin).GetMethod(nameof(IPlugin.Execute))!;
        var methodBuilder = pluginTb.DefineMethod(nameof(IPlugin.Execute),
            MethodAttributes.Public | MethodAttributes.Virtual, typeof(void), [typeof(IServiceProvider)]);
        methodBuilder.GetILGenerator().Emit(OpCodes.Ret);
        pluginTb.DefineMethodOverride(methodBuilder, executeMethod);

        var customApiCtor = typeof(CustomApiAttribute).GetConstructor(Type.EmptyTypes)!;
        var uniqueNameProp = typeof(CustomApiAttribute).GetProperty(nameof(CustomApiAttribute.UniqueName))!;
        var attrBuilder = new CustomAttributeBuilder(customApiCtor, [], [uniqueNameProp], [badUniqueName]);
        pluginTb.SetCustomAttribute(attrBuilder);

        pluginTb.CreateType();

        var xrmSdkDir = Path.GetDirectoryName(typeof(IPlugin).Assembly.Location)!;
        File.Copy(Path.Combine(xrmSdkDir, "Microsoft.Xrm.Sdk.dll"), Path.Combine(dir, "Microsoft.Xrm.Sdk.dll"), overwrite: true);
        var attributesDir = Path.GetDirectoryName(typeof(CustomApiAttribute).Assembly.Location)!;
        File.Copy(Path.Combine(attributesDir, "Flowline.Attributes.dll"), Path.Combine(dir, "Flowline.Attributes.dll"), overwrite: true);

        var path = Path.Combine(dir, $"{assemblyName}.dll");
        ab.Save(path);
        return path;
    }

    // The regression these three guard: hashing the .nupkg file's own bytes made the package path
    // re-upload on every push, because NuGet rewrites the container on every pack even when nothing
    // recompiled. A byte-level assertion can't catch that — only a hash that ignores the container can.
    [Fact]
    public void ComputeNupkgPayloadHash_ContainerRewrittenPayloadUnchanged_ReturnsSameHash()
    {
        var dllA = new byte[] { 1, 2, 3 };
        var dllB = new byte[] { 4, 5, 6 };

        // Same lib/ payload, every piece of container metadata NuGet regenerates per pack differs:
        // psmdcp entry name (a fresh GUID each time), entry timestamps, nuspec version and commit,
        // plus the zip's entry ordering.
        var first = BuildNupkg("e232950058a2411e9a6e612767e508fa", new DateTimeOffset(2026, 7, 29, 15, 24, 9, TimeSpan.Zero), "0.0.0-alpha.0.2",
            ("lib/net462/A.dll", dllA), ("lib/net462/B.dll", dllB));
        var second = BuildNupkg("040c161ca1674247b7b108de80bfd68b", new DateTimeOffset(2026, 8, 8, 17, 18, 16, TimeSpan.Zero), "0.0.0-alpha.0.4",
            ("lib/net462/B.dll", dllB), ("lib/net462/A.dll", dllA));

        first.Should().NotEqual(second, "the two packages must differ at the byte level or the test proves nothing");
        PluginService.ComputeNupkgPayloadHash(first).Should().Be(PluginService.ComputeNupkgPayloadHash(second));
    }

    [Fact]
    public void ComputeNupkgPayloadHash_LibContentChanged_ReturnsDifferentHash()
    {
        var stamp = new DateTimeOffset(2026, 8, 8, 17, 18, 16, TimeSpan.Zero);
        var before = BuildNupkg("abc", stamp, "1.0.0", ("lib/net462/A.dll", [1, 2, 3]));
        var after = BuildNupkg("abc", stamp, "1.0.0", ("lib/net462/A.dll", [1, 2, 4]));

        PluginService.ComputeNupkgPayloadHash(before).Should().NotBe(PluginService.ComputeNupkgPayloadHash(after));
    }

    [Fact]
    public void ComputeNupkgPayloadHash_LibEntryRenamed_ReturnsDifferentHash()
    {
        var stamp = new DateTimeOffset(2026, 8, 8, 17, 18, 16, TimeSpan.Zero);
        var before = BuildNupkg("abc", stamp, "1.0.0", ("lib/net462/A.dll", [1, 2, 3]));
        var after = BuildNupkg("abc", stamp, "1.0.0", ("lib/net462/Renamed.dll", [1, 2, 3]));

        PluginService.ComputeNupkgPayloadHash(before).Should().NotBe(PluginService.ComputeNupkgPayloadHash(after));
    }

    // Mirrors the real .nupkg layout observed from `dotnet pack`: container metadata entries plus a
    // lib/<tfm>/ payload. Only the parts that vary per pack are parameterised.
    static byte[] BuildNupkg(string psmdcpGuid, DateTimeOffset stamp, string nuspecVersion, params (string Name, byte[] Content)[] libEntries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] content)
            {
                var entry = zip.CreateEntry(name);
                entry.LastWriteTime = stamp;
                using var stream = entry.Open();
                stream.Write(content);
            }

            Add("_rels/.rels", "rels"u8.ToArray());
            Add("Pkg.nuspec", Encoding.UTF8.GetBytes($"<version>{nuspecVersion}</version>"));
            Add("[Content_Types].xml", "types"u8.ToArray());
            Add($"package/services/metadata/core-properties/{psmdcpGuid}.psmdcp", "props"u8.ToArray());
            foreach (var (name, content) in libEntries)
                Add(name, content);
        }

        return ms.ToArray();
    }
}
