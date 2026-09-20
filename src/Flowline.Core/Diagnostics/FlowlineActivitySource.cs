using System.Diagnostics;
using System.Reflection;

namespace Flowline.Core.Diagnostics;

public static class FlowlineActivitySource
{
    // In Core, not in the CLI project: the spinner helper next door starts a stage span for every
    // phase it shows, and Core may never reference Flowline.
    static readonly string s_version =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "0.0.0";

    public static readonly ActivitySource Source = new("Flowline.CLI", s_version);
}
