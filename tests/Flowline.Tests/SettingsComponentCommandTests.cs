using FluentAssertions;
using Flowline.Commands;
using Flowline.Core;
using Flowline.Core.Configure;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Flowline.Tests;

public class SettingsComponentCommandTests
{
    // ── The kind is the operation ────────────────────────────────────────────

    // KTD4: two command classes, five operation names, and the invoked name is what says which kind.
    // Getting this mapping wrong would silently address the wrong table.

    [Theory]
    [InlineData("flow", ConfigurableComponentKind.CloudFlow)]
    [InlineData("workflow", ConfigurableComponentKind.Workflow)]
    [InlineData("plugin", ConfigurableComponentKind.PluginStep)]
    public void StateOperations_MapToTheirKind(string operation, ConfigurableComponentKind expected) =>
        SettingsStateCommand.KindFor(operation).Should().Be(expected);

    [Theory]
    [InlineData("envvar", ConfigurableComponentKind.EnvironmentVariable)]
    [InlineData("connref", ConfigurableComponentKind.ConnectionReference)]
    public void ValueOperations_MapToTheirKind(string operation, ConfigurableComponentKind expected) =>
        SettingsValueCommand.KindFor(operation).Should().Be(expected);

    // The five names registered in Program.cs are the five the classes answer to. A name registered but
    // unmapped would throw at runtime on an invocation that parsed cleanly.
    [Fact]
    public void EveryRegisteredOperationName_MapsToADistinctKind()
    {
        var kinds = new[] { "flow", "workflow", "plugin" }.Select(SettingsStateCommand.KindFor)
            .Concat(new[] { "envvar", "connref" }.Select(SettingsValueCommand.KindFor))
            .ToArray();

        kinds.Should().OnlyHaveUniqueItems();
        kinds.Should().HaveCount(5);
    }

    // ── The one contradiction left to check by hand ──────────────────────────

    // KTD5: because the kind is the operation, the parser rejects --value on a flow and --on on a variable
    // with no hand-written check. This pair is what remains.

    [Fact]
    public void OnAndOffTogether_AreRejectedNamingBoth()
    {
        var error = SettingsStateCommand.ValidateStateFlags(on: true, off: true);

        error.Should().NotBeNull();
        error.Should().Contain("--on").And.Contain("--off");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void AnyOtherCombination_IsAccepted(bool on, bool off) =>
        SettingsStateCommand.ValidateStateFlags(on, off).Should().BeNull();

    // ── Exit codes ───────────────────────────────────────────────────────────

    static SingleComponentOutcome Outcome(SingleComponentActionKind action, bool suspended = false,
        bool? priorEnabled = null, string? priorValue = null) =>
        new(new InventoryComponent(ConfigurableComponentKind.CloudFlow, "ApprovalFlow", Guid.NewGuid(), true),
            action, priorEnabled, priorValue, null, suspended);

    [Theory]
    [InlineData(SingleComponentActionKind.Read, ExitCode.Success)]
    [InlineData(SingleComponentActionKind.Applied, ExitCode.Success)]
    [InlineData(SingleComponentActionKind.Unchanged, ExitCode.Success)]
    [InlineData(SingleComponentActionKind.Skipped, ExitCode.ValidationFailed)]
    [InlineData(SingleComponentActionKind.Failed, ExitCode.PartialSuccess)]
    public void EachOutcome_EarnsItsTypedCode(SingleComponentActionKind action, ExitCode expected) =>
        SettingsComponentOutcomes.ExitCodeFor(Outcome(action)).Should().Be(expected);

    // KTD8: Inconclusive means a run compared nothing, which cannot happen once a name has resolved to
    // exactly one component. It is the code the file-apply path uses and this path deliberately does not.
    [Fact]
    public void NoOutcome_EverEarnsInconclusiveOrGeneralError()
    {
        var codes = Enum.GetValues<SingleComponentActionKind>()
            .Select(a => SettingsComponentOutcomes.ExitCodeFor(Outcome(a)))
            .ToArray();

        codes.Should().NotContain(ExitCode.Inconclusive);
        codes.Should().NotContain(ExitCode.GeneralError);
    }

    // ── What a read says ─────────────────────────────────────────────────────

    // A suspended flow reads as suspended, not off. Dataverse flattens Suspended to not-enabled, so an
    // operator told "off" would turn it on and be surprised when it stops itself again.
    [Fact]
    public void ASuspendedFlow_ReadsAsSuspendedRatherThanOff()
    {
        var outcome = Outcome(SingleComponentActionKind.Read, suspended: true, priorEnabled: false);

        SettingsComponentOutcomes.DescribeCurrent(outcome, ConfigurableComponentKind.CloudFlow)
            .Should().Be("suspended");
    }

    [Theory]
    [InlineData(true, "on")]
    [InlineData(false, "off")]
    public void AnUnsuspendedStateKind_ReadsAsOnOrOff(bool enabled, string expected)
    {
        var outcome = Outcome(SingleComponentActionKind.Read, priorEnabled: enabled);

        SettingsComponentOutcomes.DescribeCurrent(outcome, ConfigurableComponentKind.CloudFlow)
            .Should().Be(expected);
    }

    [Fact]
    public void AValueKindWithNoValue_ReadsAsUnset()
    {
        var outcome = Outcome(SingleComponentActionKind.Read, priorValue: null);

        SettingsComponentOutcomes.DescribeCurrent(outcome, ConfigurableComponentKind.EnvironmentVariable)
            .Should().Be("unset");
    }

    [Fact]
    public void AValueKindWithAValue_ReadsAsThatValue()
    {
        var outcome = Outcome(SingleComponentActionKind.Read, priorValue: "https://api.contoso.com");

        SettingsComponentOutcomes.DescribeCurrent(outcome, ConfigurableComponentKind.ConnectionReference)
            .Should().Be("https://api.contoso.com");
    }

    // ── Kind names in sentences ──────────────────────────────────────────────

    // The operation names are abbreviations; the messages are not. "No connrefs in this solution" is not
    // a sentence anyone writes.
    [Fact]
    public void EveryKind_HasASingularAndAPluralLabel()
    {
        foreach (var kind in Enum.GetValues<ConfigurableComponentKind>())
        {
            SettingsComponentNames.Singular(kind).Should().NotBeNullOrWhiteSpace();
            SettingsComponentNames.Plural(kind).Should().NotBeNullOrWhiteSpace();
            SettingsComponentNames.Singular(kind).Should().NotBe(kind.ToString());
        }
    }

    // ── Picker labels ────────────────────────────────────────────────────────

    // R9: the picker exists so nobody has to know the exact name, so the label leads with the name the
    // search filters on and that the caller would otherwise have typed.

    static InventoryComponent Component(ConfigurableComponentKind kind, string name, bool? enabled = null,
        bool suspended = false, string? value = null) =>
        new(kind, name, Guid.NewGuid(), enabled, value, Suspended: suspended);

    [Fact]
    public void APickerLabel_LeadsWithTheAddressableName()
    {
        SettingsComponentOutcomes
            .DescribeCandidate(Component(ConfigurableComponentKind.CloudFlow, "ApprovalFlow", enabled: true),
                ConfigurableComponentKind.CloudFlow)
            .Should().StartWith("ApprovalFlow");
    }

    [Theory]
    [InlineData(true, false, "on")]
    [InlineData(false, false, "off")]
    [InlineData(false, true, "suspended")]
    public void AStateKindLabel_SaysWhatItCurrentlyIs(bool enabled, bool suspended, string expected)
    {
        SettingsComponentOutcomes
            .DescribeCandidate(Component(ConfigurableComponentKind.Workflow, "contoso_AutoNumber", enabled, suspended),
                ConfigurableComponentKind.Workflow)
            .Should().EndWith(expected);
    }

    // A value kind shows its name alone. Putting the value in the label would print a Dataverse-stored
    // secret into the picker — an exposure the read path already accepts and this one need not add to.
    [Fact]
    public void AValueKindLabel_CarriesNoValue()
    {
        var label = SettingsComponentOutcomes.DescribeCandidate(
            Component(ConfigurableComponentKind.ConnectionReference, "contoso_Mailbox", value: "super-secret-id"),
            ConfigurableComponentKind.ConnectionReference);

        label.Should().Be("contoso_Mailbox");
        label.Should().NotContain("super-secret-id");
    }

    // A component name is whatever someone typed in the maker portal, and Spectre parses a selection
    // prompt's converter output as markup. A flow called "[Account] nightly sync" crashed the picker with
    // "Could not find color or style 'Account'" — square brackets are ordinary in a flow name and a style
    // tag to the renderer.
    [Fact]
    public void APickerLabel_SurvivesAComponentNameThatLooksLikeMarkup()
    {
        var label = SettingsComponentOutcomes.DescribeCandidate(
            Component(ConfigurableComponentKind.CloudFlow, "[Account] nightly sync", enabled: true),
            ConfigurableComponentKind.CloudFlow);

        // The property that matters is that the renderer accepts it, not how it is spelled.
        var act = () => new Markup(label);

        act.Should().NotThrow();
    }

    [Fact]
    public void APickerLabel_RendersTheBracketsBackAsTyped()
    {
        var label = SettingsComponentOutcomes.DescribeCandidate(
            Component(ConfigurableComponentKind.EnvironmentVariable, "[Account] url"),
            ConfigurableComponentKind.EnvironmentVariable);

        var console = new TestConsole();
        console.Write(new Markup(label));

        console.Output.Should().Contain("[Account] url");
    }
}
