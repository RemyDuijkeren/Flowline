using System.ComponentModel;
using Spectre.Console.Cli;

namespace Flowline;

public class BaseCommandSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Enable verbose output")]
    public bool Verbose { get; set; } = false;
}
