namespace Flowline.Core;

// DotNetVersion and GitVersion are nullable because standalone mode deliberately probes neither tool —
// it checks pac only. Null there means "not checked", which is the honest value: a placeholder like
// "n/a" would look like a version to anything aggregating this and couldn't be told apart from a real
// one. Project mode still fills both in.
public sealed record FlowlineToolVersions(
    string FlowlineVersion,
    string? DotNetVersion,
    string PacVersion,
    string? PacInstallType,
    string? GitVersion,
    string? GitBranch
);

public sealed class FlowlineRuntimeOptions
{
    public bool IsVerbose { get; set; }
    public string[] Force { get; set; } = [];
    public string? CommandName { get; set; }
    public string? ArgsRedacted { get; set; }
    public FlowlineToolVersions? ToolVersions { get; set; }
    public byte[]? TelemetrySalt { get; set; }
    public bool AutoSwitchProfile { get; set; }
}
