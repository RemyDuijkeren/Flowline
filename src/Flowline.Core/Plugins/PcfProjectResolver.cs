namespace Flowline.Core.Plugins;

/// <summary>Answers whether a project is a Power Apps Component Framework (PCF) control (R10, KD5).</summary>
/// <remarks>
/// PCF is not a supported Flowline project type yet — this exists only so WebResources detection can
/// exclude PCF controls from its by-elimination candidate list, not because Flowline packs, pushes, or
/// registers them.
///
/// <b>Draft, not a settled contract.</b> The rules below come from reading real PCF control output
/// (extension, SDK package reference, manifest placement), not from a verification pass against the
/// range of shapes PCF tooling produces. When PCF becomes a first-class Flowline project type, its
/// detection needs its own verification pass against real controls (KD5) — this is a starting point for
/// that work, not the final word.
/// </remarks>
public static class PcfProjectResolver
{
    const string PcfSdkPackage = "Microsoft.PowerApps.MSBuild.Pcf";
    const string ControlManifestFileName = "ControlManifest.Input.xml";

    /// <summary>Whether <paramref name="projectFilePath"/> is a PCF control, by extension or by content.</summary>
    /// <remarks>
    /// Cheap, text-and-folder only — no MSBuild evaluation, matching the rest of discovery's build-free
    /// rule (R3). Never throws: an unreadable project file is "not a PCF project" rather than a failure,
    /// since this only ever gates an exclusion, not a build.
    ///
    /// <c>.pcfproj</c> is the primary signal — PCF's own project templates use that extension, never
    /// <c>.csproj</c>. The <c>.csproj</c>-wrapped case exists for a PCF control folded into a plain SDK
    /// project: the package reference is the direct signal, and the sibling manifest is a fallback for a
    /// project file that pulls the SDK reference in transitively (a <c>Directory.Build.props</c>, e.g.)
    /// and so carries none of the marker text itself.
    /// </remarks>
    public static bool IsPcfProject(string projectFilePath)
    {
        if (string.Equals(Path.GetExtension(projectFilePath), ".pcfproj", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            if (File.ReadAllText(projectFilePath).Contains(PcfSdkPackage, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return HasSiblingControlManifest(projectFilePath);
    }

    // PCF nests the control's source (and its manifest) in a subfolder named after the control, so the
    // manifest sits either beside the project file or one level down — never deeper.
    static bool HasSiblingControlManifest(string projectFilePath)
    {
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFilePath));
        if (projectDir == null)
            return false;

        try
        {
            if (File.Exists(Path.Combine(projectDir, ControlManifestFileName)))
                return true;

            return Directory.EnumerateDirectories(projectDir)
                             .Any(dir => File.Exists(Path.Combine(dir, ControlManifestFileName)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
