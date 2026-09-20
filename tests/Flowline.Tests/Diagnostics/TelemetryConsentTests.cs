using Flowline.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Flowline.Tests.Diagnostics;

public class TelemetryConsentTests
{
    const string ConnectionString = "InstrumentationKey=fake;";

    static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(v => v.Name == name).Value;

    // Connection string gate — beats everything else

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsEnabled_EmptyOrWhitespaceConnectionString_IsOffRegardlessOfOtherInputs(string? connectionString)
    {
        TelemetryConsent.IsEnabled(connectionString, Env(("FLOWLINE_TELEMETRY_OPTOUT", "0")), true).Should().BeFalse();
    }

    // Opt-out environment variables

    [Theory]
    [InlineData("FLOWLINE_TELEMETRY_OPTOUT")]
    [InlineData("PP_TOOLS_TELEMETRY_OPTOUT")]
    public void IsEnabled_OptOutVariableSetToOne_IsOff(string variableName)
    {
        TelemetryConsent.IsEnabled(ConnectionString, Env((variableName, "1")), null).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_FlowlineOptOut_DisablesIndependentlyOfPacVariable()
    {
        TelemetryConsent.IsEnabled(ConnectionString, Env(("FLOWLINE_TELEMETRY_OPTOUT", "1")), null).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_PacOptOut_DisablesIndependentlyOfFlowlineVariable()
    {
        TelemetryConsent.IsEnabled(ConnectionString, Env(("PP_TOOLS_TELEMETRY_OPTOUT", "1")), null).Should().BeFalse();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("")]
    public void IsEnabled_NonOptOutValues_DoNotDisable(string value)
    {
        TelemetryConsent.IsEnabled(ConnectionString, Env(("FLOWLINE_TELEMETRY_OPTOUT", value)), null).Should().BeTrue();
        TelemetryConsent.IsEnabled(ConnectionString, Env(("PP_TOOLS_TELEMETRY_OPTOUT", value)), null).Should().BeTrue();
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("TRUE")]
    public void IsEnabled_RecognisedTruthyValues_CaseInsensitiveAndTrimmed_DoDisable(string value)
    {
        TelemetryConsent.IsEnabled(ConnectionString, Env(("FLOWLINE_TELEMETRY_OPTOUT", $"  {value}  ")), null).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_UnrecognisedValue_IsNotAnOptOut()
    {
        TelemetryConsent.IsEnabled(ConnectionString, Env(("FLOWLINE_TELEMETRY_OPTOUT", "maybe")), null).Should().BeTrue();
    }

    // No signal at all

    [Fact]
    public void IsEnabled_NoVariableAndNoPacSignal_IsOn()
    {
        TelemetryConsent.IsEnabled(ConnectionString, _ => null, null).Should().BeTrue();
    }

    // pac signal

    [Fact]
    public void IsEnabled_PacFalse_IsOff()
    {
        TelemetryConsent.IsEnabled(ConnectionString, _ => null, false).Should().BeFalse();
    }

    [Fact]
    public void IsEnabled_PacTrue_DoesNotEnableByItself_AndVariableStillWins()
    {
        TelemetryConsent.IsEnabled(ConnectionString, _ => null, true).Should().BeTrue();
        TelemetryConsent.IsEnabled(ConnectionString, Env(("FLOWLINE_TELEMETRY_OPTOUT", "1")), true).Should().BeFalse();
    }
}
