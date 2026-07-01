using System.ComponentModel;
using Spectre.Console.Cli;

namespace Flowline;

public class FlowlineSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Show detailed command output")]
    public bool Verbose { get; set; } = false;

    [CommandOption("-f|--force")]
    [Description("Allow operations that are blocked by default because they risk unrecoverable data loss")]
    public bool Force { get; set; } = false;

    [CommandOption("--no-cache")]
    [Description("Re-run all pre-flight checks instead of using cached results")]
    public bool NoCache { get; set; } = false;
}
