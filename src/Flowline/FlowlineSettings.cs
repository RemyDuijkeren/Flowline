using System.ComponentModel;
using Spectre.Console.Cli;

namespace Flowline;

public class FlowlineSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    public bool Verbose { get; set; } = false;

    [CommandOption("--json")]
    [Description("Output machine-readable JSON for agents and CI")]
    public bool JsonOutput { get; set; } = false;

    [CommandOption("-f|--force")]
    [Description("Force the operation to continue without confirmation")]
    public bool Force { get; set; } = false;
}
