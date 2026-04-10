using Microsoft.Xrm.Sdk;
using Microsoft.Xrm.Sdk.Messages;
using Microsoft.Xrm.Sdk.Query;
using Microsoft.PowerPlatform.Dataverse.Client;

namespace Flowline.Core.Models;

public enum WebResourceType
{
    HTML = 1,
    HTM = 1,
    CSS = 2,
    JS = 3,
    XML = 4,
    XAML = 4,
    XSD = 4,
    PNG = 5,
    JPG = 6,
    JPEG = 6,
    GIF = 7,
    XAP = 8,
    XSL = 9,
    XSLT = 9,
    ICO = 10,
    SVG = 11, // D365 only
    RESX = 12 // D365 only
}

public enum WebResourceAction
{
    Create,
    Update,
    UpdateAndAddToPatchSolution,
    Delete
}

public record WebResourceSyncResult(bool Success, string Message);
