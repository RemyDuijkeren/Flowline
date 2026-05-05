using Microsoft.Xrm.Sdk;
using NSubstitute;
using Flowline.Core.Services;
using Flowline.Core.Models;
using Flowline.Core;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests;

public class PluginPlannerTests
{
    private readonly TestConsole _console = new();
    private readonly FlowlineRuntimeOptions _runtimeOptions = new();
    private readonly PluginPlanner _planner;
    private readonly Entity _assembly;

    public PluginPlannerTests()
    {
        _planner  = new PluginPlanner(_console, _runtimeOptions);
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
        HashSet<Guid>? systemUserIds = null,
        string prefix = "abc",
        Dictionary<Guid, IReadOnlyList<string>>? componentMembership = null,
        Dictionary<Guid, int>? componentTypeById = null) => new(
            pluginTypes  ?? new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase),
            steps        ?? [],
            images       ?? [],
            customApis   ?? [],
            requestParams ?? [],
            responseProps ?? [],
            messageIds   ?? new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase),
            filterIds    ?? DefaultFilterIds(messageIds),
            systemUserIds ?? [],
            prefix,
            componentMembership ?? new Dictionary<Guid, IReadOnlyList<string>>(),
            componentTypeById   ?? new Dictionary<Guid, int>());

    static Dictionary<(Guid, string?, string?), Guid?> DefaultFilterIds(Dictionary<string, Guid>? messageIds)
    {
        var result = new Dictionary<(Guid, string?, string?), Guid?>();
        if (messageIds == null)
            return result;

        foreach (var messageId in messageIds.Values)
        {
            result[(messageId, "account", null)] = Guid.NewGuid();
            result[(messageId, "contact", null)] = Guid.NewGuid();
            result[(messageId, "lead", null)] = Guid.NewGuid();
        }

        return result;
    }

    static PluginAssemblyMetadata Metadata(params PluginTypeMetadata[] plugins) =>
        new("MyPlugin", "MyPlugin, Version=1.0.0.0", [1], "hash", "1.0.0.0", null, "neutral", [..plugins]);

    // -- Plugin type planning --

    [Fact]
    public void Plan_NewPluginType_CreatesUpsert()
    {
        var plan = _planner.Plan(Snapshot(), Metadata(
            new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], [], false)), _assembly, "MySolution");

        var action = Assert.Single(plan.PluginTypes.Upserts);
        Assert.Equal("MyPlugin", action.Name);
        Assert.True(action.IsCreate);
        Assert.Equal("MyNamespace.MyPlugin", action.Entity.GetAttributeValue<string>("typename"));
    }

    [Fact]
    public void Plan_NewWorkflowType_SetsWorkflowActivityGroupName()
    {
        var plan = _planner.Plan(Snapshot(), Metadata(
            new PluginTypeMetadata("MyActivity", "MyNamespace.MyActivity", [], [], IsWorkflow: true)), _assembly, "MySolution");

        var action = Assert.Single(plan.PluginTypes.Upserts);
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
            new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [],[], false)), _assembly, "MySolution");

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

        Assert.Contains(plan.PluginTypes.Deletes, a => a.Name == "Obsolete.Plugin");
        Assert.Equal(typeId, plan.PluginTypes.Deletes.Single(a => a.Name == "Obsolete.Plugin").Id);
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

        Assert.Contains(plan.PluginTypes.Deletes, a => a.Name == "Obsolete.Activity");
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
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.Contains(plan.Steps.Upserts, a => a.Name == step.Name);
        var action = plan.Steps.Upserts.Single(a => a.Name == step.Name);
        Assert.True(action.IsCreate);
        Assert.Equal("MySolution", action.SolutionName);
    }

    [Fact]
    public void Plan_InvalidMessage_ThrowsClearException()
    {
        var typeId = Guid.NewGuid();
        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            messageIds: new(StringComparer.OrdinalIgnoreCase));

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Foo of account", "Foo", "account", 20, 0, 1, null, null, [], []);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution"));

        Assert.Contains("Foo", ex.Message);
        Assert.Contains("MyNamespace.MyPlugin", ex.Message);
        Assert.Contains("[Step]", ex.Message);
    }

    [Fact]
    public void Plan_InvalidEntityOnCreate_ThrowsClearException()
    {
        var typeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: []);

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of acount", "Update", "acount", 20, 0, 1, null, null, [], []);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution"));

        Assert.Contains("acount", ex.Message);
        Assert.Contains("Update", ex.Message);
        Assert.Contains("MyNamespace.MyPlugin", ex.Message);
    }

    [Fact]
    public void Plan_InvalidEntityOnUpdate_ThrowsClearException()
    {
        var typeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        const string stepName = "MyNamespace.MyPlugin: Update of acount";
        var existingStep = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["name"] = stepName,
            ["plugintypeid"] = new EntityReference("plugintype", typeId),
            ["sdkmessageid"] = new EntityReference("sdkmessage", messageId)
        };
        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: []);

        var step = new PluginStepMetadata(stepName, "Update", "acount", 20, 0, 1, null, null, [], []);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution"));

        Assert.Contains("acount", ex.Message);
        Assert.Contains("Update", ex.Message);
    }

    [Fact]
    public void Plan_ValidEntity_SetsSdkMessageFilterId()
    {
        var typeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId = Guid.NewGuid();
        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId });

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of account", "Update", "account", 20, 0, 1, null, null, [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.Equal(filterId, plan.Steps.Upserts.Single(a => a.Name == step.Name).Entity.GetAttributeValue<EntityReference>("sdkmessagefilterid").Id);
    }

    [Fact]
    public void Plan_AllEntitiesNull_DoesNotRequireFilter()
    {
        var typeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Create"] = messageId },
            filterIds: []);

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Create of any", "Create", null, 20, 0, 1, null, null, [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.False(plan.Steps.Upserts.Single(a => a.Name == step.Name).Entity.Contains("sdkmessagefilterid"));
    }

    [Fact]
    public void Plan_AllEntitiesNone_DoesNotRequireFilter()
    {
        var typeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Create"] = messageId },
            filterIds: []);

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Create of none", "Create", "none", 20, 0, 1, null, null, [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.False(plan.Steps.Upserts.Single(a => a.Name == step.Name).Entity.Contains("sdkmessagefilterid"));
    }

    [Fact]
    public void Plan_UnsupportedMessageForImage_ThrowsClearException()
    {
        var typeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Retrieve"] = messageId });

        var image = new PluginImageMetadata("Pre Image", "preimage", 0, "name");
        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Retrieve of any", "Retrieve", null, 20, 0, 1, null, null, [image], []);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution"));

        Assert.Contains("Pre Image", ex.Message);
        Assert.Contains("Retrieve", ex.Message);
        Assert.Contains("Supported messages", ex.Message);
        Assert.Contains("Update", ex.Message);
    }

    [Fact]
    public void Plan_ExistingUnchangedStep_OnlyAddsToSolution()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId  = Guid.NewGuid();
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
            ["sdkmessageid"]        = new EntityReference("sdkmessage", messageId),
            ["sdkmessagefilterid"]  = new EntityReference("sdkmessagefilter", filterId)
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId });

        var step = new PluginStepMetadata(stepName, "Update", "account", 20, 0, 1, null, null, [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.Empty(plan.Steps.Upserts);
        Assert.Contains(plan.Steps.AddSolutionComponents, a => a.Name == stepName);
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
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.Contains(plan.Steps.Upserts, a => a.Name == stepName);
        Assert.False(plan.Steps.Upserts.Single(a => a.Name == stepName).IsCreate);
    }

    [Fact]
    public void Plan_UpdatedStepInOtherSolution_AddsWarning()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var stepId    = Guid.NewGuid();
        const string stepName = "MyNamespace.MyPlugin: Update of account";

        var existingStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"]         = stepName,
            ["plugintypeid"] = new EntityReference("plugintype", typeId),
            ["stage"]        = new OptionSetValue(10), // will change to 20
            ["mode"]         = new OptionSetValue(0),
            ["rank"]         = 1,
            ["sdkmessageid"] = new EntityReference("sdkmessage", messageId)
        };

        var membership = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [stepId] = ["MySolution", "OtherSolution"]
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            componentMembership: membership);

        var step = new PluginStepMetadata(stepName, "Update", "account", 20, 0, 1, null, null, [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.Single(plan.Warnings);
        Assert.Contains("OtherSolution", plan.Warnings[0]);
        Assert.Contains(stepName, plan.Warnings[0]);
    }

    [Fact]
    public void Plan_UpdatedStepInCurrentSolutionOnly_NoWarning()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var stepId    = Guid.NewGuid();
        const string stepName = "MyNamespace.MyPlugin: Update of account";

        var existingStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"]         = stepName,
            ["plugintypeid"] = new EntityReference("plugintype", typeId),
            ["stage"]        = new OptionSetValue(10),
            ["mode"]         = new OptionSetValue(0),
            ["rank"]         = 1,
            ["sdkmessageid"] = new EntityReference("sdkmessage", messageId)
        };

        var membership = new Dictionary<Guid, IReadOnlyList<string>>
        {
            [stepId] = ["MySolution", "Default"]
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            componentMembership: membership);

        var step = new PluginStepMetadata(stepName, "Update", "account", 20, 0, 1, null, null, [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.Empty(plan.Warnings);
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

        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], [], false)), _assembly, "MySolution");

        Assert.Contains(plan.Steps.Deletes, a => a.Name == stepName);
        Assert.Equal(stepId, plan.Steps.Deletes.Single(a => a.Name == stepName).Id);
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
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.Contains(plan.Steps.Upserts, a => a.Name == stepName);
        Assert.False(plan.Steps.Upserts.Single(a => a.Name == stepName).IsCreate);
        Assert.True(plan.Steps.Upserts.Single(a => a.Name == stepName).Entity.GetAttributeValue<bool>("asyncautodelete"));
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
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.Contains(plan.Steps.Upserts, a => a.Name == step.Name);
        Assert.True(plan.Steps.Upserts.Single(a => a.Name == step.Name).IsCreate);
        Assert.True(plan.Steps.Upserts.Single(a => a.Name == step.Name).Entity.GetAttributeValue<bool>("asyncautodelete"));
    }

    // -- RunAs / impersonatinguserid --

    [Fact]
    public void Plan_ExistingStep_WithRunAsChange_DetectsChange()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var stepId    = Guid.NewGuid();
        var userId    = Guid.NewGuid();
        const string stepName = "MyNamespace.MyPlugin: Update of account";

        var existingStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"]                 = stepName,
            ["plugintypeid"]         = new EntityReference("plugintype", typeId),
            ["stage"]                = new OptionSetValue(20),
            ["mode"]                 = new OptionSetValue(0),
            ["rank"]                 = 1,
            ["sdkmessageid"]         = new EntityReference("sdkmessage", messageId),
            ["impersonatinguserid"]  = (EntityReference?)null   // no impersonation currently
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            systemUserIds: [userId]);

        var step = new PluginStepMetadata(stepName, "Update", "account", 20, 0, 1, null, null, [], [], RunAs: userId);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.Contains(plan.Steps.Upserts, a => a.Name == stepName);
        Assert.False(plan.Steps.Upserts.Single(a => a.Name == stepName).IsCreate);
        Assert.Equal(userId, plan.Steps.Upserts.Single(a => a.Name == stepName).Entity.GetAttributeValue<EntityReference>("impersonatinguserid").Id);
    }

    [Fact]
    public void Plan_NewStep_WithRunAs_SetsImpersonatingUserid()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var userId    = Guid.NewGuid();
        const string stepName = "MyNamespace.MyPlugin: Update of account";

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            systemUserIds: [userId]);

        var step = new PluginStepMetadata(stepName, "Update", "account", 20, 0, 1, null, null, [], [], RunAs: userId);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.Contains(plan.Steps.Upserts, a => a.Name == stepName);
        Assert.True(plan.Steps.Upserts.Single(a => a.Name == stepName).IsCreate);
        Assert.Equal(userId, plan.Steps.Upserts.Single(a => a.Name == stepName).Entity.GetAttributeValue<EntityReference>("impersonatinguserid").Id);
    }

    [Fact]
    public void Plan_NewStep_WithMissingRunAsUser_ThrowsClearException()
    {
        var typeId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        const string stepName = "MyNamespace.MyPlugin: Update of account";

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId });

        var step = new PluginStepMetadata(stepName, "Update", "account", 20, 0, 1, null, null, [], [], RunAs: userId);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution"));

        Assert.Contains(stepName, ex.Message);
        Assert.Contains(userId.ToString(), ex.Message);
        Assert.Contains("system user", ex.Message);
        Assert.Contains("RunAs", ex.Message);
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
            prefix: "abc",
            componentTypeById: new Dictionary<Guid, int> { [apiId] = 400 });

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

        Assert.Contains(plan.CustomApis.Deletes, a => a.Name == "abc_ObsoleteApi");
        Assert.Equal(apiId, plan.CustomApis.Deletes.Single(a => a.Name == "abc_ObsoleteApi").Id);
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

        Assert.Contains(plan.CustomApis.Upserts, a => a.Name == "MyApi");
        var action = plan.CustomApis.Upserts.Single(a => a.Name == "MyApi");
        Assert.True(action.IsCreate);
        Assert.Equal("MySolution", action.SolutionName);
        Assert.Equal("abc_MyApi", action.Entity.GetAttributeValue<string>("uniquename"));
    }
}
