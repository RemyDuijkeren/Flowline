using FluentAssertions;
using Flowline.Core.Configure;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Xunit;

namespace Flowline.Core.Tests.Configure;

public class SolutionComponentInventoryTests
{
    static Entity Workflow(int statecode)
    {
        var e = new Entity("workflow", Guid.NewGuid());
        e["statecode"] = new OptionSetValue(statecode);
        return e;
    }

    static Entity Step(int statecode)
    {
        var e = new Entity("sdkmessageprocessingstep", Guid.NewGuid());
        e["statecode"] = new OptionSetValue(statecode);
        return e;
    }

    // The two tables use opposite polarity and reading either as "1 means on" is the trap these helpers
    // exist to keep out of the writers. Confirmed against Flowline's own workflow deactivation, which sets
    // statecode 0 for Draft, and against PACX's plugin step disable, which sets statecode 1 for Disabled.
    [Theory]
    [InlineData(0, false)] // Draft
    [InlineData(1, true)]  // Activated
    [InlineData(2, false)] // Suspended — neither on nor off, treated as not-active so a declared-on file acts
    public void IsWorkflowActive_MapsStatecode(int statecode, bool expected)
    {
        SolutionComponentInventory.IsWorkflowActive(Workflow(statecode)).Should().Be(expected);
    }

    // Draft and Suspended both read as not-active above, which is why the apply needs this second question:
    // a flow that stopped itself is not the same as one that was never started.
    [Theory]
    [InlineData(0, false)] // Draft
    [InlineData(1, false)] // Activated
    [InlineData(2, true)]  // Suspended
    public void IsWorkflowSuspended_SeparatesSuspendedFromDraft(int statecode, bool expected)
    {
        SolutionComponentInventory.IsWorkflowSuspended(Workflow(statecode)).Should().Be(expected);
    }

    [Theory]
    [InlineData(0, true)]  // Enabled
    [InlineData(1, false)] // Disabled
    public void IsStepEnabled_MapsStatecode_WithOppositePolarityToWorkflow(int statecode, bool expected)
    {
        SolutionComponentInventory.IsStepEnabled(Step(statecode)).Should().Be(expected);
    }

    [Fact]
    public void StateHelpers_DisagreeOnTheSameStatecode()
    {
        // Guards the inversion explicitly: statecode 0 means off for a workflow and on for a step.
        SolutionComponentInventory.IsWorkflowActive(Workflow(0)).Should().BeFalse();
        SolutionComponentInventory.IsStepEnabled(Step(0)).Should().BeTrue();
    }

    [Fact]
    public void MissingStatecode_ReadsAsNotActiveAndNotEnabled()
    {
        SolutionComponentInventory.IsWorkflowActive(new Entity("workflow")).Should().BeFalse();
        SolutionComponentInventory.IsStepEnabled(new Entity("sdkmessageprocessingstep")).Should().BeFalse();
    }

    static SolutionInventory Inventory(params InventoryComponent[] components) => new(components);

    static InventoryComponent Flow(string name) =>
        new(ConfigurableComponentKind.CloudFlow, name, Guid.NewGuid(), false);

    [Fact]
    public void Match_SingleName_ReturnsTheComponent()
    {
        var inventory = Inventory(Flow("order_processing"), Flow("welcome_email"));

        var match = inventory.Match(ConfigurableComponentKind.CloudFlow, "order_processing");

        match.Component!.Name.Should().Be("order_processing");
        match.Ambiguous.Should().BeEmpty();
        match.NotFound.Should().BeFalse();
    }

    [Fact]
    public void Match_IsCaseInsensitive()
    {
        var inventory = Inventory(Flow("order_processing"));

        inventory.Match(ConfigurableComponentKind.CloudFlow, "ORDER_PROCESSING").Component.Should().NotBeNull();
    }

    [Fact]
    public void Match_AbsentName_IsNotFound()
    {
        var inventory = Inventory(Flow("order_processing"));

        var match = inventory.Match(ConfigurableComponentKind.CloudFlow, "missing_flow");

        match.NotFound.Should().BeTrue();
        match.Component.Should().BeNull();
    }

    // KTD9: Dataverse enforces uniqueness on none of these name columns, so two matches happens in normal
    // use. Picking one arbitrarily could switch on the wrong flow in production.
    [Fact]
    public void Match_TwoComponentsWithTheSameName_ReturnsBothAndSelectsNeither()
    {
        var inventory = Inventory(Flow("shared_name"), Flow("shared_name"), Flow("other"));

        var match = inventory.Match(ConfigurableComponentKind.CloudFlow, "shared_name");

        match.Component.Should().BeNull();
        match.Ambiguous.Should().HaveCount(2);
        match.NotFound.Should().BeFalse("ambiguous is a different outcome from absent");
    }

    [Fact]
    public void Match_SameNameInAnotherKind_DoesNotCollide()
    {
        var inventory = Inventory(
            Flow("shared_name"),
            new InventoryComponent(ConfigurableComponentKind.PluginStep, "shared_name", Guid.NewGuid(), true));

        inventory.Match(ConfigurableComponentKind.CloudFlow, "shared_name").Component.Should().NotBeNull();
        inventory.Match(ConfigurableComponentKind.PluginStep, "shared_name").Component.Should().NotBeNull();
    }

    [Fact]
    public void OfKind_FiltersToOneClass()
    {
        var inventory = Inventory(
            Flow("a"),
            new InventoryComponent(ConfigurableComponentKind.EnvironmentVariable, "cr123_Url", Guid.NewGuid(), null));

        inventory.OfKind(ConfigurableComponentKind.EnvironmentVariable).Should().ContainSingle()
            .Which.Enabled.Should().BeNull("a value class carries no state");
    }

    // componenttype 29 is the whole Process family, not just flows: business rules, business process flows,
    // dialogs, actions and desktop flows are all `workflow` rows carrying a statecode. An unfiltered read
    // handed every one of them to the state writer, so a pull wrote them into the settings file and an apply
    // of that file deactivated them. The narrowing has to happen in the query, not after it.
    [Fact]
    public async Task ReadAsync_AsksDataverseForTheProcessCategoriesItCanName()
    {
        QueryExpression? workflowQuery = null;
        var service = Substitute.For<IOrganizationServiceAsync2>();
        service.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.Arg<QueryExpression>();
                if (query.EntityName == "solutioncomponent")
                    return Task.FromResult(new EntityCollection([SolutionComponent(Guid.NewGuid())]));
                if (query.EntityName == "workflow") workflowQuery = query;
                return Task.FromResult(new EntityCollection([]));
            });

        await SolutionComponentInventory.ReadAsync(service, "Contoso", CancellationToken.None);

        var category = workflowQuery!.Criteria.Conditions.Single(c => c.AttributeName == "category");
        category.Operator.Should().Be(ConditionOperator.In);
        category.Values.Should().BeEquivalentTo([0, 2, 3, 4, 5],
            "the inline surface switches the whole process family, but a category with no kind to map to " +
            "must not come back: 1 is the deprecated dialog, 6 and 7 have unconfirmed state semantics");
    }

    // Both sections write the same table, so the category is the only thing that puts a row in one section
    // rather than the other. A cloud flow landing under Workflows would still apply correctly but would move
    // between sections on the next pull, churning the file.
    [Theory]
    [InlineData(5, ConfigurableComponentKind.CloudFlow)]
    [InlineData(0, ConfigurableComponentKind.Workflow)]
    public async Task ReadAsync_SplitsProcessRowsByCategory(int category, ConfigurableComponentKind expected)
    {
        var id = Guid.NewGuid();
        var service = Substitute.For<IOrganizationServiceAsync2>();
        service.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.Arg<QueryExpression>();
                if (query.EntityName == "solutioncomponent")
                    return Task.FromResult(new EntityCollection([SolutionComponent(id)]));
                if (query.EntityName == "workflow")
                    return Task.FromResult(new EntityCollection([WorkflowRow(id, "order_processing", category)]));
                return Task.FromResult(new EntityCollection([]));
            });

        var inventory = await SolutionComponentInventory.ReadAsync(service, "Contoso", CancellationToken.None);

        inventory.Components.Should().ContainSingle().Which.Kind.Should().Be(expected);
    }

    // The user-visible bug: a business process flow showed up in a pulled settings file. Proves the row
    // cannot reach the inventory even when Dataverse hands it back, which asserting the query condition
    // alone does not.
    [Theory]
    [InlineData(1)] // Dialog, deprecated
    [InlineData(6)] // Desktop Flow, state semantics unconfirmed
    [InlineData(7)] // AI Flow, state semantics unconfirmed
    public async Task ReadAsync_UnmappedProcessCategory_NeverReachesTheInventory(int category)
    {
        var id = Guid.NewGuid();
        var service = Substitute.For<IOrganizationServiceAsync2>();
        service.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.Arg<QueryExpression>();
                if (query.EntityName == "solutioncomponent")
                    return Task.FromResult(new EntityCollection([SolutionComponent(id)]));
                if (query.EntityName == "workflow")
                    return Task.FromResult(new EntityCollection([WorkflowRow(id, "msdyn_bpf_d3d97bac", category)]));
                return Task.FromResult(new EntityCollection([]));
            });

        var inventory = await SolutionComponentInventory.ReadAsync(service, "Contoso", CancellationToken.None);

        inventory.Components.Should().BeEmpty();
    }

    // KTD25: the read widened and the file did not. This is the regression the widening most risks — a
    // business rule reaching the inventory is correct, a business rule reaching a settings file is the
    // bug the old two-category filter existed to prevent.
    [Theory]
    [InlineData(2, ConfigurableComponentKind.BusinessRule)]
    [InlineData(3, ConfigurableComponentKind.Action)]
    [InlineData(4, ConfigurableComponentKind.BusinessProcessFlow)]
    public async Task ReadAsync_ProcessCategory_ArrivesAsItsOwnKind(
        int category, ConfigurableComponentKind expected)
    {
        var inventory = await ReadOneWorkflowAsync(category);

        inventory.Components.Should().ContainSingle().Which.Kind.Should().Be(expected);
    }

    // Every class the inventory reads is one a settings file has a section for. That was not true when
    // the process read first widened — business rules, business process flows and actions were readable
    // and switchable but never captured — and the set is kept as the one place that says so, because the
    // next class to arrive may well not have a section.
    [Fact]
    public void EveryKindTheInventoryReads_CanBeDeclaredInAFile() =>
        Enum.GetValues<ConfigurableComponentKind>()
            .Should().OnlyContain(k => ConfigurableComponentKinds.IsFileManaged(k));

    [Theory]
    [InlineData(0, ConfigurableComponentKind.Workflow)]
    [InlineData(5, ConfigurableComponentKind.CloudFlow)]
    public async Task ReadAsync_TheOriginalTwoCategories_StillMapAsBefore(
        int category, ConfigurableComponentKind expected)
    {
        var inventory = await ReadOneWorkflowAsync(category);

        inventory.Components.Should().ContainSingle().Which.Kind.Should().Be(expected);
    }

    // Dataverse generates a business process flow's unique name from the backing entity it creates, so
    // it arrives as msdyn_bpf_<guid>: unreadable in a list and untypeable at a prompt. The display name is
    // what the file declares too, so the two agree on one addressing key.
    [Fact]
    public async Task ReadAsync_ABusinessProcessFlow_IsAddressedByItsDisplayName()
    {
        var inventory = await ReadOneWorkflowAsync(
            WorkflowCategoryBusinessProcessFlow, "msdyn_bpf_d3d97bac8c294105840e99e37a9d1c39", "Rijopdracht intake");

        inventory.Components.Should().ContainSingle().Which.Name.Should().Be("Rijopdracht intake");
    }

    // A business rule has no unique name at all (null on every one, confirmed against a live solution), so
    // its name has always been the display name — and "Set date" says nothing about which table it governs.
    // Qualifying it by table is the same fix forms and views needed, for the same reason.
    [Fact]
    public async Task ReadAsync_ABusinessRule_IsQualifiedByItsTable()
    {
        var inventory = await ReadOneWorkflowAsync(
            WorkflowCategoryBusinessRule, uniqueName: null, displayName: "Set date", primaryEntity: "opdracht");

        var rule = inventory.Components.Should().ContainSingle().Subject;
        rule.Name.Should().Be("opdracht.Set date");
        rule.Table.Should().Be("opdracht");
    }

    // A rule with no primary entity is still addressable, just unqualified. Dropping it would make it
    // invisible, which is worse than an unqualified name.
    [Fact]
    public async Task ReadAsync_ABusinessRuleWithNoTable_KeepsItsBareName()
    {
        var inventory = await ReadOneWorkflowAsync(
            WorkflowCategoryBusinessRule, uniqueName: null, displayName: "Set date", primaryEntity: null);

        inventory.Components.Should().ContainSingle().Which.Name.Should().Be("Set date");
    }

    // The qualification is deliberately not applied to every class. A cloud flow, a classic workflow, an
    // action and a business process flow each name themselves, so prefixing a table would be noise to read
    // and a longer thing to type. Only names that mean nothing alone get qualified.
    [Theory]
    [InlineData(WorkflowCategoryClassic)]
    [InlineData(WorkflowCategoryAction)]
    [InlineData(WorkflowCategoryModernFlow)]
    public async Task ReadAsync_TheSelfNamingClasses_AreNotQualifiedByTable(int category)
    {
        var inventory = await ReadOneWorkflowAsync(
            category, uniqueName: "contoso_Thing", displayName: "Thing", primaryEntity: "account");

        inventory.Components.Should().ContainSingle().Which.Name.Should().Be("contoso_Thing");
    }

    // Every other class keeps the unique name, which is stable across renames and is what a settings file
    // keys on. Business rules are absent because they have no unique name to keep: the column is null on
    // every one of them, so they are addressed by table and display name instead.
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public async Task ReadAsync_EveryOtherProcessClass_KeepsItsUniqueName(int category)
    {
        var inventory = await ReadOneWorkflowAsync(category, "contoso_Unique", "Some Display Name");

        inventory.Components.Should().ContainSingle().Which.Name.Should().Be("contoso_Unique");
    }

    const int WorkflowCategoryClassic = 0;
    const int WorkflowCategoryBusinessRule = 2;
    const int WorkflowCategoryAction = 3;
    const int WorkflowCategoryBusinessProcessFlow = 4;
    const int WorkflowCategoryModernFlow = 5;

    static Task<SolutionInventory> ReadOneWorkflowAsync(int category) =>
        ReadOneWorkflowAsync(category, "contoso_Thing", null);

    static Task<SolutionInventory> ReadOneWorkflowAsync(
        int category, string? uniqueName, string? displayName, string? primaryEntity) =>
        ReadOneWorkflowAsync(category, uniqueName!, displayName, primaryEntity, qualified: true);

    static async Task<SolutionInventory> ReadOneWorkflowAsync(
        int category, string uniqueName, string? displayName, string? primaryEntity = null, bool qualified = false)
    {
        var id = Guid.NewGuid();
        var service = Substitute.For<IOrganizationServiceAsync2>();
        service.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.Arg<QueryExpression>();
                if (query.EntityName == "solutioncomponent")
                    return Task.FromResult(new EntityCollection([SolutionComponent(id)]));
                if (query.EntityName == "workflow")
                {
                    var row = WorkflowRow(id, uniqueName, category);
                    if (displayName is not null) row["name"] = displayName;
                    if (qualified && uniqueName is null) row["uniquename"] = null;
                    if (primaryEntity is not null) row["primaryentity"] = primaryEntity;
                    return Task.FromResult(new EntityCollection([row]));
                }
                return Task.FromResult(new EntityCollection([]));
            });

        return await SolutionComponentInventory.ReadAsync(service, "Contoso", CancellationToken.None);
    }

    static Entity SolutionComponent(Guid objectId)
    {
        var e = new Entity("solutioncomponent", Guid.NewGuid());
        e["objectid"] = objectId;
        e["componenttype"] = new OptionSetValue(SolutionComponentInventory.WorkflowComponentType);
        return e;
    }

    static Entity WorkflowRow(Guid id, string uniqueName, int category)
    {
        var e = new Entity("workflow", id);
        e["uniquename"] = uniqueName;
        e["statecode"] = new OptionSetValue(1);
        e["category"] = new OptionSetValue(category);
        return e;
    }
}
