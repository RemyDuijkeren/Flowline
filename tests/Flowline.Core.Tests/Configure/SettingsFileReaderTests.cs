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
            """.ReplaceLineEndings("\n");

        var document = SettingsFileReader.Parse(json);
        var written = SettingsFileReader.Write(document);

        // Normalised going in, not just coming out. The writer inherits the source document's endings, so
        // comparing against a normalised expectation made this test depend on how git checked out its own
        // source file: LF as authored, CRLF on a Windows clone.
        written.Should().Be(json);
    }

    // R12c, verified against a real `pac solution create-settings` run: PAC writes CRLF and no trailing
    // newline on Windows. Pinning LF rewrote every line of the file PAC had just produced, so the first
    // pull was a whole-file diff burying the one entry that actually changed. The ending is inherited for
    // the same reason property order is.
    [Fact]
    public void ReadThenWrite_CrlfSourceFile_KeepsCrlf()
    {
        var json = "{\r\n  \"EnvironmentVariables\": [],\r\n  \"CopilotAgents\": []\r\n}";

        var written = SettingsFileReader.Write(SettingsFileReader.Parse(json));

        written.Should().Be(json);
        written.Should().NotEndWith("\n\n", "PAC writes no trailing newline and neither does Flowline");
    }

    [Fact]
    public void ReadThenWrite_LfSourceFile_KeepsLf()
    {
        var json = "{\n  \"EnvironmentVariables\": [],\n  \"CopilotAgents\": []\n}";

        SettingsFileReader.Write(SettingsFileReader.Parse(json)).Should().Be(json);
    }

    // System.Text.Json defaults to HTML-safe escaping. A URL with a query string is an ordinary environment
    // variable value, and writing it back as an escape sequence both mangles a hand-edited file and differs
    // from the bytes PAC produced for the same value.
    [Fact]
    public void ReadThenWrite_ValueWithHtmlSignificantCharacters_IsNotEscaped()
    {
        var json = """
            {
              "EnvironmentVariables": [
                {
                  "SchemaName": "cr123_Url",
                  "Value": "https://x.invalid/?a=1&b=2"
                }
              ]
            }
            """.ReplaceLineEndings("\n");

        var written = SettingsFileReader.Write(SettingsFileReader.Parse(json));

        written.Should().Contain("?a=1&b=2");
        written.Should().NotContain("u0026", "HTML-safe escaping would mangle a hand-edited value");
        written.Should().Be(json);
    }

    // A document Flowline builds rather than reads gets LF, so a settings file born on a Windows machine
    // and one born in Linux CI are byte-identical.
    [Fact]
    public void Write_DocumentWithNoSourceFile_UsesLf()
    {
        var document = new SettingsDocument { Flows = { new ComponentStateEntry("order_processing", false) } };

        SettingsFileReader.Write(document).Should().NotContain("\r");
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

    // A pull overwrites a file the team committed and filled in by hand. A write that failed halfway used to
    // leave that file truncated, destroying work no re-run can reconstruct.
    [Fact]
    public void Save_WhenTheWriteFails_LeavesTheExistingFileIntact()
    {
        var folder = Directory.CreateTempSubdirectory("flowline-save-").FullName;
        var path = Path.Combine(folder, "deploymentSettings.prod.json");
        const string original = """{ "EnvironmentVariables": [ { "SchemaName": "cr123_Url", "Value": "keep-me" } ] }""";
        File.WriteAllText(path, original);

        try
        {
            // Read-only is the cheapest real failure: the move into place is refused after the replacement
            // has already been written to the temp file, which is exactly the window that used to truncate.
            File.SetAttributes(path, FileAttributes.ReadOnly);

            var act = () => SettingsFileReader.Save(new SettingsDocument(), path);

            act.Should().Throw<UnauthorizedAccessException>();

            File.SetAttributes(path, FileAttributes.Normal);
            File.ReadAllText(path).Should().Be(original);
            Directory.GetFiles(folder, "*.tmp").Should().BeEmpty("a failed write cleans up after itself");
        }
        finally
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Save_OverAnExistingFile_ReplacesItAndLeavesNoTempBehind()
    {
        var folder = Directory.CreateTempSubdirectory("flowline-save-").FullName;
        var path = Path.Combine(folder, "deploymentSettings.prod.json");
        File.WriteAllText(path, """{ "EnvironmentVariables": [] }""");

        try
        {
            var document = SettingsFileReader.Parse(
                """{ "EnvironmentVariables": [ { "SchemaName": "cr123_New", "Value": "" } ] }""");

            SettingsFileReader.Save(document, path);

            File.ReadAllText(path).Should().Contain("cr123_New");
            Directory.GetFiles(folder, "*.tmp").Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
