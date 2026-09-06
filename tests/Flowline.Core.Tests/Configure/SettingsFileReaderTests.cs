using FluentAssertions;
using Flowline.Core;
using Flowline.Core.Configure;
using Xunit;

namespace Flowline.Core.Tests.Configure;

public class SettingsFileReaderTests
{
    // R12a/KTD on PAC-generated sections: `pac solution create-settings` owns the PAC half of this file and
    // Microsoft grows it unannounced — CopilotAgents arrived in real files while the published parameter docs
    // still listed two sections. A reader that models those sections would drop whatever lands next, so the
    // contract under test is that anything Flowline does not own survives a read-modify-write untouched.
    [Fact]
    public void ReadThenWrite_PacNativeSectionsOnly_RoundTripsUnchanged()
    {
        var json = """
            {
              "EnvironmentVariables": [
                {
                  "SchemaName": "cr123_ApiUrl",
                  "Value": "https://example.invalid"
                }
              ],
              "ConnectionReferences": [
                {
                  "LogicalName": "cr123_shared_dataverse",
                  "ConnectionId": "",
                  "ConnectorId": "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps"
                }
              ]
            }
            """;

        var document = SettingsFileReader.Parse(json);
        var written = SettingsFileReader.Write(document);

        written.Should().Be(json.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void ReadThenWrite_UnknownTopLevelSection_IsRetained()
    {
        var json = """
            {
              "EnvironmentVariables": [],
              "ConnectionReferences": [],
              "CopilotAgents": [
                {
                  "SchemaName": "cr123_agent"
                }
              ]
            }
            """;

        var written = SettingsFileReader.Write(SettingsFileReader.Parse(json));

        written.Should().Contain("CopilotAgents");
        written.Should().Contain("cr123_agent");
    }

    [Fact]
    public void Parse_FlowlineSections_AreTypedAndRemovedFromPassThrough()
    {
        var json = """
            {
              "EnvironmentVariables": [],
              "Flows": [ { "Name": "Order Processing", "Enabled": false } ],
              "PluginSteps": [ { "Name": "Contoso.Plugins.OnCreate: Create of account", "Enabled": false } ]
            }
            """;

        var document = SettingsFileReader.Parse(json);

        document.Flows.Should().ContainSingle()
            .Which.Should().Be(new ComponentStateEntry("Order Processing", false));
        document.PluginSteps.Should().ContainSingle()
            .Which.Name.Should().Be("Contoso.Plugins.OnCreate: Create of account");
        document.PassThrough.Should().ContainKey("EnvironmentVariables");
        document.PassThrough.Should().NotContainKey(SettingsDocument.FlowsProperty);
    }

    // R12c: a second pull against an unchanged environment must produce a byte-identical file, or every run
    // leaves a spurious diff. The contract is same-file-in-same-file-out, not order-independence across
    // differently-built documents: sorting sections would reorder every file pac create-settings already
    // wrote, which is the churn this requirement exists to prevent.
    [Fact]
    public void Write_ReadWriteRead_IsByteStableAcrossRepeatedPulls()
    {
        var json = SettingsFileReader.Write(SettingsFileReader.Parse("""
            {
              "ConnectionReferences": [],
              "EnvironmentVariables": [],
              "Flows": [ { "Name": "B flow", "Enabled": false }, { "Name": "A flow", "Enabled": false } ]
            }
            """));

        var second = SettingsFileReader.Write(SettingsFileReader.Parse(json));

        second.Should().Be(json);
    }

    // The written file is committed, so its line endings must not depend on which OS produced it.
    [Fact]
    public void Write_UsesLineFeedRegardlessOfPlatform()
    {
        var written = SettingsFileReader.Write(SettingsFileReader.Parse("""{ "EnvironmentVariables": [] }"""));

        written.Should().NotContain("\r");
    }

    // KTD1: a malformed file is ConfigInvalid (11), not an unhandled JsonException surfacing as a stack trace.
    [Fact]
    public void Parse_MalformedJson_ThrowsConfigInvalid()
    {
        var act = () => SettingsFileReader.Parse("{ \"EnvironmentVariables\": [ ");

        act.Should().Throw<FlowlineException>()
            .Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
    }

    [Fact]
    public void Parse_JsonThatIsNotAnObject_ThrowsConfigInvalid()
    {
        var act = () => SettingsFileReader.Parse("[]");

        act.Should().Throw<FlowlineException>()
            .Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
    }
}
