using Microsoft.Xrm.Sdk;
using NSubstitute;
using Flowline.Core.Services;
using Flowline.Core.Models;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests;

public class PluginPlannerTests
{
    private readonly TestConsole _console = new();
    private readonly PluginPlanner _planner;
    private readonly Entity _assembly;

    public PluginPlannerTests()
    {
        _planner  = new PluginPlanner(_console);
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
        Assert.Equal("MySolution", action.SolutionName);
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
    public void Plan_ObsoletePluginTypeWithProtectedStep_NotDeleted()
    {
        var typeId = Guid.NewGuid();
        var protectedStep = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["name"]                                  = "Orphaned.Step",
            ["plugintypeid"]                           = new EntityReference("plugintype", typeId),
            ["sdkmessageprocessingstepsecureconfigid"] = new EntityReference("sdkmessageprocessingstepsecureconfig", Guid.NewGuid())
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["Obsolete.Plugin"] = new Entity("plugintype", typeId) { ["typename"] = "Obsolete.Plugin", ["isworkflowactivity"] = false }
            },
            steps: [protectedStep]);

        var plan = _planner.Plan(snapshot, Metadata(), _assembly, "MySolution");

        // The step is protected (Secure Configuration) so it's left in place; the type still
        // references it, so the type must not be deleted alongside it (KTD7).
        Assert.DoesNotContain(plan.PluginTypes.Deletes, a => a.Name == "Obsolete.Plugin");
        Assert.DoesNotContain(plan.Steps.Deletes, a => a.Name == "Orphaned.Step");
        Assert.Contains(plan.Warnings, w => w.Contains("Obsolete.Plugin"));
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
    public void Plan_NewImage_UpsertNameIncludesStepContext()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        const string stepName = "MyNamespace.MyPlugin: Update of account";

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId });

        var image = new PluginImageMetadata("preimage", "preimage", 0, "name");
        var step = new PluginStepMetadata(stepName, "Update", "account", 20, 0, 1, null, null, [image], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        var imageAction = Assert.Single(plan.Images.Upserts);
        Assert.Contains(" on ", imageAction.Name);
        Assert.Contains(stepName, imageAction.Name);
    }

    static Entity ExistingMatchedStep(Guid stepId, Guid typeId, Guid messageId, Guid filterId) => new("sdkmessageprocessingstep", stepId)
    {
        ["name"]               = "MyNamespace.MyPlugin: Update of account",
        ["plugintypeid"]       = new EntityReference("plugintype", typeId),
        ["stage"]              = new OptionSetValue(20),
        ["mode"]               = new OptionSetValue(0),
        ["rank"]               = 1,
        ["sdkmessageid"]       = new EntityReference("sdkmessage", messageId),
        ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId),
        ["asyncautodelete"]    = false
    };

    [Fact]
    public void Plan_ExistingImage_AliasRenamed_UpdatesInPlace()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId  = Guid.NewGuid();
        var stepId    = Guid.NewGuid();
        var existingStep = ExistingMatchedStep(stepId, typeId, messageId, filterId);

        var existingImage = new Entity("sdkmessageprocessingstepimage", Guid.NewGuid())
        {
            ["name"]                       = "PreImage",
            ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", stepId),
            ["imagetype"]                  = new OptionSetValue(0),
            ["entityalias"]                = "oldAlias",
            ["attributes"]                 = "name"
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            images: [existingImage],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId });

        var image = new PluginImageMetadata("PreImage", "newAlias", 0, "name");
        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of account", "Update", "account", 20, 0, 1, null, null, [image], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        var upsert = Assert.Single(plan.Images.Upserts);
        Assert.False(upsert.IsCreate);
        Assert.Equal("newAlias", upsert.Entity.GetAttributeValue<string>("entityalias"));
        Assert.Empty(plan.Images.Deletes);
    }

    [Fact]
    public void Plan_ExistingImage_AttributesChanged_UpdatesInPlace()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId  = Guid.NewGuid();
        var stepId    = Guid.NewGuid();
        var existingStep = ExistingMatchedStep(stepId, typeId, messageId, filterId);

        var existingImage = new Entity("sdkmessageprocessingstepimage", Guid.NewGuid())
        {
            ["name"]                       = "PreImage",
            ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", stepId),
            ["imagetype"]                  = new OptionSetValue(0),
            ["entityalias"]                = "alias",
            ["attributes"]                 = "name"
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            images: [existingImage],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId });

        var image = new PluginImageMetadata("PreImage", "alias", 0, "name,statecode");
        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of account", "Update", "account", 20, 0, 1, null, null, [image], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        var upsert = Assert.Single(plan.Images.Upserts);
        Assert.False(upsert.IsCreate);
        Assert.Equal("name,statecode", upsert.Entity.GetAttributeValue<string>("attributes"));
    }

    [Fact]
    public void Plan_DuplicateSameTypeImages_LowestIdMatchedRestOrphaned()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId  = Guid.NewGuid();
        var stepId    = Guid.NewGuid();
        var existingStep = ExistingMatchedStep(stepId, typeId, messageId, filterId);

        var lowerId  = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var higherId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var imageLow = new Entity("sdkmessageprocessingstepimage", lowerId)
        {
            ["name"] = "PreImage", ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", stepId),
            ["imagetype"] = new OptionSetValue(0), ["entityalias"] = "lowAlias", ["attributes"] = "name"
        };
        var imageHigh = new Entity("sdkmessageprocessingstepimage", higherId)
        {
            ["name"] = "PreImageDuplicate", ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", stepId),
            ["imagetype"] = new OptionSetValue(0), ["entityalias"] = "highAlias", ["attributes"] = "name"
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            images: [imageHigh, imageLow],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId });

        var image = new PluginImageMetadata("PreImage", "lowAlias", 0, "name");
        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of account", "Update", "account", 20, 0, 1, null, null, [image], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.Empty(plan.Images.Upserts); // lowest-id row already matches — no change detected
        Assert.Contains(plan.Images.Deletes, d => d.Id == higherId);
        Assert.DoesNotContain(plan.Images.Deletes, d => d.Id == lowerId);
    }

    [Fact]
    public void Plan_ImageTypeWithNoCodeDeclaration_DeletedAsOrphan()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId  = Guid.NewGuid();
        var stepId    = Guid.NewGuid();
        var existingStep = ExistingMatchedStep(stepId, typeId, messageId, filterId);

        var existingImage = new Entity("sdkmessageprocessingstepimage", Guid.NewGuid())
        {
            ["name"] = "PostImage", ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", stepId),
            ["imagetype"] = new OptionSetValue(1), ["entityalias"] = "alias", ["attributes"] = "name"
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            images: [existingImage],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId });

        // Code declares no images at all for this step
        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of account", "Update", "account", 20, 0, 1, null, null, [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.Contains(plan.Images.Deletes, d => d.Name.StartsWith("PostImage"));
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
    public void Plan_ExistingUnchangedStepAlreadyInSolution_DoesNotAddToSolution()
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
            filterIds: new() { [(messageId, "account", null)] = filterId },
            componentMembership: new() { [stepId] = ["MySolution"] });

        var step = new PluginStepMetadata(stepName, "Update", "account", 20, 0, 1, null, null, [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.Empty(plan.Steps.Upserts);
        Assert.Empty(plan.Steps.AddSolutionComponents);
    }

    [Fact]
    public void Plan_ExistingChangedStep_CreatesUpdate()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var stepId    = Guid.NewGuid();
        var filterId  = Guid.NewGuid();
        const string stepName = "MyNamespace.MyPlugin: Update of account";

        var existingStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"]               = stepName,
            ["plugintypeid"]       = new EntityReference("plugintype", typeId),
            ["stage"]              = new OptionSetValue(20),
            ["mode"]               = new OptionSetValue(0),
            ["rank"]               = 1, // will change to 2 — a mutable field, not part of the identity key
            ["sdkmessageid"]       = new EntityReference("sdkmessage", messageId),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId)
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId });

        var step = new PluginStepMetadata(stepName, "Update", "account", 20, 0, 2, null, null, [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.Contains(plan.Steps.Upserts, a => a.Name == stepName);
        Assert.False(plan.Steps.Upserts.Single(a => a.Name == stepName).IsCreate);
        Assert.Empty(plan.Steps.Deletes);
    }

    [Fact]
    public void Plan_ExistingStep_StageChanged_RecreatesRatherThanUpdates()
    {
        // Stage is part of the identity key (R1) — needed to tell apart multiple [Handles] on one
        // class that differ only by stage/mode (R4). Changing it therefore recreates the step
        // rather than updating it in place; this is a deliberate, resolved design decision, not a bug.
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var stepId    = Guid.NewGuid();
        var filterId  = Guid.NewGuid();
        const string stepName = "MyNamespace.MyPlugin: Update of account";

        var existingStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"]               = stepName,
            ["plugintypeid"]       = new EntityReference("plugintype", typeId),
            ["stage"]              = new OptionSetValue(10), // pre-validation; code now declares 20
            ["mode"]               = new OptionSetValue(0),
            ["rank"]               = 1,
            ["sdkmessageid"]       = new EntityReference("sdkmessage", messageId),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId)
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

        Assert.Contains(plan.Steps.Upserts, a => a.Name == stepName);
        Assert.True(plan.Steps.Upserts.Single(a => a.Name == stepName).IsCreate);
        Assert.Contains(plan.Steps.Deletes, d => d.Id == stepId);
    }

    [Fact]
    public void Plan_UpdatedStepInOtherSolution_AddsWarning()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var stepId    = Guid.NewGuid();
        var filterId  = Guid.NewGuid();
        const string stepName = "MyNamespace.MyPlugin: Update of account";

        var existingStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"]               = stepName,
            ["plugintypeid"]       = new EntityReference("plugintype", typeId),
            ["stage"]              = new OptionSetValue(20),
            ["mode"]               = new OptionSetValue(0),
            ["rank"]               = 1, // will change to 2 — a mutable field, not part of the identity key
            ["sdkmessageid"]       = new EntityReference("sdkmessage", messageId),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId)
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
            filterIds: new() { [(messageId, "account", null)] = filterId },
            componentMembership: membership);

        var step = new PluginStepMetadata(stepName, "Update", "account", 20, 0, 2, null, null, [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.Single(plan.Warnings);
        Assert.Contains("OtherSolution", plan.Warnings[0]);
        Assert.Contains(stepName, plan.Warnings[0]);
    }

    [Fact]
    public void Plan_ObsoleteStepInOtherSolution_AddsWarning()
    {
        // A step whose identity-key field changed (or that was simply removed from source) is now
        // deleted, not updated — the cross-solution warning must fire on deletes too, or a shared
        // registration can be silently removed with no warning.
        var typeId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        const string stepName = "Orphaned.Step";

        var obsoleteStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"] = stepName,
            ["plugintypeid"] = new EntityReference("plugintype", typeId)
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
            steps: [obsoleteStep],
            componentMembership: membership);

        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], [], false)), _assembly, "MySolution");

        Assert.Contains(plan.Steps.Deletes, d => d.Id == stepId);
        Assert.Contains(plan.Warnings, w => w.Contains("OtherSolution") && w.Contains(stepName));
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

    [Fact]
    public void Plan_ObsoleteStepWithSecureConfig_ReportsInsteadOfDeleting()
    {
        var typeId  = Guid.NewGuid();
        var stepId  = Guid.NewGuid();
        const string stepName = "Orphaned.Step";

        var obsoleteStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"]                                   = stepName,
            ["plugintypeid"]                            = new EntityReference("plugintype", typeId),
            ["sdkmessageprocessingstepsecureconfigid"]  = new EntityReference("sdkmessageprocessingstepsecureconfig", Guid.NewGuid())
        };
        var obsoleteImage = new Entity("sdkmessageprocessingstepimage", Guid.NewGuid())
        {
            ["name"] = "PreImage",
            ["sdkmessageprocessingstepid"] = new EntityReference("sdkmessageprocessingstep", stepId),
            ["imagetype"] = new OptionSetValue(0)
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [obsoleteStep],
            images: [obsoleteImage]);

        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], [], false)), _assembly, "MySolution");

        Assert.DoesNotContain(plan.Steps.Deletes, a => a.Name == stepName);
        Assert.Contains(plan.Warnings, w => w.Contains(stepName) && w.Contains("Secure Configuration"));
        Assert.Empty(plan.Images.Deletes);
    }

    // -- asyncautodelete / DeleteJobOnSuccess --

    [Fact]
    public void Plan_AsyncStep_WithDeleteJobOnSuccess_DetectsChange()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var stepId    = Guid.NewGuid();
        var filterId  = Guid.NewGuid();
        const string stepName = "MyNamespace.MyPlugin: Update of account";

        var existingStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"]               = stepName,
            ["plugintypeid"]       = new EntityReference("plugintype", typeId),
            ["stage"]              = new OptionSetValue(40),
            ["mode"]               = new OptionSetValue(1),
            ["rank"]               = 1,
            ["sdkmessageid"]       = new EntityReference("sdkmessage", messageId),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId),
            ["asyncautodelete"]    = false   // currently false in Dataverse
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId });

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
        var filterId  = Guid.NewGuid();
        const string stepName = "MyNamespace.MyPlugin: Update of account";

        var existingStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"]                 = stepName,
            ["plugintypeid"]         = new EntityReference("plugintype", typeId),
            ["stage"]                = new OptionSetValue(20),
            ["mode"]                 = new OptionSetValue(0),
            ["rank"]                 = 1,
            ["sdkmessageid"]         = new EntityReference("sdkmessage", messageId),
            ["sdkmessagefilterid"]   = new EntityReference("sdkmessagefilter", filterId),
            ["impersonatinguserid"]  = (EntityReference?)null   // no impersonation currently
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId },
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

    [Fact]
    public void Plan_ExistingCustomApiComponentsAlreadyInSolution_DoesNotAddToSolution()
    {
        var typeId  = Guid.NewGuid();
        var apiId   = Guid.NewGuid();
        var paramId = Guid.NewGuid();
        var propId  = Guid.NewGuid();

        var existingApi = new Entity("customapi", apiId)
        {
            ["uniquename"]                      = "abc_MyApi",
            ["name"]                            = "abc_MyApi",
            ["displayname"]                     = "My Api",
            ["description"]                     = "desc",
            ["bindingtype"]                     = new OptionSetValue(0),
            ["isfunction"]                      = false,
            ["isprivate"]                       = false,
            ["allowedcustomprocessingsteptype"] = new OptionSetValue(0),
            ["plugintypeid"]                    = new EntityReference("plugintype", typeId)
        };
        var existingParam = new Entity("customapirequestparameter", paramId)
        {
            ["customapiid"]       = new EntityReference("customapi", apiId),
            ["uniquename"]        = "Input",
            ["name"]              = "Input",
            ["displayname"]       = "Input",
            ["description"]       = "input desc",
            ["type"]              = new OptionSetValue(10),
            ["isoptional"]        = true,
            ["logicalentityname"] = (string?)null
        };
        var existingProp = new Entity("customapiresponseproperty", propId)
        {
            ["customapiid"]       = new EntityReference("customapi", apiId),
            ["uniquename"]        = "Output",
            ["name"]              = "Output",
            ["displayname"]       = "Output",
            ["description"]       = "output desc",
            ["type"]              = new OptionSetValue(10),
            ["logicalentityname"] = (string?)null
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            customApis: [existingApi],
            requestParams: [existingParam],
            responseProps: [existingProp],
            prefix: "abc",
            componentMembership: new()
            {
                [apiId] = ["MySolution"],
                [paramId] = ["MySolution"],
                [propId] = ["MySolution"]
            });

        var customApi = new CustomApiMetadata(
            "MyApi", "My Api", "desc", 0, null, false, false, 0, null, "MyNamespace.MyPlugin",
            [new RequestParameterMetadata("Input", "Input", "Input", "input desc", 10, true, null)],
            [new ResponsePropertyMetadata("Output", "Output", "Output", "output desc", 10, null)]);

        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [], [customApi], false, IsCustomApi: true)), _assembly, "MySolution");

        Assert.Empty(plan.CustomApis.AddSolutionComponents);
        Assert.Empty(plan.RequestParams.AddSolutionComponents);
        Assert.Empty(plan.ResponseProps.AddSolutionComponents);
    }

    // -- CustomApi UniqueName override (U4) --

    [Fact]
    public void Plan_CustomApiUniqueNameOverride_NoLiveRecord_CreatesUsingOverrideVerbatim()
    {
        var typeId = Guid.NewGuid();
        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.LegacyOrderApprovalPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.LegacyOrderApprovalPlugin" }
            },
            prefix: "dev1");

        var customApi = new CustomApiMetadata(
            "LegacyOrderApproval", "Legacy Order Approval", "desc", 0, null, false, false, 0, null,
            "MyNamespace.LegacyOrderApprovalPlugin", [], [], UniqueNameOverride: "dev1_ApproveOrder");
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("LegacyOrderApprovalPlugin", "MyNamespace.LegacyOrderApprovalPlugin", [], [customApi], false, IsCustomApi: true)), _assembly, "MySolution");

        var action = Assert.Single(plan.CustomApis.Upserts);
        Assert.True(action.IsCreate);
        Assert.Equal("dev1_ApproveOrder", action.Entity.GetAttributeValue<string>("uniquename"));
    }

    [Fact]
    public void Plan_CustomApiUniqueNameOverride_MatchesExistingLiveRecord_UpdatesInPlace_NoDelete()
    {
        // Core migration scenario: class unchanged, live API's name doesn't match the derived formula.
        var typeId = Guid.NewGuid();
        var apiId  = Guid.NewGuid();

        var liveApi = new Entity("customapi", apiId)
        {
            ["uniquename"]                      = "dev1_ApproveOrder",
            ["displayname"]                     = "Approve Order",
            ["description"]                     = "desc",
            ["bindingtype"]                     = new OptionSetValue(0),
            ["isfunction"]                       = false,
            ["isprivate"]                       = false,
            ["allowedcustomprocessingsteptype"] = new OptionSetValue(0),
            ["plugintypeid"]                    = new EntityReference("plugintype", typeId)
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.LegacyOrderApprovalPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.LegacyOrderApprovalPlugin" }
            },
            customApis: [liveApi],
            prefix: "dev1",
            componentTypeById: new Dictionary<Guid, int> { [apiId] = 400 });

        var customApi = new CustomApiMetadata(
            "LegacyOrderApproval", "Approve Order", "desc", 0, null, false, false, 0, null,
            "MyNamespace.LegacyOrderApprovalPlugin", [], [], UniqueNameOverride: "dev1_ApproveOrder");
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("LegacyOrderApprovalPlugin", "MyNamespace.LegacyOrderApprovalPlugin", [], [customApi], false, IsCustomApi: true)), _assembly, "MySolution");

        // Adopted in place — no create (already exists), and critically no delete of the live record.
        Assert.Empty(plan.CustomApis.Deletes);
        Assert.DoesNotContain(plan.CustomApis.Upserts, a => a.IsCreate);
    }

    [Fact]
    public void Plan_CustomApiUniqueNameOverride_Redundant_WarnsButProceeds()
    {
        var typeId = Guid.NewGuid();
        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyApiPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyApiPlugin" }
            },
            prefix: "dev1");

        // Class name "MyApiPlugin" strips (via "Plugin" suffix) to "MyApi" — override matches exactly.
        var customApi = new CustomApiMetadata(
            "MyApi", "My Api", "desc", 0, null, false, false, 0, null,
            "MyNamespace.MyApiPlugin", [], [], UniqueNameOverride: "dev1_MyApi");
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyApiPlugin", "MyNamespace.MyApiPlugin", [], [customApi], false, IsCustomApi: true)), _assembly, "MySolution");

        var action = Assert.Single(plan.CustomApis.Upserts);
        Assert.True(action.IsCreate);
        Assert.Equal("dev1_MyApi", action.Entity.GetAttributeValue<string>("uniquename"));
        Assert.Contains("redundant", _console.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Plan_CustomApiUniqueNameOverride_WrongPrefix_Throws()
    {
        var typeId = Guid.NewGuid();
        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyApiPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyApiPlugin" }
            },
            prefix: "dev1");

        var customApi = new CustomApiMetadata(
            "MyApi", "My Api", "desc", 0, null, false, false, 0, null,
            "MyNamespace.MyApiPlugin", [], [], UniqueNameOverride: "wrong_MyApi");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyApiPlugin", "MyNamespace.MyApiPlugin", [], [customApi], false, IsCustomApi: true)), _assembly, "MySolution"));

        Assert.Contains("wrong_MyApi", ex.Message);
        Assert.Contains("dev1_", ex.Message);
    }

    [Fact]
    public void Plan_TwoCustomApisSameUniqueNameOverride_Throws()
    {
        var typeId1 = Guid.NewGuid();
        var typeId2 = Guid.NewGuid();
        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.FirstApiPlugin"]  = new Entity("plugintype", typeId1) { ["typename"] = "MyNamespace.FirstApiPlugin" },
                ["MyNamespace.SecondApiPlugin"] = new Entity("plugintype", typeId2) { ["typename"] = "MyNamespace.SecondApiPlugin" }
            },
            prefix: "dev1");

        var firstApi  = new CustomApiMetadata("First", "First", "desc", 0, null, false, false, 0, null, "MyNamespace.FirstApiPlugin", [], [], UniqueNameOverride: "dev1_Shared");
        var secondApi = new CustomApiMetadata("Second", "Second", "desc", 0, null, false, false, 0, null, "MyNamespace.SecondApiPlugin", [], [], UniqueNameOverride: "dev1_Shared");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _planner.Plan(snapshot,
                Metadata(
                    new PluginTypeMetadata("FirstApiPlugin", "MyNamespace.FirstApiPlugin", [], [firstApi], false, IsCustomApi: true),
                    new PluginTypeMetadata("SecondApiPlugin", "MyNamespace.SecondApiPlugin", [], [secondApi], false, IsCustomApi: true)),
                _assembly, "MySolution"));

        Assert.Contains("dev1_Shared", ex.Message);
        Assert.Contains("MyNamespace.FirstApiPlugin", ex.Message);
        Assert.Contains("MyNamespace.SecondApiPlugin", ex.Message);
    }

    [Fact]
    public void Plan_DerivedNameCollidesWithUniqueNameOverride_Throws()
    {
        var typeId1 = Guid.NewGuid();
        var typeId2 = Guid.NewGuid();
        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyApiPlugin"]     = new Entity("plugintype", typeId1) { ["typename"] = "MyNamespace.MyApiPlugin" },
                ["MyNamespace.OtherApiPlugin"] = new Entity("plugintype", typeId2) { ["typename"] = "MyNamespace.OtherApiPlugin" }
            },
            prefix: "dev1");

        // "MyApiPlugin" derives (via "Plugin" suffix) to "MyApi" -> "dev1_MyApi"; the second class
        // explicitly overrides to the same final value.
        var derivedApi  = new CustomApiMetadata("MyApi", "My Api", "desc", 0, null, false, false, 0, null, "MyNamespace.MyApiPlugin", [], []);
        var overrideApi = new CustomApiMetadata("Other", "Other", "desc", 0, null, false, false, 0, null, "MyNamespace.OtherApiPlugin", [], [], UniqueNameOverride: "dev1_MyApi");

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _planner.Plan(snapshot,
                Metadata(
                    new PluginTypeMetadata("MyApiPlugin", "MyNamespace.MyApiPlugin", [], [derivedApi], false, IsCustomApi: true),
                    new PluginTypeMetadata("OtherApiPlugin", "MyNamespace.OtherApiPlugin", [], [overrideApi], false, IsCustomApi: true)),
                _assembly, "MySolution"));

        Assert.Contains("dev1_MyApi", ex.Message);
    }

    [Fact]
    public void Plan_TwoCustomApisSameDerivedName_NoOverrideOnEither_Throws()
    {
        // Both classes omit UniqueNameOverride entirely -- the collision comes purely from the
        // derived "{prefix}_{BaseName}" formula, not from any override. R6 must catch this case too,
        // since duplicate detection can no longer rely on the compiler enforcing unique class names.
        var typeId1 = Guid.NewGuid();
        var typeId2 = Guid.NewGuid();
        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.FirstApiPlugin"]  = new Entity("plugintype", typeId1) { ["typename"] = "MyNamespace.FirstApiPlugin" },
                ["MyNamespace.SecondApiPlugin"] = new Entity("plugintype", typeId2) { ["typename"] = "MyNamespace.SecondApiPlugin" }
            },
            prefix: "dev1");

        var firstApi  = new CustomApiMetadata("Shared", "Shared", "desc", 0, null, false, false, 0, null, "MyNamespace.FirstApiPlugin", [], []);
        var secondApi = new CustomApiMetadata("Shared", "Shared", "desc", 0, null, false, false, 0, null, "MyNamespace.SecondApiPlugin", [], []);

        var ex = Assert.Throws<InvalidOperationException>(() =>
            _planner.Plan(snapshot,
                Metadata(
                    new PluginTypeMetadata("FirstApiPlugin", "MyNamespace.FirstApiPlugin", [], [firstApi], false, IsCustomApi: true),
                    new PluginTypeMetadata("SecondApiPlugin", "MyNamespace.SecondApiPlugin", [], [secondApi], false, IsCustomApi: true)),
                _assembly, "MySolution"));

        Assert.Contains("dev1_Shared", ex.Message);
        Assert.Contains("MyNamespace.FirstApiPlugin", ex.Message);
        Assert.Contains("MyNamespace.SecondApiPlugin", ex.Message);
    }

    // -- Tuple identity match (R1/R2) --

    [Fact]
    public void Plan_StepRenamedToStageQualified_TupleMatchProducesUpdate()
    {
        // Simulate a class that gained a second [Handles], causing the step name to acquire
        // a stage suffix. The snapshot still holds the old (unsuffixed) name.
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId  = Guid.NewGuid();
        var stepId    = Guid.NewGuid();
        const string oldName = "MyNamespace.MyPlugin: Update of account";
        const string newName = "MyNamespace.MyPlugin: Update of account at PreOperation";

        var existingStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"]                = oldName,
            ["plugintypeid"]        = new EntityReference("plugintype", typeId),
            ["stage"]               = new OptionSetValue(20),
            ["mode"]                = new OptionSetValue(0),
            ["rank"]                = 1,
            ["filteringattributes"] = (string?)null,
            ["configuration"]       = (string?)null,
            ["sdkmessageid"]        = new EntityReference("sdkmessage", messageId),
            ["sdkmessagefilterid"]  = new EntityReference("sdkmessagefilter", filterId),
            ["asyncautodelete"]     = false
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId });

        var step = new PluginStepMetadata(newName, "Update", "account", 20, 0, 1, null, null, [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        var upsert = Assert.Single(plan.Steps.Upserts);
        Assert.Equal(newName, upsert.Name);
        Assert.False(upsert.IsCreate);
        Assert.Empty(plan.Steps.Deletes);
    }

    [Fact]
    public void Plan_TupleMatch_DoesNotDeleteOldStepName()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId  = Guid.NewGuid();
        var stepId    = Guid.NewGuid();
        const string oldName = "MyNamespace.MyPlugin: Update of account";
        const string newName = "MyNamespace.MyPlugin: Update of account at PreOperation";

        var existingStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"]               = oldName,
            ["plugintypeid"]       = new EntityReference("plugintype", typeId),
            ["stage"]              = new OptionSetValue(20),
            ["mode"]               = new OptionSetValue(0),
            ["rank"]               = 1,
            ["sdkmessageid"]       = new EntityReference("sdkmessage", messageId),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId),
            ["asyncautodelete"]    = false
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId });

        var step = new PluginStepMetadata(newName, "Update", "account", 20, 0, 1, null, null, [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        Assert.DoesNotContain(plan.Steps.Deletes, d => d.Id == stepId);
    }

    [Fact]
    public void Plan_TwoHandlesSameStageDifferentMode_MatchIndependently()
    {
        // Regression for docs/solutions/logic-errors/secondary-match-predicate-missing-mode.md:
        // sync (mode=0) and async (mode=1) PostOperation (stage=40) steps on one class must never
        // be confused by the tuple lookup — mode is part of the identity key (R1) precisely because
        // stage alone is shared by both.
        var typeId     = Guid.NewGuid();
        var messageId  = Guid.NewGuid();
        var filterId   = Guid.NewGuid();
        var syncStepId  = Guid.NewGuid();
        var asyncStepId = Guid.NewGuid();
        const string syncName  = "MyNamespace.MyPlugin: Update of account";
        const string asyncName = "MyNamespace.MyPlugin: Update of account at PostOperationAsync";

        var syncStep = new Entity("sdkmessageprocessingstep", syncStepId)
        {
            ["name"]               = syncName,
            ["plugintypeid"]       = new EntityReference("plugintype", typeId),
            ["stage"]              = new OptionSetValue(40),
            ["mode"]               = new OptionSetValue(0),
            ["rank"]               = 1,
            ["sdkmessageid"]       = new EntityReference("sdkmessage", messageId),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId),
            ["asyncautodelete"]    = false
        };
        var asyncStep = new Entity("sdkmessageprocessingstep", asyncStepId)
        {
            ["name"]               = asyncName,
            ["plugintypeid"]       = new EntityReference("plugintype", typeId),
            ["stage"]              = new OptionSetValue(40),
            ["mode"]               = new OptionSetValue(1),
            ["rank"]               = 2,
            ["sdkmessageid"]       = new EntityReference("sdkmessage", messageId),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId),
            ["asyncautodelete"]    = true
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [syncStep, asyncStep],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId });

        var syncMeta  = new PluginStepMetadata(syncName, "Update", "account", 40, 0, 1, null, null, [], []);
        var asyncMeta = new PluginStepMetadata(asyncName, "Update", "account", 40, 1, 2, null, null, [], [], AsyncAutoDelete: true);
        var plan = _planner.Plan(snapshot,
            Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [syncMeta, asyncMeta], [], false)),
            _assembly, "MySolution");

        // Both rows already match their own asmStep exactly — nothing changed, and critically,
        // neither is mistaken for the other's match (which would show up as an obsolete delete).
        Assert.Empty(plan.Steps.Upserts);
        Assert.Empty(plan.Steps.Deletes);
    }

    [Fact]
    public void Plan_CollisionWithNameTiebreak_UpdatesMatchAndDeletesOther()
    {
        // Two existing rows share the same identity tuple (message+table+stage+mode) — a
        // collision (R5). Neither has a Secure Configuration, and exactly one (A) matches the
        // name the current code would generate, so A is updated and B is deleted as obsolete.
        var typeId     = Guid.NewGuid();
        var messageId  = Guid.NewGuid();
        var filterId   = Guid.NewGuid();
        var stepAId    = Guid.NewGuid();
        var stepBId    = Guid.NewGuid();
        const string stepName = "MyNamespace.MyPlugin: Update of account at PreOperation";

        var stepA = new Entity("sdkmessageprocessingstep", stepAId)
        {
            ["name"]               = stepName,
            ["plugintypeid"]       = new EntityReference("plugintype", typeId),
            ["stage"]              = new OptionSetValue(20),
            ["mode"]               = new OptionSetValue(0),
            ["rank"]               = 1,
            ["sdkmessageid"]       = new EntityReference("sdkmessage", messageId),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId),
            ["asyncautodelete"]    = false
        };
        var stepB = new Entity("sdkmessageprocessingstep", stepBId)
        {
            ["name"]               = "MyNamespace.MyPlugin: Update of account",
            ["plugintypeid"]       = new EntityReference("plugintype", typeId),
            ["stage"]              = new OptionSetValue(20),
            ["mode"]               = new OptionSetValue(0),
            ["rank"]               = 1,
            ["sdkmessageid"]       = new EntityReference("sdkmessage", messageId),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId),
            ["asyncautodelete"]    = false
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [stepA, stepB],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId });

        var step = new PluginStepMetadata(stepName, "Update", "account", 20, 0, 1, null, null, [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        // Name-tiebreak winner (A) matched → no upsert (no changes detected), B is obsolete → deleted
        Assert.Empty(plan.Steps.Upserts);
        Assert.Contains(plan.Steps.Deletes, d => d.Id == stepBId);
        Assert.DoesNotContain(plan.Steps.Deletes, d => d.Id == stepAId);
    }

    // -- Collision handling (R5) --

    static (Entity stepA, Entity stepB, Guid typeId, Guid messageId, Guid filterId) CollidingSteps(
        Action<Entity>? configureA = null, Action<Entity>? configureB = null)
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId  = Guid.NewGuid();

        var stepA = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["name"]               = "MyNamespace.MyPlugin: Update of account",
            ["plugintypeid"]       = new EntityReference("plugintype", typeId),
            ["stage"]              = new OptionSetValue(20),
            ["mode"]               = new OptionSetValue(0),
            ["rank"]               = 1,
            ["sdkmessageid"]       = new EntityReference("sdkmessage", messageId),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId),
            ["asyncautodelete"]    = false
        };
        var stepB = new Entity("sdkmessageprocessingstep", Guid.NewGuid())
        {
            ["name"]               = "Some Other Display Name",
            ["plugintypeid"]       = new EntityReference("plugintype", typeId),
            ["stage"]              = new OptionSetValue(20),
            ["mode"]               = new OptionSetValue(0),
            ["rank"]               = 1,
            ["sdkmessageid"]       = new EntityReference("sdkmessage", messageId),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", filterId),
            ["asyncautodelete"]    = false
        };
        configureA?.Invoke(stepA);
        configureB?.Invoke(stepB);

        return (stepA, stepB, typeId, messageId, filterId);
    }

    [Fact]
    public void Plan_CollisionWithNoNameMatch_ThrowsNamingBothRows()
    {
        var (stepA, stepB, typeId, messageId, filterId) = CollidingSteps(
            configureA: s => s["name"] = "Neither Matches A",
            configureB: s => s["name"] = "Neither Matches B");

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [stepA, stepB],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId });

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of account", "Update", "account", 20, 0, 1, null, null, [], []);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution"));

        Assert.Contains(stepA.Id.ToString(), ex.Message);
        Assert.Contains(stepB.Id.ToString(), ex.Message);
    }

    [Fact]
    public void Plan_CollisionWithBothNamesMatching_ThrowsNamingBothRows()
    {
        var (stepA, stepB, typeId, messageId, filterId) = CollidingSteps(
            configureB: s => s["name"] = "MyNamespace.MyPlugin: Update of account");

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [stepA, stepB],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId });

        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of account", "Update", "account", 20, 0, 1, null, null, [], []);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution"));

        Assert.Contains(stepA.Id.ToString(), ex.Message);
        Assert.Contains(stepB.Id.ToString(), ex.Message);
    }

    [Fact]
    public void Plan_CollisionWithSecureConfigOnNonMatchingRow_ThrowsWithoutAttemptingTiebreak()
    {
        var (stepA, stepB, typeId, messageId, filterId) = CollidingSteps(
            configureB: s => s["sdkmessageprocessingstepsecureconfigid"] = new EntityReference("sdkmessageprocessingstepsecureconfig", Guid.NewGuid()));

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [stepA, stepB],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId });

        // stepA's name matches exactly — the tiebreak would resolve if attempted — but stepB
        // carries a Secure Configuration, so the gate must fire before any tiebreak is attempted.
        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of account", "Update", "account", 20, 0, 1, null, null, [], []);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution"));

        Assert.Contains("Secure Configuration", ex.Message);
    }

    [Fact]
    public void Plan_CollisionWithSecureConfigOnMatchingRow_ThrowsWithoutAttemptingTiebreak()
    {
        var (stepA, stepB, typeId, messageId, filterId) = CollidingSteps(
            configureA: s => s["sdkmessageprocessingstepsecureconfigid"] = new EntityReference("sdkmessageprocessingstepsecureconfig", Guid.NewGuid()));

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [stepA, stepB],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId });

        // stepA would win the name tiebreak, but stepA itself carries the Secure Configuration —
        // the gate checks all colliding rows, not just the one that would lose the tiebreak.
        var step = new PluginStepMetadata("MyNamespace.MyPlugin: Update of account", "Update", "account", 20, 0, 1, null, null, [], []);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution"));

        Assert.Contains("Secure Configuration", ex.Message);
    }

    [Fact]
    public void Plan_NoNameMatchNoSecondaryMatch_CreatesStep()
    {
        var typeId    = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var filterId  = Guid.NewGuid();
        var stepId    = Guid.NewGuid();

        // Snapshot has a Create step; assembly wants an Update step
        var existingStep = new Entity("sdkmessageprocessingstep", stepId)
        {
            ["name"]               = "MyNamespace.MyPlugin: Create of account",
            ["plugintypeid"]       = new EntityReference("plugintype", typeId),
            ["stage"]              = new OptionSetValue(40),
            ["mode"]               = new OptionSetValue(0),
            ["rank"]               = 1,
            ["sdkmessageid"]       = new EntityReference("sdkmessage", Guid.NewGuid()),
            ["sdkmessagefilterid"] = new EntityReference("sdkmessagefilter", Guid.NewGuid()),
            ["asyncautodelete"]    = false
        };

        var snapshot = Snapshot(
            pluginTypes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["MyNamespace.MyPlugin"] = new Entity("plugintype", typeId) { ["typename"] = "MyNamespace.MyPlugin" }
            },
            steps: [existingStep],
            messageIds: new(StringComparer.OrdinalIgnoreCase) { ["Update"] = messageId },
            filterIds: new() { [(messageId, "account", null)] = filterId });

        const string newStepName = "MyNamespace.MyPlugin: Update of account";
        var step = new PluginStepMetadata(newStepName, "Update", "account", 20, 0, 1, null, null, [], []);
        var plan = _planner.Plan(snapshot, Metadata(new PluginTypeMetadata("MyPlugin", "MyNamespace.MyPlugin", [step], [], false)), _assembly, "MySolution");

        var upsert = Assert.Single(plan.Steps.Upserts);
        Assert.Equal(newStepName, upsert.Name);
        Assert.True(upsert.IsCreate);
    }

    // -- Multi-assembly package planning (U5) --
    //
    // U5 does not modify Plan's signature or diff logic (docs/plans/2026-07-14-001-feat-pluginpackage-nuget-support-plan.md
    // lines 441-466, KTD15). U4's PluginReader.LoadPackageSnapshotsAsync produces one independently-scoped
    // RegistrationSnapshot per assembly — these tests confirm Plan, called once per (snapshot, metadata,
    // assembly) tuple from that output, stays correctly isolated per assembly with no cross-assembly leakage.

    [Fact]
    public void Plan_TwoAssembliesSamePackage_EachIndependentlyDiffed_NoCrossAssemblyLeakage()
    {
        var assemblyA = new Entity("pluginassembly", Guid.NewGuid()) { ["name"] = "AssemblyA" };
        var assemblyB = new Entity("pluginassembly", Guid.NewGuid()) { ["name"] = "AssemblyB" };

        // Neither assembly's snapshot has ever seen the other's types — this is what U4's
        // LoadPackageSnapshotsAsync actually produces (one query scoped to one assembly.Id each).
        var snapshotA = Snapshot();
        var snapshotB = Snapshot();

        var metadataA = new PluginAssemblyMetadata("AssemblyA", "AssemblyA, Version=1.0.0.0", [1], "hashA", "1.0.0.0", null, "neutral",
            [new PluginTypeMetadata("NewTypeA", "NamespaceA.NewTypeA", [], [], false)]);
        var metadataB = new PluginAssemblyMetadata("AssemblyB", "AssemblyB, Version=1.0.0.0", [1], "hashB", "1.0.0.0", null, "neutral",
            [new PluginTypeMetadata("NewTypeB", "NamespaceB.NewTypeB", [], [], false)]);

        var planA = _planner.Plan(snapshotA, metadataA, assemblyA, "MySolution");
        var planB = _planner.Plan(snapshotB, metadataB, assemblyB, "MySolution");

        var upsertA = Assert.Single(planA.PluginTypes.Upserts);
        Assert.Equal("NamespaceA.NewTypeA", upsertA.Entity.GetAttributeValue<string>("typename"));
        Assert.DoesNotContain(planA.PluginTypes.Upserts, u => u.Entity.GetAttributeValue<string>("typename") == "NamespaceB.NewTypeB");

        var upsertB = Assert.Single(planB.PluginTypes.Upserts);
        Assert.Equal("NamespaceB.NewTypeB", upsertB.Entity.GetAttributeValue<string>("typename"));
        Assert.DoesNotContain(planB.PluginTypes.Upserts, u => u.Entity.GetAttributeValue<string>("typename") == "NamespaceA.NewTypeA");
    }

    [Fact]
    public void Plan_ClassRemovedFromOneAssemblyOnly_OnlyThatAssemblysPlanHasDeletes()
    {
        var typeIdA = Guid.NewGuid();
        var typeIdB = Guid.NewGuid();
        var assemblyA = new Entity("pluginassembly", Guid.NewGuid()) { ["name"] = "AssemblyA" };
        var assemblyB = new Entity("pluginassembly", Guid.NewGuid()) { ["name"] = "AssemblyB" };

        var snapshotA = Snapshot(pluginTypes: new(StringComparer.OrdinalIgnoreCase)
        {
            ["NamespaceA.RemovedType"] = new Entity("plugintype", typeIdA) { ["typename"] = "NamespaceA.RemovedType", ["isworkflowactivity"] = false }
        });
        var snapshotB = Snapshot(pluginTypes: new(StringComparer.OrdinalIgnoreCase)
        {
            ["NamespaceB.StillThereType"] = new Entity("plugintype", typeIdB) { ["typename"] = "NamespaceB.StillThereType", ["isworkflowactivity"] = false }
        });

        // Assembly A's class was deleted locally — its reflected metadata no longer declares the type.
        var metadataA = new PluginAssemblyMetadata("AssemblyA", "AssemblyA, Version=1.0.0.0", [1], "hashA", "1.0.0.0", null, "neutral", []);
        // Assembly B is unchanged — its type is still declared.
        var metadataB = new PluginAssemblyMetadata("AssemblyB", "AssemblyB, Version=1.0.0.0", [1], "hashB", "1.0.0.0", null, "neutral",
            [new PluginTypeMetadata("StillThereType", "NamespaceB.StillThereType", [], [], false)]);

        var planA = _planner.Plan(snapshotA, metadataA, assemblyA, "MySolution");
        var planB = _planner.Plan(snapshotB, metadataB, assemblyB, "MySolution");

        var deleteA = Assert.Single(planA.PluginTypes.Deletes);
        Assert.Equal("NamespaceA.RemovedType", deleteA.Name);
        Assert.Equal(typeIdA, deleteA.Id);

        Assert.Empty(planB.PluginTypes.Deletes);
        Assert.Equal(0, planB.TotalChanges);
    }

    [Fact]
    public void Plan_UnchangedTwoAssemblyPackage_PerAssemblyScopedSnapshotProducesEmptyPlan_KTD15Regression()
    {
        var typeIdA = Guid.NewGuid();
        var typeIdB = Guid.NewGuid();
        var assemblyA = new Entity("pluginassembly", Guid.NewGuid()) { ["name"] = "AssemblyA" };

        var pluginTypeA = new Entity("plugintype", typeIdA) { ["typename"] = "NamespaceA.TypeA", ["isworkflowactivity"] = false };
        var pluginTypeB = new Entity("plugintype", typeIdB) { ["typename"] = "NamespaceB.TypeB", ["isworkflowactivity"] = false };

        // KTD15: this is exactly what LoadPackageSnapshotsAsync produces for assembly A — a snapshot
        // scoped only to A's own auto-created plugintype records. Assembly B's live plugintype (TypeB)
        // is deliberately NOT included here — it was never fetched by A's scoped query in the first
        // place, unlike a hypothetical merged/shared snapshot which would contain both. If Plan were
        // ever called with such a merged snapshot instead, TypeB would look "no longer present locally"
        // from assembly A's single-assembly metadata below, and the assertions below would fail because
        // PluginTypes.Deletes would then contain TypeB (see Plan_ObsoletePluginType_CreatesDelete for
        // proof that any entry present in snapshot.PluginTypes but absent from local metadata is deleted).
        var snapshotAScoped = Snapshot(pluginTypes: new(StringComparer.OrdinalIgnoreCase)
        {
            ["NamespaceA.TypeA"] = pluginTypeA
        });

        // Neither assembly changed locally: A's metadata still declares exactly TypeA.
        var metadataA = new PluginAssemblyMetadata("AssemblyA", "AssemblyA, Version=1.0.0.0", [1], "hashA", "1.0.0.0", null, "neutral",
            [new PluginTypeMetadata("TypeA", "NamespaceA.TypeA", [], [], false)]);

        var planA = _planner.Plan(snapshotAScoped, metadataA, assemblyA, "MySolution");

        Assert.Equal(0, planA.TotalChanges);
        Assert.Empty(planA.PluginTypes.Deletes);
        Assert.Empty(planA.PluginTypes.Upserts);
        Assert.DoesNotContain(planA.PluginTypes.Deletes, d => d.Name == "NamespaceB.TypeB");
        Assert.DoesNotContain(planA.PluginTypes.Deletes, d => d.Id == typeIdB);

        // Sanity check on the scoping itself: assembly B's type is genuinely absent from A's snapshot,
        // not merely "correctly attributed" within a shared dictionary — there is no merged dictionary here.
        Assert.DoesNotContain("NamespaceB.TypeB", snapshotAScoped.PluginTypes.Keys);
    }
}
