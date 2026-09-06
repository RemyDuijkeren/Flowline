using FluentAssertions;
using Flowline.Core.Configure;
using Flowline.Core.Models;
using Microsoft.PowerPlatform.Dataverse.Client;
using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Query;
using NSubstitute;
using Xunit;

namespace Flowline.Core.Tests.Configure;

public class ConfigurePullServiceTests
{
    /// <summary>A service whose value-row lookup returns nothing unless a value is given.</summary>
    static IOrganizationServiceAsync2 Service(string? environmentVariableValue = null)
    {
        var service = Substitute.For<IOrganizationServiceAsync2>();
        var rows = new EntityCollection();

        if (environmentVariableValue is not null)
            rows.Entities.Add(new Entity("environmentvariablevalue", Guid.NewGuid()) { ["value"] = environmentVariableValue });

        service.RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(rows));

        return service;
    }

    static SettingsDocument Doc(string json) => SettingsFileReader.Parse(json);

    static InventoryComponent Variable(string name, int? type = null, int? secretStore = null) =>
        new(ConfigurableComponentKind.EnvironmentVariable, name, Guid.NewGuid(), null, Type: type, SecretStore: secretStore);

    static InventoryComponent Reference(string name, string? connectionId) =>
        new(ConfigurableComponentKind.ConnectionReference, name, Guid.NewGuid(), null, CurrentValue: connectionId);

    static InventoryComponent Flow(string name, bool enabled) =>
        new(ConfigurableComponentKind.Flow, name, Guid.NewGuid(), enabled);

    static InventoryComponent Step(string name, bool enabled) =>
        new(ConfigurableComponentKind.PluginStep, name, Guid.NewGuid(), enabled);

    // The skeleton is what a real `pac solution create-settings` run emits: values empty, ConnectorId
    // present, and a third section beyond the two the published parameter docs list.
    const string PacSkeleton = """
        {
          "EnvironmentVariables": [
            {
              "SchemaName": "cr123_ApiUrl",
              "Value": ""
            }
          ],
          "ConnectionReferences": [
            {
              "LogicalName": "cr123_dataverse",
              "ConnectionId": "",
              "ConnectorId": "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps"
            }
          ],
          "CopilotAgents": []
        }
        """;

    // ── Live capture ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Build_FillsEmptyValuesFromTheEnvironment()
    {
        var inventory = new SolutionInventory([Variable("cr123_ApiUrl"), Reference("cr123_dataverse", "conn-guid")]);

        var result = await new ConfigurePullService()
            .BuildAsync(Service("https://live.invalid"), Doc(PacSkeleton), null, inventory, CancellationToken.None);

        var written = SettingsFileReader.Write(result.Document);

        written.Should().Contain("https://live.invalid");
        written.Should().Contain("conn-guid");
    }

    // R12a: the section Microsoft added without warning survives a pull, because it is carried rather than
    // modelled. This is the whole reason the skeleton comes from pac instead of being generated here.
    [Fact]
    public async Task Build_CarriesAPacSectionFlowlineDoesNotModel()
    {
        var result = await new ConfigurePullService().BuildAsync(
            Service(), Doc(PacSkeleton), null, new SolutionInventory([]), CancellationToken.None);

        result.Document.PassThrough.Should().ContainKey("CopilotAgents");
    }

    [Fact]
    public async Task Build_ConnectorIdIsUntouched()
    {
        var result = await new ConfigurePullService().BuildAsync(
            Service(), Doc(PacSkeleton), null, new SolutionInventory([]), CancellationToken.None);

        SettingsFileReader.Write(result.Document)
            .Should().Contain("shared_commondataserviceforapps");
    }

    // ── Merge (R12b) ─────────────────────────────────────────────────────────

    // R12 asks a pull to capture live values and R12b asks it to preserve what the file already has. They
    // only conflict for an entry that has both, and the file wins: someone who pinned a value meant it, and
    // overwriting it would silently revert the decision on every pull.
    [Fact]
    public async Task Build_ValueAlreadyInTheFile_IsPreservedOverTheLiveOne()
    {
        var existing = Doc("""
            {
              "EnvironmentVariables": [ { "SchemaName": "cr123_ApiUrl", "Value": "https://pinned.invalid" } ],
              "ConnectionReferences": []
            }
            """);

        var result = await new ConfigurePullService().BuildAsync(
            Service("https://live.invalid"), Doc(PacSkeleton), existing,
            new SolutionInventory([Variable("cr123_ApiUrl")]), CancellationToken.None);

        var written = SettingsFileReader.Write(result.Document);

        written.Should().Contain("https://pinned.invalid");
        written.Should().NotContain("https://live.invalid");
    }

    // AE5a/R12b: a solution that gained a variable gets a new entry, and the values already in the file are
    // left alone.
    [Fact]
    public async Task Build_NewComponent_IsAddedAndReported()
    {
        var existing = Doc("""{ "EnvironmentVariables": [], "ConnectionReferences": [] }""");

        var result = await new ConfigurePullService().BuildAsync(
            Service("https://live.invalid"), Doc(PacSkeleton), existing,
            new SolutionInventory([Variable("cr123_ApiUrl")]), CancellationToken.None);

        result.Added.Should().Contain(a => a.Contains("cr123_ApiUrl"));
    }

    // R12b: an entry whose component is gone is reported rather than dropped. A name that disappeared from
    // one environment may still matter in another, and deleting a line someone wrote is the worse failure.
    [Fact]
    public async Task Build_EntryWhoseComponentIsGone_IsKeptAndReported()
    {
        var existing = Doc("""
            {
              "EnvironmentVariables": [ { "SchemaName": "cr123_Retired", "Value": "still-here" } ],
              "ConnectionReferences": []
            }
            """);

        var result = await new ConfigurePullService().BuildAsync(
            Service(), Doc(PacSkeleton), existing, new SolutionInventory([]), CancellationToken.None);

        result.Vanished.Should().Contain(v => v.Contains("cr123_Retired"));
        SettingsFileReader.Write(result.Document).Should().Contain("cr123_Retired");
    }

    // ── Determinism (R12c) ───────────────────────────────────────────────────

    [Fact]
    public async Task Build_PulledTwiceAgainstAnUnchangedEnvironment_IsByteIdentical()
    {
        var inventory = new SolutionInventory(
        [
            Variable("cr123_ApiUrl"),
            Reference("cr123_dataverse", "conn-guid"),
            Flow("nightly_sync", false),
            Step("audit_step", false),
        ]);

        var service = new ConfigurePullService();

        var first = await service.BuildAsync(
            Service("https://live.invalid"), Doc(PacSkeleton), null, inventory, CancellationToken.None);
        var firstText = SettingsFileReader.Write(first.Document);

        var second = await service.BuildAsync(
            Service("https://live.invalid"), Doc(PacSkeleton), SettingsFileReader.Parse(firstText), inventory,
            CancellationToken.None);

        SettingsFileReader.Write(second.Document).Should().Be(firstText);
    }

    [Fact]
    public async Task Build_KeepsTheExistingFilesLineEndings()
    {
        var existing = SettingsFileReader.Parse("{\r\n  \"EnvironmentVariables\": []\r\n}");

        var result = await new ConfigurePullService().BuildAsync(
            Service(), Doc(PacSkeleton), existing, new SolutionInventory([]), CancellationToken.None);

        SettingsFileReader.Write(result.Document).Should().Contain("\r\n");
    }

    // ── State classes (R12) ──────────────────────────────────────────────────

    // Deploy already activates what it imports, and absence means untouched, so an entry saying a component
    // is on describes what would have happened anyway. Only the deliberate exceptions are worth a line.
    [Fact]
    public async Task Build_WritesOnlyTheComponentsThatAreOff()
    {
        var inventory = new SolutionInventory(
        [
            Flow("nightly_sync", false),
            Flow("order_processing", true),
            Step("audit_step", false),
            Step("validation_step", true),
        ]);

        var result = await new ConfigurePullService().BuildAsync(
            Service(), Doc(PacSkeleton), null, inventory, CancellationToken.None);

        result.Document.Flows.Should().ContainSingle().Which.Name.Should().Be("nightly_sync");
        result.Document.PluginSteps.Should().ContainSingle().Which.Name.Should().Be("audit_step");
    }

    // Someone who wrote "Enabled": true wrote it on purpose — a pull that dropped every on-state entry would
    // erase the record of a deliberate re-enable.
    [Fact]
    public async Task Build_AnOnComponentTheFileAlreadyDeclares_KeepsItsLine()
    {
        var existing = Doc("""
            { "EnvironmentVariables": [], "Flows": [ { "Name": "order_processing", "Enabled": true } ] }
            """);

        var result = await new ConfigurePullService().BuildAsync(
            Service(), Doc(PacSkeleton), existing,
            new SolutionInventory([Flow("order_processing", true)]), CancellationToken.None);

        result.Document.Flows.Should().ContainSingle()
            .Which.Should().Be(new ComponentStateEntry("order_processing", true));
    }

    // ── Secrets (R13) ────────────────────────────────────────────────────────

    // A Key Vault-backed Secret stores a reference — subscription, vault, secret name — not the secret. Those
    // fields are what makes the variable reproducible, and they are not sensitive.
    [Fact]
    public async Task Build_KeyVaultBackedSecret_IsPulledVerbatim()
    {
        var inventory = new SolutionInventory(
            [Variable("cr123_ApiKey", ConfigurePullService.SecretType, ConfigurePullService.KeyVaultSecretStore)]);

        var skeleton = Doc("""{ "EnvironmentVariables": [ { "SchemaName": "cr123_ApiKey", "Value": "" } ] }""");

        var result = await new ConfigurePullService()
            .BuildAsync(Service("{\"vault\":\"contoso-kv\"}"), skeleton, null, inventory, CancellationToken.None);

        SettingsFileReader.Write(result.Document).Should().Contain("contoso-kv");
        result.Placeholders.Should().BeEmpty();
    }

    // Every other store holds the secret itself. R13 fails closed, so the value row is never even queried.
    [Theory]
    [InlineData(1)]      // Microsoft Dataverse
    [InlineData(99)]     // a store this build has never heard of
    [InlineData(null)]   // no store recorded at all
    public async Task Build_SecretNotBackedByKeyVault_WritesAPlaceholderAndReadsNothing(int? secretStore)
    {
        var service = Service("super-secret");
        var inventory = new SolutionInventory(
            [Variable("cr123_ApiKey", ConfigurePullService.SecretType, secretStore)]);

        var skeleton = Doc("""{ "EnvironmentVariables": [ { "SchemaName": "cr123_ApiKey", "Value": "" } ] }""");

        var result = await new ConfigurePullService()
            .BuildAsync(service, skeleton, null, inventory, CancellationToken.None);

        var written = SettingsFileReader.Write(result.Document);

        written.Should().Contain(ConfigurePullService.SecretPlaceholder);
        written.Should().NotContain("super-secret");
        result.Placeholders.Should().ContainSingle().Which.Should().Be("cr123_ApiKey");
        await service.DidNotReceive().RetrieveMultipleAsync(Arg.Any<QueryExpression>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void IsUnreadableSecret_OrdinaryVariable_IsReadable()
    {
        ConfigurePullService.IsUnreadableSecret(Variable("cr123_ApiUrl")).Should().BeFalse();
    }

    // ── AE1: pulled, then applied back, changes nothing ──────────────────────

    // The discriminating round trip. A variable with no live value pulls as an empty string, and apply has to
    // leave it alone — creating an empty value row there would be a write on a component the file is really
    // saying nothing about.
    [Fact]
    public async Task Build_ThenApply_AgainstTheSameEnvironment_WritesNothing()
    {
        var inventory = new SolutionInventory(
        [
            Variable("cr123_ApiUrl"),
            Reference("cr123_dataverse", "conn-guid"),
            Flow("nightly_sync", false),
        ]);

        var pullService = Service();  // no value row: the variable has no live value
        var pulled = await new ConfigurePullService()
            .BuildAsync(pullService, Doc(PacSkeleton), null, inventory, CancellationToken.None);

        var applyService = Service();
        var outcome = await new ConfigureApplyService().ApplyAsync(
            applyService, SettingsFileReader.Parse(SettingsFileReader.Write(pulled.Document)),
            inventory, RunMode.Normal, CancellationToken.None);

        outcome.Failed.Should().Be(0);
        outcome.Skipped.Should().Be(0);
        outcome.Applied.Should().Be(0, "a file applied to the environment it came from changes nothing");
        await applyService.DidNotReceive().CreateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
        await applyService.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }

    // The specific way AE1 failed first: the apply path had no way to see a connection reference's current
    // binding, so it rewrote every one of them on every run — a write against a live environment for a file
    // that declared exactly what was already there.
    [Fact]
    public async Task Build_ThenApply_ConnectionReferenceAlreadyBound_IsUnchanged()
    {
        var inventory = new SolutionInventory([Reference("cr123_dataverse", "conn-guid")]);

        var pulled = await new ConfigurePullService()
            .BuildAsync(Service(), Doc(PacSkeleton), null, inventory, CancellationToken.None);

        var applyService = Service();
        var outcome = await new ConfigureApplyService().ApplyAsync(
            applyService, pulled.Document, inventory, RunMode.Normal, CancellationToken.None);

        outcome.Unchanged.Should().Be(1);
        outcome.Applied.Should().Be(0);
        await applyService.DidNotReceive().UpdateAsync(Arg.Any<Entity>(), Arg.Any<CancellationToken>());
    }
}
