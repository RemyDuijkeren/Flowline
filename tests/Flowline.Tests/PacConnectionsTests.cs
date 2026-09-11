using Flowline.Utils;
using FluentAssertions;

namespace Flowline.Tests;

/// <summary>
/// The table `pac connection list` prints is the only contract available: the verb rejects `--json`.
/// </summary>
/// <remarks>
/// The sample below is verbatim output from PAC 2.11.2 against a live environment, spacing included, so a
/// change to PAC's layout fails here rather than in front of someone binding a connection reference.
/// </remarks>
public class PacConnectionsTests
{
    const string RealOutput =
        """
        Connected as admindwe@example.org
        Id                                                        Name                       API Id                                                              Status
        shared-commondataser-863397d0-10d3-43b1-8824-5fd5d954356a admindwe@example.org       /providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps Connected
        shared-translatorv2-b7534668-069a-4c68-aa1c-552dd957fc00  ROVM                       /providers/Microsoft.PowerApps/apis/shared_translatorv2             Connected
        shared-webcontents-d2b3af1c-13b0-4a1a-9017-f91ec7f4f3b8   HTTP Request AD            /providers/Microsoft.PowerApps/apis/shared_webcontents              Connected

        """;

    [Fact]
    public void Parse_ReadsEveryRowPastThePreamble()
    {
        var connections = PacConnections.Parse(RealOutput);

        connections.Should().HaveCount(3);
        connections[0].Id.Should().Be("shared-commondataser-863397d0-10d3-43b1-8824-5fd5d954356a");
        connections[0].ConnectorId.Should()
            .Be("/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps");
        connections[0].Status.Should().Be("Connected");
    }

    // The reason this parses by column offset: splitting on whitespace turns one connection into three.
    [Fact]
    public void Parse_KeepsANameThatContainsSpaces()
    {
        var connections = PacConnections.Parse(RealOutput);

        connections[2].Name.Should().Be("HTTP Request AD");
        connections[2].ConnectorId.Should().Be("/providers/Microsoft.PowerApps/apis/shared_webcontents");
    }

    // PAC pads each column to its widest cell, so one long name moves every column to its right. Offsets
    // have to come from the run's own header line, never from a constant.
    [Fact]
    public void Parse_FollowsTheHeaderWhenALongNameWidensTheColumn()
    {
        const string widened =
            """
            Connected as admindwe@example.org
            Id                                                       Name                                             API Id                                                  Status
            shared-translatorv2-b7534668-069a-4c68-aa1c-552dd957fc00 A connection with a very long descriptive label  /providers/Microsoft.PowerApps/apis/shared_translatorv2 Connected
            """;

        var connections = PacConnections.Parse(widened);

        connections.Should().ContainSingle();
        connections[0].Name.Should().Be("A connection with a very long descriptive label");
        connections[0].ConnectorId.Should().Be("/providers/Microsoft.PowerApps/apis/shared_translatorv2");
        connections[0].Status.Should().Be("Connected");
    }

    // The point of the picker: a connection reference can only bind to its own connector, so the other
    // two connections in this environment are noise.
    [Fact]
    public void ForConnector_KeepsOnlyTheConnectionsThatCouldBind()
    {
        var matching = PacConnections.ForConnector(
            PacConnections.Parse(RealOutput),
            "/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps");

        matching.Should().ContainSingle()
            .Which.Id.Should().Be("shared-commondataser-863397d0-10d3-43b1-8824-5fd5d954356a");
    }

    // Dataverse casing is not guaranteed to match what PAC prints.
    [Fact]
    public void ForConnector_MatchesRegardlessOfCase()
    {
        PacConnections.ForConnector(
                PacConnections.Parse(RealOutput),
                "/providers/Microsoft.PowerApps/apis/SHARED_WEBCONTENTS")
            .Should().ContainSingle().Which.Name.Should().Be("HTTP Request AD");
    }

    // Better to show every connection than to show none: an empty list reads as "this environment has
    // nothing", which is a different and wrong claim.
    [Fact]
    public void ForConnector_WithNoConnectorRecorded_KeepsEverything()
    {
        PacConnections.ForConnector(PacConnections.Parse(RealOutput), null).Should().HaveCount(3);
    }

    // An environment with no connections, or a `pac` that printed an error instead of a table. Neither is
    // a parse failure: the caller offers a typed id instead.
    [Fact]
    public void Parse_ReturnsNothingWhenThereIsNoTable()
    {
        PacConnections.Parse("Error: the environment could not be reached.").Should().BeEmpty();
        PacConnections.Parse("").Should().BeEmpty();
    }
}
