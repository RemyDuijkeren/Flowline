using System.Diagnostics;
using System.Reflection;

namespace Flowline.Diagnostics;

public static class FlowlineActivitySource
{
    static readonly string s_version =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? "0.0.0";

    public static readonly ActivitySource Source = new("Flowline.CLI", s_version);
}
