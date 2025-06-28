using System.ComponentModel;
using Spectre.Console.Cli;

namespace FlowLineCli;

public class FlowlineCommandSettings : CommandSettings
{
    [CommandArgument(0, "<environment>")]
    [Description("The Power Platform environment to work with")]
    public string Environment { get; set; } = null!;
}
