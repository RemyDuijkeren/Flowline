using Flowline.Core.Configure;
using FluentAssertions;

namespace Flowline.Core.Tests.Configure;

/// <summary>
/// The blanks a capture leaves behind, which are what the guided fill walks (R19, KTD24).
/// </summary>
public class SettingsFileGapsTests
{
    const string FileWithBlanks =
        """
        {
          "EnvironmentVariables": [
            { "SchemaName": "contoso_ApiUrl", "Value": "" },
            { "SchemaName": "contoso_Region", "Value": "west" },
            { "SchemaName": "contoso_ApiKey", "Value": "<set-this-secret>" }
          ],
          "ConnectionReferences": [
            {
              "LogicalName": "contoso_Dataverse",
              "ConnectionId": "",
              "ConnectorId": "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps"
            },
            {
              "LogicalName": "contoso_Mail",
              "ConnectionId": "shared-office365-0001",
              "ConnectorId": "/providers/Microsoft.PowerApps/apis/shared_office365"
            }
          ],
          "CloudFlows": { "ApprovalFlow": false }
        }
        """;

    static SettingsDocument Parse(string json) => SettingsFileReader.Parse(json);

    [Fact]
    public void Find_ReturnsOnlyTheEntriesWithNothingFilledIn()
    {
        var gaps = SettingsFileGaps.Find(Parse(FileWithBlanks));

        gaps.Select(g => g.Name).Should().Equal("contoso_ApiUrl", "contoso_Dataverse");
    }

    // Prompting for a secret would take one typed at a terminal and put it in a file the team commits.
    // The capture already warns about these; the fill leaves them alone.
    [Fact]
    public void Find_TreatsASecretPlaceholderAsFilledIn()
    {
        SettingsFileGaps.Find(Parse(FileWithBlanks))
            .Should().NotContain(g => g.Name == "contoso_ApiKey");
    }

    // The connector is what narrows the environment's connections to the ones that could bind, so it has
    // to survive from the file into the picker.
    [Fact]
    public void Find_CarriesTheConnectorOfAnUnboundReference()
    {
        var gap = SettingsFileGaps.Find(Parse(FileWithBlanks))
            .Single(g => g.Kind == SettingsGapKind.ConnectionReference);

        gap.ConnectorId.Should().Be("/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps");
    }

    [Fact]
    public void Find_OnAFullyFilledFile_ReturnsNothing()
    {
        var gaps = SettingsFileGaps.Find(Parse(
            """{ "EnvironmentVariables": [ { "SchemaName": "contoso_Region", "Value": "west" } ] }"""));

        gaps.Should().BeEmpty();
    }

    [Fact]
    public void Fill_SetsTheValueOnTheEntryItBelongsTo()
    {
        var document = Parse(FileWithBlanks);
        var gap = SettingsFileGaps.Find(document).First(g => g.Kind == SettingsGapKind.EnvironmentVariable);

        SettingsFileGaps.Fill(document, gap, "https://api.contoso.com").Should().BeTrue();

        SettingsFileGaps.Find(document).Should().NotContain(g => g.Name == "contoso_ApiUrl");
        SettingsFileReader.Write(document).Should().Contain("https://api.contoso.com");
    }

    [Fact]
    public void Fill_BindsAConnectionReference()
    {
        var document = Parse(FileWithBlanks);
        var gap = SettingsFileGaps.Find(document).Single(g => g.Kind == SettingsGapKind.ConnectionReference);

        SettingsFileGaps.Fill(document, gap, "shared-commondataser-0002").Should().BeTrue();

        SettingsFileReader.Write(document).Should().Contain("shared-commondataser-0002");
        SettingsFileGaps.Find(document).Should().NotContain(g => g.Kind == SettingsGapKind.ConnectionReference);
    }

    // The pass-through contract: PAC owns these sections, so filling one value must not disturb the
    // properties Flowline does not know about, nor the order they were written in.
    [Fact]
    public void Fill_LeavesEveryOtherPropertyAndTheOrderAlone()
    {
        var document = Parse(FileWithBlanks);
        var gap = SettingsFileGaps.Find(document).Single(g => g.Kind == SettingsGapKind.ConnectionReference);

        SettingsFileGaps.Fill(document, gap, "shared-commondataser-0002");

        var written = SettingsFileReader.Write(document);
        written.Should().Contain("shared_commondataserviceforapps");
        written.Should().Contain("shared-office365-0001");
        written.IndexOf("contoso_Dataverse", StringComparison.Ordinal)
            .Should().BeLessThan(written.IndexOf("contoso_Mail", StringComparison.Ordinal));
    }

    [Fact]
    public void Fill_ForANameThatIsNotThere_ReportsThatItDidNothing()
    {
        var document = Parse(FileWithBlanks);

        SettingsFileGaps
            .Fill(document, new SettingsGap(SettingsGapKind.EnvironmentVariable, "contoso_Missing"), "x")
            .Should().BeFalse();
    }
}
