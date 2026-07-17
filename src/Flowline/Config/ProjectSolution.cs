using System.Text.Json.Serialization;

namespace Flowline.Config;

// Auto: today's zero-config default — a .nupkg anywhere under the plugins build output routes to the
// package path (R1/KD1), falling back to the classic .dll silently when the build produced none. Nupkg:
// require a .nupkg; fail loudly instead of falling back when the build didn't produce one — for a team
// that wants to guarantee it never silently regresses to a classic push. Dll: force the classic path even
// when a .nupkg exists — the only option for on-premises environments (Dependent Assemblies is cloud-only).
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PluginPackageMode
{
    Auto,
    Nupkg,
    Dll,
}

public class ProjectSolution
{
    public string UniqueName { get; init; } = null!;
    public bool IncludeManaged { get; set; } = false;
    public PluginPackageMode PluginPackageMode { get; set; } = PluginPackageMode.Auto;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GenerateConfig? Generate { get; set; }
}
