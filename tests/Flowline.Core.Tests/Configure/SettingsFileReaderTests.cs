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
        var document = new SettingsDocument { CloudFlows = { new ComponentStateEntry("order_processing", false) } };

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
              "CloudFlows": { "Order Processing": false },
              "PluginSteps": { "Contoso.Plugins.OnCreate: Create of account": false }
            }
            """;

        var document = SettingsFileReader.Parse(json);

        document.CloudFlows.Should().ContainSingle()
            .Which.Should().Be(new ComponentStateEntry("Order Processing", false));
        document.PluginSteps.Should().ContainSingle()
            .Which.Name.Should().Be("Contoso.Plugins.OnCreate: Create of account");
        document.PassThrough.Should().ContainKey("EnvironmentVariables");
        document.PassThrough.Should().NotContainKey(SettingsDocument.CloudFlowsProperty);
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
              "CloudFlows": { "B flow": false, "A flow": false }
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
            // Cheapest real failure per platform. On Windows a read-only target refuses the move into place,
            // which is exactly the window that used to truncate. Unix ignores the file's own permissions on a
            // rename, so there the folder is made unwritable instead and the temp write is what fails.
            if (OperatingSystem.IsWindows())
                File.SetAttributes(path, FileAttributes.ReadOnly);
            else
                File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);

            var act = () => SettingsFileReader.Save(new SettingsDocument(), path);

            act.Should().Throw<UnauthorizedAccessException>();

            Restore();
            File.ReadAllText(path).Should().Be(original);
            Directory.GetFiles(folder, "*.tmp").Should().BeEmpty("a failed write cleans up after itself");
        }
        finally
        {
            Restore();
            Directory.Delete(folder, recursive: true);
        }

        void Restore()
        {
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            }
            else if (Directory.Exists(folder))
            {
                File.SetUnixFileMode(folder,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
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

    // The shape people hand-edit. An array of { Name, Enabled } objects cost four lines per entry, which
    // buried the handful of deliberate exceptions a state section exists to carry.
    [Fact]
    public void Write_StateSections_AreNameToBooleanMaps()
    {
        var document = new SettingsDocument
        {
            CloudFlows = { new ComponentStateEntry("Order Processing", false) },
            Workflows = { new ComponentStateEntry("Escalate case", false) },
        };

        SettingsFileReader.Write(document).Should().Be(
            """
            {
              "CloudFlows": {
                "Order Processing": false
              },
              "Workflows": {
                "Escalate case": false
              }
            }
            """.ReplaceLineEndings("\n"));
    }

    // Cloud flows and classic workflows are both workflow rows in Dataverse but separate things to whoever
    // edits the file, so each keeps its own section and neither leaks into the other.
    [Fact]
    public void Parse_CloudFlowsAndWorkflows_AreKeptApart()
    {
        var document = SettingsFileReader.Parse("""
            {
              "CloudFlows": { "Order Processing": false },
              "Workflows": { "Escalate case": true }
            }
            """);

        document.CloudFlows.Should().ContainSingle().Which.Name.Should().Be("Order Processing");
        document.Workflows.Should().ContainSingle()
            .Which.Should().Be(new ComponentStateEntry("Escalate case", true));
    }

    [Fact]
    public void Parse_StateSectionWrittenAsAnArray_IsRejected()
    {
        var act = () => SettingsFileReader.Parse(
            """{ "CloudFlows": [ { "Name": "Order Processing", "Enabled": false } ] }""");

        act.Should().Throw<FlowlineException>()
            .Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
    }

    [Theory]
    [InlineData("""{ "CloudFlows": { "Order Processing": "false" } }""")]
    [InlineData("""{ "CloudFlows": { "Order Processing": null } }""")]
    [InlineData("""{ "CloudFlows": { "Order Processing": 0 } }""")]
    public void Parse_StateValueThatIsNotABoolean_IsRejected(string json)
    {
        var act = () => SettingsFileReader.Parse(json);

        act.Should().Throw<FlowlineException>()
            .Which.ExitCode.Should().Be(ExitCode.ConfigInvalid);
    }
}
