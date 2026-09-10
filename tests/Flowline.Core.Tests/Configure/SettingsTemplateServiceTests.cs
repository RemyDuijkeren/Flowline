using FluentAssertions;
using Flowline.Core.Configure;
using Xunit;

namespace Flowline.Core.Tests.Configure;

public class SettingsTemplateServiceTests
{
    static SettingsDocument Doc(string json) => SettingsFileReader.Parse(json);

    // The skeleton is what a real `pac solution create-settings` run emits: values empty, ConnectorId
    // present, and a third section beyond the two the published parameter docs list.
    const string PacSkeleton = """
        {
          "EnvironmentVariables": [
            { "SchemaName": "cr123_ApiUrl", "Value": "" }
          ],
          "ConnectionReferences": [
            { "LogicalName": "cr123_dataverse", "ConnectionId": "", "ConnectorId": "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps" }
          ],
          "CopilotAgents": []
        }
        """;

    // ── AE11: a new key appears with an empty value ────────────────────────────

    [Fact]
    public void Merge_KeyNotInExistingFile_IsAddedWithAnEmptyValue()
    {
        var result = SettingsTemplateService.Merge(Doc(PacSkeleton), existing: null);

        var written = SettingsFileReader.Write(result.Document);
        written.Should().Contain("\"cr123_ApiUrl\"").And.Contain("\"Value\": \"\"");
        result.Added.Should().Contain("EnvironmentVariables: cr123_ApiUrl");
        result.Added.Should().Contain("ConnectionReferences: cr123_dataverse");
    }

    // ── A value already in the file survives a refresh ──────────────────────────

    [Fact]
    public void Merge_ValueAlreadyInTheFile_IsPreservedAcrossARefresh()
    {
        var existing = Doc("""
            {
              "EnvironmentVariables": [ { "SchemaName": "cr123_ApiUrl", "Value": "https://pinned.invalid" } ],
              "ConnectionReferences": [ { "LogicalName": "cr123_dataverse", "ConnectionId": "conn-guid" } ]
            }
            """);

        var result = SettingsTemplateService.Merge(Doc(PacSkeleton), existing);

        SettingsFileReader.Write(result.Document).Should().Contain("https://pinned.invalid").And.Contain("conn-guid");
        result.Added.Should().BeEmpty();
    }

    // ── A key whose component left the solution is reported, not deleted ────────

    [Fact]
    public void Merge_KeyNoLongerInTheSkeleton_IsReportedVanishedButKept()
    {
        var existing = Doc("""
            {
              "EnvironmentVariables": [
                { "SchemaName": "cr123_ApiUrl", "Value": "" },
                { "SchemaName": "cr123_Retired", "Value": "old-value" }
              ],
              "ConnectionReferences": []
            }
            """);

        var result = SettingsTemplateService.Merge(Doc(PacSkeleton), existing);

        result.Vanished.Should().Contain("EnvironmentVariables: cr123_Retired");
        SettingsFileReader.Write(result.Document).Should().Contain("cr123_Retired").And.Contain("old-value");
    }

    // ── The merged template carries no cloud flow, workflow or plugin step section ──

    [Fact]
    public void Merge_DropsTheThreeStateSections()
    {
        var existing = Doc("""
            {
              "EnvironmentVariables": [],
              "ConnectionReferences": [],
              "CloudFlows": { "MyFlow": false },
              "Workflows": { "MyWorkflow": false },
              "PluginSteps": { "MyStep": false }
            }
            """);

        var result = SettingsTemplateService.Merge(Doc(PacSkeleton), existing);

        var written = SettingsFileReader.Write(result.Document);
        written.Should().NotContain("CloudFlows").And.NotContain("Workflows").And.NotContain("PluginSteps");
        result.Document.CloudFlows.Should().BeEmpty();
        result.Document.Workflows.Should().BeEmpty();
        result.Document.PluginSteps.Should().BeEmpty();
    }

    // ── A PAC-native section such as CopilotAgents survives the merge ───────────

    [Fact]
    public void Merge_CarriesAPacNativeSectionFlowlineDoesNotModel()
    {
        var result = SettingsTemplateService.Merge(Doc(PacSkeleton), existing: null);

        result.Document.PassThrough.Should().ContainKey("CopilotAgents");
    }

    // ── First run vs. refresh ────────────────────────────────────────────────

    [Fact]
    public void Merge_NoExistingFile_EveryKeyIsReportedAdded()
    {
        var result = SettingsTemplateService.Merge(Doc(PacSkeleton), existing: null);

        result.Added.Should().HaveCount(2);
    }

    [Fact]
    public void Merge_UnchangedSolution_ReportsNothing()
    {
        var existing = Doc(PacSkeleton);

        var result = SettingsTemplateService.Merge(Doc(PacSkeleton), existing);

        result.Added.Should().BeEmpty();
        result.Vanished.Should().BeEmpty();
    }
}
