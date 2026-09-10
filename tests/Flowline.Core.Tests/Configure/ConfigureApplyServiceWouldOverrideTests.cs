using System.Text.Json.Nodes;
using FluentAssertions;
using Flowline.Core.Configure;

namespace Flowline.Core.Tests.Configure;

/// <summary>
/// KTD12: the inline surface warns that the next push will undo the change it just made, and the honest
/// question is whether a push would actually move the component — not whether the file mentions it. An
/// empty value is skipped by the apply tiers, and a file that already declares what was just written
/// would apply the same thing again. Warning in either case trains an operator to ignore the warning.
/// </summary>
public class ConfigureApplyServiceWouldOverrideTests
{
    static SettingsDocument WithValues(string section, string nameProperty, string valueProperty,
        params (string Name, string Value)[] entries)
    {
        var array = new JsonArray();
        foreach (var (name, value) in entries)
            array.Add(new JsonObject { [nameProperty] = name, [valueProperty] = value });

        var document = new SettingsDocument();
        document.PassThrough[section] = array;
        return document;
    }

    [Fact]
    public void AVariableWithAValue_WouldBeApplied() =>
        ConfigureApplyService.WouldOverride(
                WithValues("EnvironmentVariables", "SchemaName", "Value", ("contoso_ApiUrl", "https://api.contoso.com")),
                ConfigurableComponentKind.EnvironmentVariable, "contoso_ApiUrl", writtenValue: "https://staging.contoso.com")
            .Should().BeTrue();

    // The whole point of the check. `pac solution create-settings` writes an empty Value for a variable
    // nobody has filled in, so a file full of empty entries names half the solution and would override
    // none of it.
    [Fact]
    public void AVariableWithAnEmptyValue_WouldNotBeApplied() =>
        ConfigureApplyService.WouldOverride(
                WithValues("EnvironmentVariables", "SchemaName", "Value", ("contoso_ApiUrl", "")),
                ConfigurableComponentKind.EnvironmentVariable, "contoso_ApiUrl", writtenValue: "https://api.contoso.com")
            .Should().BeFalse();

    [Fact]
    public void AConnectionReferenceWithAnEmptyConnectionId_WouldNotBeApplied() =>
        ConfigureApplyService.WouldOverride(
                WithValues("ConnectionReferences", "LogicalName", "ConnectionId", ("contoso_Mailbox", "")),
                ConfigurableComponentKind.ConnectionReference, "contoso_Mailbox", writtenValue: "a1b2c3")
            .Should().BeFalse();

    [Fact]
    public void AConnectionReferenceWithAConnectionId_WouldBeApplied() =>
        ConfigureApplyService.WouldOverride(
                WithValues("ConnectionReferences", "LogicalName", "ConnectionId", ("contoso_Mailbox", "a1b2c3")),
                ConfigurableComponentKind.ConnectionReference, "contoso_Mailbox", writtenValue: "d4e5f6")
            .Should().BeTrue();

    // A state has no inert form: declaring a flow at all declares it, so any entry would be applied.
    [Fact]
    public void ADeclaredFlowState_WouldBeApplied()
    {
        var document = new SettingsDocument { CloudFlows = { new ComponentStateEntry("ApprovalFlow", false) } };

        ConfigureApplyService.WouldOverride(document, ConfigurableComponentKind.CloudFlow, "ApprovalFlow",
                writtenEnabled: true)
            .Should().BeTrue();
    }

    [Fact]
    public void AComponentTheFileDoesNotName_WouldNotBeApplied()
    {
        var document = new SettingsDocument { CloudFlows = { new ComponentStateEntry("ApprovalFlow", false) } };

        ConfigureApplyService.WouldOverride(document, ConfigurableComponentKind.CloudFlow, "SomeOtherFlow",
                writtenEnabled: true)
            .Should().BeFalse();
    }

    // The apply path matches names case-insensitively, so the prediction has to as well — otherwise a
    // change would be silently overridden by a file the warning said nothing about.
    [Fact]
    public void MatchingIsCaseInsensitive_LikeTheApplyPath()
    {
        var document = new SettingsDocument { Workflows = { new ComponentStateEntry("contoso_AutoNumber", true) } };

        ConfigureApplyService.WouldOverride(document, ConfigurableComponentKind.Workflow, "CONTOSO_AUTONUMBER",
                writtenEnabled: false)
            .Should().BeTrue();
    }

    [Fact]
    public void AKindTheFileHasNoSectionFor_WouldNotBeApplied() =>
        ConfigureApplyService.WouldOverride(new SettingsDocument(), ConfigurableComponentKind.PluginStep, "anything",
                writtenEnabled: true)
            .Should().BeFalse();

    // The direction that was wrong before: a file agreeing with what was just written would apply the same
    // thing again, so warning that it "will put it back" describes a change that never happens.
    [Fact]
    public void AFileDeclaringTheStateThatWasJustWritten_WouldNotOverride()
    {
        var document = new SettingsDocument { CloudFlows = { new ComponentStateEntry("ApprovalFlow", false) } };

        ConfigureApplyService.WouldOverride(document, ConfigurableComponentKind.CloudFlow, "ApprovalFlow",
                writtenEnabled: false)
            .Should().BeFalse();
    }

    [Fact]
    public void AFileDeclaringTheValueThatWasJustWritten_WouldNotOverride() =>
        ConfigureApplyService.WouldOverride(
                WithValues("EnvironmentVariables", "SchemaName", "Value", ("contoso_ApiUrl", "https://api.contoso.com")),
                ConfigurableComponentKind.EnvironmentVariable, "contoso_ApiUrl",
                writtenValue: "https://api.contoso.com")
            .Should().BeFalse();

    // Values compare exactly. A connection id differing only in case is a different connection.
    [Fact]
    public void AValueDifferingOnlyInCase_WouldOverride() =>
        ConfigureApplyService.WouldOverride(
                WithValues("ConnectionReferences", "LogicalName", "ConnectionId", ("contoso_Mailbox", "A1B2C3")),
                ConfigurableComponentKind.ConnectionReference, "contoso_Mailbox", writtenValue: "a1b2c3")
            .Should().BeTrue();
}
