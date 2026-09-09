using System.ComponentModel;
using Spectre.Console.Cli;

namespace Flowline;

// Base for the commands that name the environment they act on with a flag: push, pull, generate, init
// and clone. deploy, drift and configure take that environment as their positional <target> instead, so
// they stay on DataverseSettings and never show this --env (KD1, R2). Provision also stays on
// DataverseSettings — it needs --env too, but for the Production source it copies from rather than the
// environment it acts on (that's the positional role), so it declares its own copy with its own wording
// instead of inheriting this one.
public class EnvironmentSettings : DataverseSettings
{
    [CommandOption("-e|--env <ROLE|URL>")]
    [Description("Environment to connect to: dev, test, uat, prod, or a URL (saved to .flowline)")]
    public string? Env { get; set; }
}
