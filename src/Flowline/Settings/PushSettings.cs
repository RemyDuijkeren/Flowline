using System.ComponentModel;
using Spectre.Console.Cli;

namespace Flowline.Settings;

/// <summary>What a push covers. Bare <c>push</c> is every target; a subcommand names one.</summary>
[Flags]
public enum PushTargets
{
    None = 0,
    Plugins = 1,
    WebResources = 2,
    All = Plugins | WebResources
}

/// <summary>
/// The options every push command takes, declared on the push branch so <c>push -h</c> lists them and
/// they bind on either side of the subcommand name (<c>push --dry-run plugins</c> and
/// <c>push plugins --dry-run</c> both work). Options that only mean something for one target live on
/// that target's settings, so the parser rejects them anywhere else.
/// </summary>
/// <remarks>
/// The members below the options are how each command tells the one push engine what to do. They
/// carry no attribute, so Spectre never sees them as options.
/// </remarks>
public abstract class PushSettings : SolutionNameSettings
{
    [CommandOption("--no-delete")]
    [Description("Push without deleting any Dataverse assets that are missing from source")]
    [DefaultValue(false)]
    public bool NoDelete { get; set; } = false;

    [CommandOption("--no-build")]
    [Description("Skip 'dotnet build' and push the existing Release artifacts")]
    [DefaultValue(false)]
    public bool NoBuild { get; set; } = false;

    [CommandOption("--dry-run")]
    [Description("Preview changes without touching Dataverse")]
    [DefaultValue(false)]
    public bool DryRun { get; set; } = false;

    internal abstract PushTargets Targets { get; }

    /// <summary>
    /// True when the user named the target. A named target the project doesn't have is an error;
    /// bare push skips it, since a project without web resources is a normal shape.
    /// </summary>
    internal bool TargetNamed => Targets != PushTargets.All;

    /// <summary>The pre-built artifact to push from outside a project, or null for project mode.</summary>
    internal virtual string? StandalonePath => null;

    internal virtual bool AssemblyOnly => false;

    internal virtual bool SkipPublish => false;
}
