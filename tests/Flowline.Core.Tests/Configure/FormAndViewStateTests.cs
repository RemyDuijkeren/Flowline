using FluentAssertions;
using Flowline.Core.Configure;
using Flowline.Core.Models;
using System.ServiceModel;
using Microsoft.Crm.Sdk.Messages;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Xunit;

namespace Flowline.Core.Tests.Configure;

/// <summary>
/// Main forms and public views as declarable state.
/// </summary>
/// <remarks>
/// Every claim these tests encode was settled against a live environment rather than from documentation,
/// because the behaviour is not documented and two of the four answers are counter-intuitive:
///
///   - deactivating the last active main form on a table is refused by Dataverse;
///   - a quick view or card form accepts the write and silently ignores it, which is why only main forms
///     are admitted;
///   - a form change is invisible to the app until the table is published;
///   - a view change is live immediately.
/// </remarks>
public class FormAndViewStateTests
{
    static IOrganizationServiceAsync2 Service()
    {
        var service = Substitute.For<IOrganizationServiceAsync2>();
        service.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(new EntityCollection()));
        return service;
    }

    static InventoryComponent Form(string table, string name, bool active) =>
        new(ConfigurableComponentKind.Form, $"{table}.{name}", Guid.NewGuid(), active, Table: table);

    static InventoryComponent View(string table, string name, bool active, bool isDefault = false) =>
        new(ConfigurableComponentKind.View, $"{table}.{name}", Guid.NewGuid(), active,
            Table: table, IsDefault: isDefault);

    // A form has no statecode. Activation is its own column and writing a statuscode alongside it would be
    // rejected, so the shape of this update is the claim worth pinning.
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void BuildStateUpdate_Form_WritesFormActivationStateAlone(bool enabled, int expected)
    {
        var update = ComponentStateWriter.BuildStateUpdate(Form("account", "Information", !enabled), enabled);

        update!.LogicalName.Should().Be("systemform");
        update.GetAttributeValue<OptionSetValue>("formactivationstate").Value.Should().Be(expected);
        update.Contains("statecode").Should().BeFalse();
        update.Contains("statuscode").Should().BeFalse();
    }

    // A view is on at statecode 0, the opposite of a workflow. Getting this backwards would invert every
    // declaration in a file, which is the reason each table's polarity is pinned rather than assumed.
    [Theory]
    [InlineData(true, 0, 1)]
    [InlineData(false, 1, 2)]
    public void BuildStateUpdate_View_WritesStateAndStatusTogether(bool enabled, int state, int status)
    {
        var update = ComponentStateWriter.BuildStateUpdate(View("account", "Active Accounts", !enabled), enabled);

        update!.LogicalName.Should().Be("savedquery");
        update.GetAttributeValue<OptionSetValue>("statecode").Value.Should().Be(state);
        update.GetAttributeValue<OptionSetValue>("statuscode").Value.Should().Be(status);
    }

    // Dataverse answers "Default views cannot be deactivated" as a plain fault, which reaches an operator
    // as an error code that says nothing about what to do instead. This is the one refusal a user will
    // actually hit, so it is answered before the write rather than relayed after it.
    [Fact]
    public async Task Apply_DeactivatingADefaultView_FailsWithoutCallingDataverse()
    {
        var service = Service();

        var outcome = await ComponentStateWriter.ApplyAsync(
            service, View("account", "My Active Accounts", active: true, isDefault: true),
            desiredEnabled: false, RunMode.Normal, CancellationToken.None, currentlySuspended: false);

        outcome.Outcome.Should().Be(ComponentOutcomeKind.Failed);
        outcome.Detail.Should().Contain("default view").And.Contain("Make another view the default");
        await service.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    // Activating one is ordinary: the refusal is specific to switching it off.
    [Fact]
    public async Task Apply_ActivatingADefaultView_IsNotRefused()
    {
        var service = Service();

        var outcome = await ComponentStateWriter.ApplyAsync(
            service, View("account", "My Active Accounts", active: false, isDefault: true),
            desiredEnabled: true, RunMode.Normal, CancellationToken.None, currentlySuspended: false);

        outcome.Outcome.Should().Be(ComponentOutcomeKind.Applied);
        await service.Received(1).UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Forms", ConfigurableComponentKind.Form, "systemform")]
    [InlineData("Views", ConfigurableComponentKind.View, "savedquery")]
    public async Task Apply_ADeclaredFormOrView_IsSwitchedLikeAnyOther(
        string section, ConfigurableComponentKind kind, string table)
    {
        var service = Service();
        var written = new List<string>();
        service.When(s => s.UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>()))
            .Do(call => written.Add(call.Arg<Entity>().LogicalName));

        var document = SettingsFileReader.Parse($$"""{ "{{section}}": { "account.Thing": false } }""");
        var component = kind == ConfigurableComponentKind.Form
            ? Form("account", "Thing", true)
            : View("account", "Thing", true);

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(service, document, new SolutionInventory([component]), RunMode.Normal, CancellationToken.None);

        outcome.Applied.Should().Be(1);
        written.Should().Equal(table);
    }

    // The flood this would otherwise be. A solution carries dozens of forms and views in the state they
    // should already be in; listing each one an operator has not declared would bury the entries that
    // matter under hundreds of lines telling them to declare things they have no reason to.
    [Fact]
    public async Task Apply_UndeclaredFormsAndViews_AreNotReported()
    {
        var service = Service();
        var document = SettingsFileReader.Parse("""{ "CloudFlows": { "declared_flow": true } }""");

        var inventory = new SolutionInventory([
            new InventoryComponent(ConfigurableComponentKind.CloudFlow, "declared_flow", Guid.NewGuid(), true),
            Form("account", "Information", false),
            View("account", "Active Accounts", false),
            new InventoryComponent(ConfigurableComponentKind.BusinessRule, "contoso_Rule", Guid.NewGuid(), false),
        ]);

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(service, document, inventory, RunMode.Normal, CancellationToken.None);

        // All three are off and undeclared, so all three would qualify on the state test alone. Only the
        // business rule is reported: forms and views are excluded from capture outright (KTD29).
        outcome.Undeclared.Should().ContainSingle().Which.Should().Contain("contoso_Rule");
    }

    // ── KTD33: undeclared reports what a fresh deploy would lose ─────────────

    // A component that is on and undeclared needs no line. On is what an import produces, so the file and
    // the environment already agree and there is nothing a capture would add. This is what made the old
    // report useless: a solution's components are mostly on, so it listed forty of them every run.
    [Fact]
    public async Task Apply_AnUndeclaredComponentThatIsOn_IsNotReported()
    {
        var service = Service();
        var document = SettingsFileReader.Parse("""{ "CloudFlows": { "declared_flow": true } }""");

        var inventory = new SolutionInventory([
            new InventoryComponent(ConfigurableComponentKind.CloudFlow, "declared_flow", Guid.NewGuid(), true),
            new InventoryComponent(ConfigurableComponentKind.PluginStep, "contoso_StepThatIsOn", Guid.NewGuid(), true),
            new InventoryComponent(ConfigurableComponentKind.PluginStep, "contoso_StepThatIsOff", Guid.NewGuid(), false),
        ]);

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(service, document, inventory, RunMode.Normal, CancellationToken.None);

        outcome.Undeclared.Should().ContainSingle().Which.Should().Contain("contoso_StepThatIsOff");
    }

    // A value class has no on to be in, so absence is the whole gap: nothing has said what the variable
    // should be, and a fresh environment gets no value at all.
    [Theory]
    [InlineData(ConfigurableComponentKind.EnvironmentVariable, "contoso_ApiUrl")]
    [InlineData(ConfigurableComponentKind.ConnectionReference, "contoso_Dataverse")]
    public async Task Apply_AnUndeclaredValueClass_IsAlwaysReported(ConfigurableComponentKind kind, string name)
    {
        var service = Service();
        var document = SettingsFileReader.Parse("""{ "CloudFlows": {} }""");

        var outcome = await new ConfigureApplyService().ApplyAsync(
            service, document,
            new SolutionInventory([new InventoryComponent(kind, name, Guid.NewGuid(), null)]),
            RunMode.Normal, CancellationToken.None);

        outcome.Undeclared.Should().ContainSingle().Which.Should().Contain(name);
    }

    // A form's write lands but the app keeps showing the old state until the table is published, so the
    // run publishes. The table is the smallest scope that works: the publish schema has no node for a
    // form, and the two narrower shapes that look like one are accepted and publish nothing.
    [Fact]
    public async Task Apply_SwitchingForms_NamesEachTableOnceForPublishing()
    {
        var service = Service();
        var document = SettingsFileReader.Parse("""
            {
              "Forms": { "account.Old": false, "account.New": true, "contact.Old": false },
              "Views": { "account.Active Accounts": false }
            }
            """);

        var inventory = new SolutionInventory([
            Form("account", "Old", true), Form("account", "New", false), Form("contact", "Old", true),
            View("account", "Active Accounts", true),
        ]);

        var outcome = await new ConfigureApplyService()
            .ApplyAsync(service, document, inventory, RunMode.Normal, CancellationToken.None);

        // Two tables, not three writes. A view is live without a publish, so it never appears here.
        outcome.TablesNeedingPublish.Should().Equal("account", "contact");
    }

    // The publish itself, not just the list. One request per table, and a view never provokes one.
    [Fact]
    public async Task Apply_SwitchingForms_PublishesEachTableOnce()
    {
        var service = Service();
        var published = new List<string>();
        service.ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (call.Arg<OrganizationRequest>() is PublishXmlRequest publish)
                    published.Add(publish.ParameterXml);
                return Task.FromResult<OrganizationResponse>(new PublishXmlResponse());
            });

        var document = SettingsFileReader.Parse("""
            {
              "Forms": { "account.Old": false, "contact.Old": false },
              "Views": { "account.Active Accounts": false }
            }
            """);

        var inventory = new SolutionInventory([
            Form("account", "Old", true), Form("contact", "Old", true), View("account", "Active Accounts", true),
        ]);

        await new ConfigureApplyService()
            .ApplyAsync(service, document, inventory, RunMode.Normal, CancellationToken.None);

        published.Should().Equal(
            "<importexportxml><entities><entity>account</entity></entities></importexportxml>",
            "<importexportxml><entities><entity>contact</entity></entities></importexportxml>");
    }

    // A refused publish is not a failed run: the state change was written and stands, and only the step
    // that makes it visible did not happen. Re-running the publish is safe.
    [Fact]
    public async Task Apply_WhenThePublishIsRefused_TheWriteStillCounts()
    {
        var service = Service();
        service.ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<OrganizationResponse>>(_ => throw new FaultException<OrganizationServiceFault>(
                new OrganizationServiceFault { ErrorCode = -2147204784 }, "refused"));

        var document = SettingsFileReader.Parse("""{ "Forms": { "account.Old": false } }""");

        var outcome = await new ConfigureApplyService().ApplyAsync(
            service, document, new SolutionInventory([Form("account", "Old", true)]),
            RunMode.Normal, CancellationToken.None);

        outcome.Applied.Should().Be(1);
        outcome.Failed.Should().Be(0);
        outcome.PublishFailures.Should().ContainSingle().Which.Table.Should().Be("account");
    }

    // A view change is live the moment it is written, confirmed against a live environment, so publishing
    // for one would be a table-wide republish nothing asked for.
    [Fact]
    public async Task Apply_SwitchingOnlyViews_PublishesNothing()
    {
        var service = Service();
        var published = new List<string>();
        service.ExecuteAsync(Arg.Any<OrganizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                if (call.Arg<OrganizationRequest>() is PublishXmlRequest publish)
                    published.Add(publish.ParameterXml);
                return Task.FromResult<OrganizationResponse>(new PublishXmlResponse());
            });

        var document = SettingsFileReader.Parse("""{ "Views": { "account.Active Accounts": false } }""");

        await new ConfigureApplyService().ApplyAsync(
            service, document, new SolutionInventory([View("account", "Active Accounts", true)]),
            RunMode.Normal, CancellationToken.None);

        published.Should().BeEmpty();
    }

    // A dry run wrote nothing, so there is nothing pending to publish.
    [Fact]
    public async Task Apply_DryRun_NamesNoTablesForPublishing()
    {
        var service = Service();
        var document = SettingsFileReader.Parse("""{ "Forms": { "account.Old": false } }""");

        var outcome = await new ConfigureApplyService().ApplyAsync(
            service, document, new SolutionInventory([Form("account", "Old", true)]),
            RunMode.DryRun, CancellationToken.None);

        outcome.TablesNeedingPublish.Should().BeEmpty();
    }

    // The bug this pins: a run that switched a component checked whether the file disagreed by consulting
    // a map that listed only cloud flows, classic workflows and plugin steps. For every other class the
    // answer was "the file says nothing", so the run never offered to bring the file into line and the
    // next push silently undid the change. Found by switching a view off, declaring it off, then switching
    // it back on and being asked nothing.
    //
    // Written over every state class rather than over the ones that were broken, because the defect was
    // not a wrong entry but a missing one, and only a test that enumerates the classes catches the next
    // omission.
    [Fact]
    public void WouldOverride_EveryStateClass_NoticesTheFileDeclaringTheOpposite()
    {
        foreach (var kind in ConfigurableComponentKinds.WithState)
        {
            var document = new SettingsDocument();
            document.StateSection(kind)!.Add(new ComponentStateEntry("thing", false));

            ConfigureApplyService.WouldOverride(document, kind, "thing", writtenEnabled: true)
                .Should().BeTrue($"{kind} declared off but switched on is an override");

            ConfigureApplyService.WouldOverride(document, kind, "thing", writtenEnabled: false)
                .Should().BeFalse($"{kind} declared off and switched off agrees");
        }
    }

    // The other half of the same map: a class the file can declare must have a section to declare it in.
    [Fact]
    public void EveryStateClass_HasASectionInTheDocument() =>
        ConfigurableComponentKinds.WithState.Should()
            .OnlyContain(k => new SettingsDocument().StateSection(k) != null);

    // A value class has no state section, and asking for one must answer null rather than land on some
    // other class's list.
    [Fact]
    public void AValueClass_HasNoStateSection() =>
        ConfigurableComponentKinds.WithValue.Should()
            .OnlyContain(k => new SettingsDocument().StateSection(k) == null);

    [Fact]
    public void FormsAndViews_CanBeDeclared_ButAreNeverCaptured()
    {
        foreach (var kind in new[] { ConfigurableComponentKind.Form, ConfigurableComponentKind.View })
        {
            ConfigurableComponentKinds.IsFileManaged(kind).Should().BeTrue();
            ConfigurableComponentKinds.IsCaptured(kind).Should().BeFalse();
        }
    }
}
