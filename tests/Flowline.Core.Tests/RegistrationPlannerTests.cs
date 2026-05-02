using Microsoft.Xrm.Sdk;
using NSubstitute;
using Flowline.Core.Services;
using Flowline.Core.Models;

namespace Flowline.Core.Tests;

public class RegistrationPlannerTests
{
    private readonly IFlowlineOutput _outputMock = Substitute.For<IFlowlineOutput>();
    private readonly RegistrationPlanner _planner;
    private readonly Entity _assembly;

    public RegistrationPlannerTests()
    {
        _planner  = new RegistrationPlanner(_outputMock);
        _assembly = new Entity("pluginassembly", Guid.NewGuid()) { ["name"] = "MyPlugin" };
    }

    static RegistrationSnapshot Snapshot(
        Dictionary<string, Entity>? pluginTypes = null,
        List<Entity>? steps              = null,
        List<Entity>? images             = null,
        List<Entity>? customApis         = null,
        List<Entity>? requestParams      = null,
        List<Entity>? responseProps      = null,
        Dictionary<string, Guid>? messageIds = null,
        Dictionary<(Guid, string?, string?), Guid?>? filterIds = null,
        string prefix = "abc") => new(
            pluginTypes  ?? new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase),
            steps        ?? [],
            images       ?? [],
            customApis   ?? [],
            requestParams ?? [],
            responseProps ?? [],
            messageIds   ?? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase),
            filterIds    ?? new Dictionary<(Guid, string?, string?), Guid?>(),
            prefix);

    static PluginAssemblyMetadata Metadata(params PluginTypeMetadata[] plugins) =>
        new("MyPlugin", "MyPlugin, Version=1.0.0.0", [1], "hash", "1.0.0.0", null, "neutral", [..plugins]);

    // -- Plugin type planning --

    [Fact]
    public void Plan_NewPluginType_CreatesUpsert()
    {
        var plan = _planner.Plan(Snapshot(), Metadata(
            new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], null, false)), _assembly, "MySolution");

        var (key, action) = Assert.Single(plan.PluginTypes.Upserts);
        Assert.Equal("MyPlugin", key);
        Assert.True(action.IsCreate);
        Assert.Equal("MyNamespace.MyPlugin", action.Entity.GetAttributeValue<string>("typename"));
    }

    [Fact]
    public void Plan_NewWorkflowType_SetsWorkflowActivityGroupName()
    {
        var plan = _planner.Plan(Snapshot(), Metadata(
            new PluginTypeMetadata("MyActivity", "MyNamespace.MyActivity", [], null, IsWorkflow: true)), _assembly, "MySolution");

        var (_, action) = Assert.Single(plan.PluginTypes.Upserts);
        Assert.Equal("MyPlugin (1.0.0.0)", action.Entity.GetAttributeValue<string>("workflowactivitygroupname"));
    }

    [Fact]
    public void Plan_ExistingPluginType_NoUpsert()
    {
        var typeId = Guid.NewGuid();
        var snapshot = Snapshot(pluginTypes: new(StringComparer.OrdinalIgnoreCase)
        {
            ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
        });

        var plan = _planner.Plan(snapshot, Metadata(
            new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], null, false)), _assembly, "MySolution");

        Assert.Empty(plan.PluginTypes.Upserts);
    }

    [Fact]
    public void Plan_ObsoletePluginType_CreatesDelete()
    {
        var typeId = Guid.NewGuid();
        var snapshot = Snapshot(pluginTypes: new(StringComparer.OrdinalIgnoreCase)
        {
            ["Obsolete.Plugin"] = new Entity("plugintype", typeId) { ["typename"] = "Obsolete.Plugin", ["isworkflowactivity"] = false }
        });

        var plan = _planner.Plan(snapshot, Metadata(), _assembly, "MySolution");

        Assert.True(plan.PluginTypes.Deletes.ContainsKey("Obsolete.Plugin"));
        Assert.Equal(typeId, plan.PluginTypes.Deletes["Obsolete.Plugin"].Id);
    }

    [Fact]
    public void Plan_ObsoleteWorkflowType_DeletesTypeOnly()
    {
        var typeId = Guid.NewGuid();
        var snapshot = Snapshot(pluginTypes: new(StringComparer.OrdinalIgnoreCase)
        {
            ["Obsolete.Activity"] = new Entity("plugintype", typeId) { ["typename"] = "Obsolete.Activity", ["isworkflowactivity"] = true }
        });

        var plan = _planner.Plan(snapshot, Metadata(), _assembly, "MySolution");

        Assert.True(plan.PluginTypes.Deletes.ContainsKey("Obsolete.Activity"));
        Assert.Empty(plan.Steps.Deletes);
        Assert.Empty(plan.CustomApis.Deletes);
    }

    // -- Step planning --

    [Fact]
    public void Plan_NewStep_CreatesUpsertWithSolutionName()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var snapshot  = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId });

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of account", "Update", "account", 20, 0, 1, null, null, [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], null, false)), _assembly, "MySolution");

        Assert.True(plan.Steps.Upserts.ContainsKey(step.Name));
        var action = plan.Steps.Upserts[step.Name];
        Assert.True(action.IsCreate);
        Assert.Equal("MySolution", action.SolutionName);
    }

    [Fact]
    public void Plan_ExistingUnchangedStep_OnlyAddsToSolution()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var stepId    = Guid.NewGuid();
        const string stepName = "MyNamespace.MyPlugin: Update of account";

        var existingStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"]                = stepName,
            ["plugintypeid"]        = new EntityReference("plugintype", typeId),
            ["stage"]               = new OptionSetValue(20),
            ["mode"]                = new OptionSetValue(0),
            ["rank"]                = 1,
            ["filteringattributes"] = (string?)null,
            ["configuration"]       = (string?)null,
            ["sdkmessageid"]        = new EntityReference("sdkmessage", messageId)
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId });

        var step = new PluginStepMetadata(stepName, "Update", "account", 20, 0, 1, null, null, [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], null, false)), _assembly, "MySolution");

        Assert.Empty(plan.Steps.Upserts);
        Assert.True(plan.Steps.AddSolutionComponents.ContainsKey(stepName));
    }

    [Fact]
    public void Plan_ExistingChangedStep_CreatesUpdate()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var stepId    = Guid.NewGuid();
        const string stepName = "MyNamespace.MyPlugin: Update of account";

        var existingStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"]         = stepName,
            ["plugintypeid"] = new EntityReference("plugintype", typeId),
            ["stage"]        = new OptionSetValue(10), // pre-validation; will change to 20
            ["mode"]         = new OptionSetValue(0),
            ["rank"]         = 1,
            ["sdkmessageid"] = new EntityReference("sdkmessage", messageId)
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId });

        var step = new PluginStepMetadata(stepName, "Update", "account", 20, 0, 1, null, null, [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], null, false)), _assembly, "MySolution");

        Assert.True(plan.Steps.Upserts.ContainsKey(stepName));
        Assert.False(plan.Steps.Upserts[stepName].IsCreate);
    }

    [Fact]
    public void Plan_ObsoleteStep_CreatesDelete()
    {
        var typeId  = Guid.NewGuid();
        var stepId  = Guid.NewGuid();
        const string stepName = "Orphaned.Step";

        var obsoleteStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"]         = stepName,
            ["plugintypeid"] = new EntityReference("plugintype", typeId)
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [obsoleteStep]);

        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], null, false)), _assembly, "MySolution");

        Assert.True(plan.Steps.Deletes.ContainsKey(stepName));
        Assert.Equal(stepId, plan.Steps.Deletes[stepName].Id);
    }

    // -- asyncautodelete / DeleteJobOnSuccess --

    [Fact]
    public void Plan_AsyncStep_WithDeleteJobOnSuccess_DetectsChange()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var stepId    = Guid.NewGuid();
        const string stepName = "MyNamespace.MyPlugin: Update of account";

        var existingStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"]             = stepName,
            ["plugintypeid"]     = new EntityReference("plugintype", typeId),
            ["stage"]            = new OptionSetValue(40),
            ["mode"]             = new OptionSetValue(1),
            ["rank"]             = 1,
            ["sdkmessageid"]     = new EntityReference("sdkmessage", messageId),
            ["asyncautodelete"]  = false   // currently false in Dataverse
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId });

        // Assembly now has DeleteJobOnSuccess = true → AsyncAutoDelete = true
        var step = new PluginStepMetadata(stepName, "Update", "account", 40, 1, 1, null, null, [], [], AsyncAutoDelete: true);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], null, false)), _assembly, "MySolution");

        Assert.True(plan.Steps.Upserts.ContainsKey(stepName));
        Assert.False(plan.Steps.Upserts[stepName].IsCreate);
        Assert.True(plan.Steps.Upserts[stepName].Entity.GetAttributeValue<bool>("asyncautodelete"));
    }

    [Fact]
    public void Plan_NewAsyncStep_WithDeleteJobOnSuccess_SetsFieldOnCreate()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var snapshot  = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId });

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of account", "Update", "account", 40, 1, 1, null, null, [], [], AsyncAutoDelete: true);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], null, false)), _assembly, "MySolution");

        Assert.True(plan.Steps.Upserts.ContainsKey(step.Name));
        Assert.True(plan.Steps.Upserts[step.Name].IsCreate);
        Assert.True(plan.Steps.Upserts[step.Name].Entity.GetAttributeValue<bool>("asyncautodelete"));
    }

    // -- Custom API obsolete-detection bug fix --

    [Fact]
    public void Plan_CustomApiWithMatchingPrefix_NotTreatedAsObsolete()
    {
        // Regression: before the fix, "abc_MyApi" was compared against "MyApi" (without prefix),
        // so every registered API looked obsolete and was deleted then recreated on every push.
        var typeId = Guid.NewGuid();
        var apiId  = Guid.NewGuid();

        var existingApi = new Entity("customapi", apiId)
        {
            ["uniquename"]                      = "abc_MyApi",
            ["displayname"]                     = "My Api",
            ["description"]                     = "desc",
            ["bindingtype"]                     = new OptionSetValue(0),
            ["isfunction"]                      = false,
            ["isprivate"]                       = false,
            ["allowedcustomprocessingsteptype"] = new OptionSetValue(0),
            ["plugintypeid"]                    = new EntityReference("plugintype", typeId)
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            customApis: [existingApi],
            prefix: "abc");

        var customApi = new CustomApiMetadata("MyApi", "My Api", "desc", 0, null, false, false, 0, null, "MyNamespace.MyPlugin", [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], [customApi], false, IsCustomApi: true)), _assembly, "MySolution");

        Assert.Empty(plan.CustomApis.Deletes);
    }

    [Fact]
    public void Plan_CustomApiAbsentFromAssembly_IsObsolete()
    {
        var typeId = Guid.NewGuid();
        var apiId  = Guid.NewGuid();

        var obsoleteApi = new Entity("customapi", apiId)
        {
            ["uniquename"]                      = "abc_ObsoleteApi",
            ["displayname"]                     = "Obsolete Api",
            ["bindingtype"]                     = new OptionSetValue(0),
            ["isfunction"]                      = false,
            ["allowedcustomprocessingsteptype"] = new OptionSetValue(0),
            ["plugintypeid"]                    = new EntityReference("plugintype", typeId)
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            customApis: [obsoleteApi],
            prefix: "abc");

        // Assembly has no Custom APIs — obsolete one should be deleted
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], [], false, IsCustomApi: true)), _assembly, "MySolution");

        Assert.True(plan.CustomApis.Deletes.ContainsKey("abc_ObsoleteApi"));
        Assert.Equal(apiId, plan.CustomApis.Deletes["abc_ObsoleteApi"].Id);
    }

    [Fact]
    public void Plan_NewCustomApi_CreatesUpsertWithSolutionName()
    {
        var typeId = Guid.NewGuid();
        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            prefix: "abc");

        var customApi = new CustomApiMetadata("MyApi", "My Api", "desc", 0, null, false, false, 0, null, "MyNamespace.MyPlugin", [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], [customApi], false, IsCustomApi: true)), _assembly, "MySolution");

        Assert.True(plan.CustomApis.Upserts.ContainsKey("MyApi"));
        var action = plan.CustomApis.Upserts["MyApi"];
        Assert.True(action.IsCreate);
        Assert.Equal("MySolution", action.SolutionName);
        Assert.Equal("abc_MyApi", action.Entity.GetAttributeValue<string>("uniquename"));
    }
}
