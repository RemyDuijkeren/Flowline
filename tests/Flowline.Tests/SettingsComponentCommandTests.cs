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

    // KTD25: two command classes, and --type is what says which kind. Getting this mapping wrong would
    // silently address the wrong table.

    [Theory]
    [InlineData(SettingsStateCommand.StateType.Flow, ConfigurableComponentKind.CloudFlow)]
    [InlineData(SettingsStateCommand.StateType.Workflow, ConfigurableComponentKind.Workflow)]
    [InlineData(SettingsStateCommand.StateType.Rule, ConfigurableComponentKind.BusinessRule)]
    [InlineData(SettingsStateCommand.StateType.Bpf, ConfigurableComponentKind.BusinessProcessFlow)]
    [InlineData(SettingsStateCommand.StateType.Action, ConfigurableComponentKind.Action)]
    [InlineData(SettingsStateCommand.StateType.Plugin, ConfigurableComponentKind.PluginStep)]
    [InlineData(SettingsStateCommand.StateType.Form, ConfigurableComponentKind.Form)]
    [InlineData(SettingsStateCommand.StateType.View, ConfigurableComponentKind.View)]
    public void StateTypes_MapToTheirKind(SettingsStateCommand.StateType type, ConfigurableComponentKind expected) =>
        SettingsStateCommand.KindFor(type).Should().Be(expected);

    [Theory]
    [InlineData(SettingsValueCommand.ValueType.EnvVar, ConfigurableComponentKind.EnvironmentVariable)]
    [InlineData(SettingsValueCommand.ValueType.ConnRef, ConfigurableComponentKind.ConnectionReference)]
    public void ValueTypes_MapToTheirKind(SettingsValueCommand.ValueType type, ConfigurableComponentKind expected) =>
        SettingsValueCommand.KindFor(type).Should().Be(expected);

    // Every value the parser accepts maps to its own kind, and between them the two commands cover every
    // class the inventory can hold. A type the parser accepts but the map does not would throw at runtime
    // on an invocation that parsed cleanly.
    [Fact]
    public void EveryTypeTheParserAccepts_MapsToADistinctKind()
    {
        var kinds = Enum.GetValues<SettingsStateCommand.StateType>().Select(SettingsStateCommand.KindFor)
            .Concat(Enum.GetValues<SettingsValueCommand.ValueType>().Select(SettingsValueCommand.KindFor))
            .ToArray();

        kinds.Should().OnlyHaveUniqueItems();
        kinds.Should().BeEquivalentTo(Enum.GetValues<ConfigurableComponentKind>());
    }

    // The two commands between them address exactly what the inventory carries, with nothing in both.
    [Fact]
    public void TheTwoCommands_PartitionTheKinds()
    {
        ConfigurableComponentKinds.WithState.Should().NotIntersectWith(ConfigurableComponentKinds.WithValue);

        ConfigurableComponentKinds.WithState.Concat(ConfigurableComponentKinds.WithValue)
            .Should().BeEquivalentTo(Enum.GetValues<ConfigurableComponentKind>());
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

    // The kind travels on the component now, not as a separate argument, so a test about a value kind
    // has to build a component of one.
    static SingleComponentOutcome Outcome(SingleComponentActionKind action, bool suspended = false,
        bool? priorEnabled = null, string? priorValue = null,
        ConfigurableComponentKind kind = ConfigurableComponentKind.CloudFlow) =>
        new(new InventoryComponent(kind, "ApprovalFlow", Guid.NewGuid(), true),
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

        SettingsComponentOutcomes.DescribeCurrent(outcome)
            .Should().Be("suspended");
    }

    [Theory]
    [InlineData(true, "on")]
    [InlineData(false, "off")]
    public void AnUnsuspendedStateKind_ReadsAsOnOrOff(bool enabled, string expected)
    {
        var outcome = Outcome(SingleComponentActionKind.Read, priorEnabled: enabled);

        SettingsComponentOutcomes.DescribeCurrent(outcome)
            .Should().Be(expected);
    }

    [Fact]
    public void AValueKindWithNoValue_ReadsAsUnset()
    {
        var outcome = Outcome(SingleComponentActionKind.Read, priorValue: null,
            kind: ConfigurableComponentKind.EnvironmentVariable);

        SettingsComponentOutcomes.DescribeCurrent(outcome)
            .Should().Be("unset");
    }

    [Fact]
    public void AValueKindWithAValue_ReadsAsThatValue()
    {
        var outcome = Outcome(SingleComponentActionKind.Read, priorValue: "https://api.contoso.com",
            kind: ConfigurableComponentKind.ConnectionReference);

        SettingsComponentOutcomes.DescribeCurrent(outcome)
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

    // R9, R9c: the picker exists so nobody has to know the exact name, so the label carries the name the
    // search filters on and that the caller would otherwise have typed. The state leads it, because a
    // plugin step's name runs past a hundred characters and a trailing state wrapped out of sight.

    static InventoryComponent Component(ConfigurableComponentKind kind, string name, bool? enabled = null,
        bool suspended = false, string? value = null) =>
        new(kind, name, Guid.NewGuid(), enabled, value, Suspended: suspended);

    [Fact]
    public void APickerLabel_EndsWithTheAddressableName()
    {
        SettingsComponentOutcomes
            .DescribeCandidate(Component(ConfigurableComponentKind.CloudFlow, "ApprovalFlow", enabled: true), typeWidth: 0)
            .Should().EndWith("ApprovalFlow");
    }

    [Theory]
    [InlineData(true, false, "on")]
    [InlineData(false, false, "off")]
    [InlineData(false, true, "suspended")]
    public void AStateKindLabel_SaysWhatItCurrentlyIs(bool enabled, bool suspended, string expected)
    {
        SettingsComponentOutcomes
            .DescribeCandidate(Component(ConfigurableComponentKind.Workflow, "contoso_AutoNumber", enabled, suspended), typeWidth: 0)
            .Should().Contain(expected);
    }

    // A shape as well as a word: the glyph is the column the eye runs down, the word is what the
    // picker's search matches. Suspended gets a half-filled circle rather than a second hollow one,
    // because an operator who reads a stopped-itself flow as off turns it on and it stops again.
    [Theory]
    [InlineData(true, false, "\u25cf")]
    [InlineData(false, false, "\u25cb")]
    [InlineData(false, true, "\u25d0")]
    public void AStateKindLabel_LeadsWithItsOwnShape(bool enabled, bool suspended, string glyph)
    {
        SettingsComponentOutcomes
            .Describe(Component(ConfigurableComponentKind.Workflow, "contoso_AutoNumber", enabled, suspended), typeWidth: 0)
            .Should().StartWith(glyph);
    }

    // A value kind shows its name alone. Putting the value in the label would print a Dataverse-stored
    // secret into the picker — an exposure the read path already accepts and this one need not add to.
    [Fact]
    public void AValueKindLabel_CarriesNoValue()
    {
        var label = SettingsComponentOutcomes.DescribeCandidate(
            Component(ConfigurableComponentKind.ConnectionReference, "contoso_Mailbox", value: "super-secret-id"),
            typeWidth: 0);

        label.Should().Be("connref  contoso_Mailbox");
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
            typeWidth: 0);

        // The property that matters is that the renderer accepts it, not how it is spelled.
        var act = () => new Markup(label);

        act.Should().NotThrow();
    }

    [Fact]
    public void APickerLabel_RendersTheBracketsBackAsTyped()
    {
        var label = SettingsComponentOutcomes.DescribeCandidate(
            Component(ConfigurableComponentKind.EnvironmentVariable, "[Account] url"),
            typeWidth: 0);

        var console = new TestConsole();
        console.Write(new Markup(label));

        console.Output.Should().Contain("[Account] url");
    }
}
