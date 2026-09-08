using System.ComponentModel;
using Spectre.Console.Cli;

namespace Flowline;

// Base for the commands that name their environment with a flag: push, pull, generate, init and clone.
// deploy, drift and configure take the environment as their positional <target> and provision takes a
// role, so they stay on DataverseSettings and never show --env (KD1, R2).
public class EnvironmentSettings : DataverseSettings
{
    [CommandOption("-e|--env <ROLE|URL>")]
    [Description("Target environment: dev, test, uat, prod, or a URL (default: dev)")]
    public string? Env { get; set; }
}
