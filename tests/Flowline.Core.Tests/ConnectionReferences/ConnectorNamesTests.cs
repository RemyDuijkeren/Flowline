using FluentAssertions;
using Flowline.Core.ConnectionReferences;
using Xunit;

namespace Flowline.Core.Tests.ConnectionReferences;

public class ConnectorNamesTests
{
    [Theory]
    [InlineData("/providers/Microsoft.PowerApps/apis/shared_commondataserviceforapps", "Microsoft Dataverse")]
    [InlineData("/providers/Microsoft.PowerApps/apis/SHARED_OFFICE365", "Office 365 Outlook")]
    [InlineData("/providers/Microsoft.PowerApps/apis/shared_someunknownconnector", "someunknownconnector")]
    [InlineData("/providers/Microsoft.PowerApps/apis/shared_contoso-5f1c2a3b4d", "contoso-5f1c2a3b4d")]
    [InlineData("", "")]
    public void Display_MapsKnownConnectors_ElseShowsIdWithoutShared(string connectorId, string expected) =>
        ConnectorNames.Display(connectorId).Should().Be(expected);
}
