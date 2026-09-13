using FluentAssertions;
using Flowline.Core.Configure;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Xunit;

namespace Flowline.Core.Tests.Configure;

/// <summary>Reading main forms and public views out of a solution.</summary>
public class FormAndViewInventoryTests
{
    static Entity Component(Guid objectId, int componentType)
    {
        var e = new Entity("solutioncomponent", Guid.NewGuid());
        e["objectid"] = objectId;
        e["componenttype"] = new OptionSetValue(componentType);
        return e;
    }

    static Entity FormRow(Guid id, string table, string name, int type, int activationState)
    {
        var e = new Entity("systemform", id);
        e["name"] = name;
        e["objecttypecode"] = table;
        e["type"] = new OptionSetValue(type);
        e["formactivationstate"] = new OptionSetValue(activationState);
        return e;
    }

    static Entity ViewRow(Guid id, string table, string name, int queryType, int stateCode, bool isDefault)
    {
        var e = new Entity("savedquery", id);
        e["name"] = name;
        e["returnedtypecode"] = table;
        e["querytype"] = queryType;
        e["statecode"] = new OptionSetValue(stateCode);
        e["isdefault"] = isDefault;
        return e;
    }

    // The service filters by type and querytype in the query itself, so the stub returns only what those
    // filters would have let through. What is asserted is the shape of the component that comes out.
    static async Task<SolutionInventory> ReadAsync(IEnumerable<Entity> forms, IEnumerable<Entity> views)
    {
        var formRows = forms.ToList();
        var viewRows = views.ToList();

        var service = Substitute.For<IOrganizationServiceAsync2>();
        service.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<QueryExpression>().EntityName switch
            {
                "solutioncomponent" => new EntityCollection([
                    .. formRows.Select(f => Component(f.Id, SolutionComponentInventory.SystemFormComponentType)),
                    .. viewRows.Select(v => Component(v.Id, SolutionComponentInventory.SavedQueryComponentType)),
                ]),
                "systemform" => new EntityCollection(formRows),
                "savedquery" => new EntityCollection(viewRows),
                _ => new EntityCollection([]),
            }));

        return await SolutionComponentInventory.ReadAsync(service, "Contoso", CancellationToken.None);
    }

    // A bare form name addresses nothing: every table has an Information form, and a solution holding
    // twenty tables holds twenty of them.
    [Fact]
    public async Task ReadAsync_AForm_IsAddressedByTableAndName()
    {
        var inventory = await ReadAsync([FormRow(Guid.NewGuid(), "account", "Information", 2, 1)], []);

        var form = inventory.Components.Should().ContainSingle().Subject;
        form.Kind.Should().Be(ConfigurableComponentKind.Form);
        form.Name.Should().Be("account.Information");
        form.Table.Should().Be("account");
        form.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_AView_IsAddressedByTableAndName()
    {
        var inventory = await ReadAsync([], [ViewRow(Guid.NewGuid(), "account", "Active Accounts", 0, 0, false)]);

        var view = inventory.Components.Should().ContainSingle().Subject;
        view.Kind.Should().Be(ConfigurableComponentKind.View);
        view.Name.Should().Be("account.Active Accounts");
        view.Table.Should().Be("account");
        view.Enabled.Should().BeTrue();
    }

    // formactivationstate is 1 Active / 0 Inactive, and savedquery statecode is 0 Active / 1 Inactive.
    // Opposite polarity on two tables read by the same service, which is why each is pinned.
    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task ReadAsync_FormActivationState_ReadsOneAsActive(int raw, bool expected)
    {
        var inventory = await ReadAsync([FormRow(Guid.NewGuid(), "account", "Information", 2, raw)], []);

        inventory.Components.Should().ContainSingle().Which.Enabled.Should().Be(expected);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    public async Task ReadAsync_ViewStateCode_ReadsZeroAsActive(int raw, bool expected)
    {
        var inventory = await ReadAsync([], [ViewRow(Guid.NewGuid(), "account", "Active Accounts", 0, raw, false)]);

        inventory.Components.Should().ContainSingle().Which.Enabled.Should().Be(expected);
    }

    // Dataverse refuses to deactivate a default view, so the flag has to survive the read for the writer
    // to be able to say so before attempting it.
    [Fact]
    public async Task ReadAsync_ADefaultView_CarriesThatItIsTheDefault()
    {
        var inventory = await ReadAsync([], [ViewRow(Guid.NewGuid(), "account", "My Active Accounts", 0, 0, true)]);

        inventory.Components.Should().ContainSingle().Which.IsDefault.Should().BeTrue();
    }

    // A row with no table is not addressable, and half a name is worse than none: it would collide with
    // every other form whose table failed to read.
    [Fact]
    public async Task ReadAsync_AFormWithNoTable_IsDropped()
    {
        var row = FormRow(Guid.NewGuid(), "account", "Information", 2, 1);
        row["objecttypecode"] = null;

        var inventory = await ReadAsync([row], []);

        inventory.Components.Should().BeEmpty();
    }

    [Fact]
    public async Task ReadAsync_FormsAndViewsTogether_BothArrive()
    {
        var inventory = await ReadAsync(
            [FormRow(Guid.NewGuid(), "account", "Information", 2, 1)],
            [ViewRow(Guid.NewGuid(), "contact", "Active Contacts", 0, 0, false)]);

        inventory.Components.Select(c => c.Name)
            .Should().BeEquivalentTo(["account.Information", "contact.Active Contacts"]);
    }

    // The query, not a LINQ filter afterwards, is what keeps quick create forms, dashboards and
    // advanced-find views out. Asserting the conditions is the only way to pin that without a live
    // environment, and getting it wrong would admit form types that accept a write and ignore it.
    [Fact]
    public async Task ReadAsync_FiltersToMainFormsAndPublicViews_InTheQueryItself()
    {
        var queries = new List<QueryExpression>();
        var service = Substitute.For<IOrganizationServiceAsync2>();
        service.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var query = call.Arg<QueryExpression>();
                queries.Add(query);
                return Task.FromResult(query.EntityName == "solutioncomponent"
                    ? new EntityCollection([
                        Component(Guid.NewGuid(), SolutionComponentInventory.SystemFormComponentType),
                        Component(Guid.NewGuid(), SolutionComponentInventory.SavedQueryComponentType),
                    ])
                    : new EntityCollection([]));
            });

        await SolutionComponentInventory.ReadAsync(service, "Contoso", CancellationToken.None);

        Condition(queries, "systemform", "type").Should().Be(SolutionComponentInventory.FormTypeMain);
        Condition(queries, "savedquery", "querytype").Should().Be(SolutionComponentInventory.QueryTypePublicView);
    }

    static object? Condition(List<QueryExpression> queries, string table, string column) =>
        queries.Single(q => q.EntityName == table).Criteria.Conditions
            .Single(c => c.AttributeName == column).Values.Single();
}
