using System.ComponentModel;
using Flowline.Core;
using Spectre.Console.Cli;

namespace Flowline.Settings;

public class FlowlineSettings : CommandSettings
{
    [CommandOption("-v|--verbose")]
    [Description("Show detailed command output")]
    public bool Verbose { get; set; } = false;

    // Shared by clone/configure/drift/generate/init — their only force-gated hazard is the cross-cutting
    // config-overwrite check (SettingsConsoleExtensions.Confirm), so their vocabulary is identical.
    internal static readonly string[] ConfigOnlyValidSpecifiers = ["config", "all"];

    internal static void ValidateForce(string[] force, string[] validSpecifiers, string commandName)
    {
        var invalid = force.FirstOrDefault(v => !validSpecifiers.Contains(v, StringComparer.OrdinalIgnoreCase));
        if (invalid != null)
            throw new FlowlineException(ExitCode.ValidationFailed,
                $"'{invalid}' isn't a valid --force value for '{commandName}'. Use one of: {string.Join(", ", validSpecifiers)}.");
    }
}
