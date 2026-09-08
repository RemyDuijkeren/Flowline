using System.ComponentModel;
using Spectre.Console.Cli;

namespace Flowline;

// Base for every command that touches Dataverse (push, pull, generate, init, clone, deploy, drift,
// configure, provision). diff, scaffold, sln add and status never talk to Dataverse, so they stay on
// FlowlineSettings and never show these options (R30). Commands that name their environment through a
// flag rather than a positional target derive from EnvironmentSettings instead.
public class DataverseSettings : FlowlineSettings
{
    [CommandOption("--no-cache")]
    [Description("Re-run all pre-flight checks instead of using cached results")]
    public bool NoCache { get; set; } = false;

    [CommandOption("-a|--auto-select-auth-profile")]
    [Description("Automatically switch PAC CLI's active auth profile to match the one Flowline resolved, without asking — the switch is not restored afterward")]
    public bool AutoSwitchProfile { get; set; } = false;
}
