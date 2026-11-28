namespace Flowline.Core.ConnectionReferences;

/// <summary>A readable name for a connector id such as <c>/providers/Microsoft.PowerApps/apis/shared_office365</c>.</summary>
public static class ConnectorNames
{
    // ponytail: Dataverse stores only the connector id, and the real display name lives behind the Power Apps
    // connector API (a second token audience). Common connectors are mapped here; any other shows its id
    // without "shared_". Fetch from that API if users keep meeting unmapped connectors.
    static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["shared_commondataserviceforapps"] = "Microsoft Dataverse",
        ["shared_commondataservice"] = "Common Data Service (legacy)",
        ["shared_dynamicscrmonline"] = "Dynamics 365 (deprecated)",
        ["shared_office365"] = "Office 365 Outlook",
        ["shared_outlook"] = "Outlook.com",
        ["shared_office365users"] = "Office 365 Users",
        ["shared_office365groups"] = "Office 365 Groups",
        ["shared_sharepointonline"] = "SharePoint",
        ["shared_onedriveforbusiness"] = "OneDrive for Business",
        ["shared_excelonlinebusiness"] = "Excel Online (Business)",
        ["shared_teams"] = "Microsoft Teams",
        ["shared_planner"] = "Planner",
        ["shared_approvals"] = "Approvals",
        ["shared_webcontents"] = "HTTP with Entra ID",
        ["shared_microsoftcopilotstudio"] = "Microsoft Copilot Studio",
        ["shared_flowmanagement"] = "Power Automate Management",
        ["shared_powerplatformforadmins"] = "Power Platform for Admins",
        ["shared_sql"] = "SQL Server",
        ["shared_azureblob"] = "Azure Blob Storage",
        ["shared_keyvault"] = "Azure Key Vault",
        ["shared_servicebus"] = "Service Bus",
    };

    public static string Display(string connectorId)
    {
        var key = connectorId.TrimEnd('/');
        key = key[(key.LastIndexOf('/') + 1)..];
        if (Known.TryGetValue(key, out var name)) return name;
        return key.StartsWith("shared_", StringComparison.OrdinalIgnoreCase) ? key["shared_".Length..] : key;
    }
}
