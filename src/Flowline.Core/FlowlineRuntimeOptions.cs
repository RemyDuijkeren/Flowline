namespace Flowline.Core;

public sealed record FlowlineToolVersions(
    string FlowlineVersion,
    string DotNetVersion,
    string PacVersion,
    string? PacInstallType,
    string GitVersion,
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
}
