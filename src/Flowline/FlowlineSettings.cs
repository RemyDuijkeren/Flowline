using System.ComponentModel;
using Spectre.Console.Cli;

namespace Flowline;

public class FlowlineSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Show command details")]
    public bool Verbose { get; set; } = false;

    [CommandOption("--json")]
    [Description("Write machine-readable JSON")]
    public bool JsonOutput { get; set; } = false;

    [CommandOption("-f|--force")]
    [Description("Skip confirmation prompts")]
    public bool Force { get; set; } = false;

    [CommandOption("--no-cache")]
    [Description("Refresh validation checks instead of using the local validation cache")]
    public bool NoCache { get; set; } = false;
}
