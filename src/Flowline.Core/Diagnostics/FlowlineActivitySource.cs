using System.Diagnostics;

namespace Flowline.Core.Diagnostics;

public static class FlowlineActivitySource
{
    // In Core, not in the CLI project: the spinner helper next door starts a stage span for every
    // phase it shows, and Core may never reference Flowline.
    //
    // The package version, not the file version: MinVer stamps every prerelease with the same 4-part
    // file version, and this is what App Insights shows as the application version.
    public static readonly ActivitySource Source = new("Flowline.CLI", FlowlineVersion.Display);
}
