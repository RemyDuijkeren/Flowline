using System.Security.Cryptography;
using Flowline.Core.Services;

namespace Flowline.Utils;

public record DriftWarning(DriftCategory Category, string RelativePath);

public enum DriftCategory { ContentDiffers, NewInDataverse, OnlyLocal, PluginSizeMismatch, OrphanAssembly }

public static class PluginWebResourceDriftChecker
{
    private const long PluginSizeThresholdBytes = 10 * 1024; // 10 KB

    /// <summary>
    /// Compares local build output against the unpacked solution under <paramref name="dataverseSolutionFolder"/>.
    /// </summary>
    /// <remarks>
    /// Takes the already-resolved <paramref name="layout"/> rather than loading one itself (R4) — deploy
    /// and sync both already hold one by the time they call this, so reloading here would read the solution
    /// file a second time per run. Plugin build output comes from <see cref="SolutionFileLayout.PluginProjects"/>
    /// — every plugin project the solution references gets checked, whatever it is named — and the
    /// WebResources folder from <see cref="SolutionFileLayout.WebResourcesProjectPath"/> — null when none is
    /// confidently identified, in which case only plugin checks run. No await left once the caller hands
    /// over an already-loaded layout, so this returns synchronously.
    /// </remarks>
    public static Task<List<DriftWarning>> CheckAsync(string slnFolder, SolutionFileLayout layout, string dataverseSolutionFolder, string? publisherPrefix = null, CancellationToken cancellationToken = default)
    {
        var releaseFolders = layout.PluginProjects
                             .Select(c => c.BuildOutputRoot)
                             .Where(Directory.Exists)
                             .ToList();

        var warnings = new List<DriftWarning>();
        // Null WebResources project is a legitimate state — skip the web-resource half, run plugin checks
        // only. The single loud warning is emitted by the command caller (push, sync, deploy), not here, so
        // it fires once per command instead of once per checker call.
        if (layout.WebResourcesProjectPath is { } webResourcesProject)
            warnings.AddRange(CheckWebResources(slnFolder, Path.GetDirectoryName(webResourcesProject)!, dataverseSolutionFolder, publisherPrefix, cancellationToken));
        warnings.AddRange(CheckPlugins(releaseFolders, dataverseSolutionFolder));
        warnings.AddRange(CheckOrphanAssemblies(releaseFolders, dataverseSolutionFolder));
        return Task.FromResult(warnings);
    }

    static IEnumerable<DriftWarning> CheckWebResources(string slnFolder, string webResourcesFolder, string dataverseSolutionFolder, string? publisherPrefix, CancellationToken cancellationToken = default)
    {
        var distFolder = Path.Combine(webResourcesFolder, "dist");
        var srcWebFolder = Path.Combine(dataverseSolutionFolder, "src", "WebResources");

        if (!Directory.Exists(distFolder) || !Directory.EnumerateFiles(distFolder, "*.*", SearchOption.AllDirectories).Any())
            yield break;

        var distHashes = GetFileHashes(distFolder, cancellationToken);
        var srcHashes = Directory.Exists(srcWebFolder) ? GetWebResourceSrcHashes(srcWebFolder, slnFolder, publisherPrefix, cancellationToken) : new Dictionary<string, byte[]>();

        foreach (var (relPath, srcHash) in srcHashes)
        {
            if (!distHashes.TryGetValue(relPath, out var distHash))
                yield return new DriftWarning(DriftCategory.NewInDataverse, relPath);
            else if (!srcHash.SequenceEqual(distHash))
                yield return new DriftWarning(DriftCategory.ContentDiffers, relPath);
        }

        foreach (var relPath in distHashes.Keys.Where(k => !srcHashes.ContainsKey(k)))
            yield return new DriftWarning(DriftCategory.OnlyLocal, relPath);
    }

    static Dictionary<string, byte[]> GetWebResourceSrcHashes(string srcWebFolder, string slnFolder, string? publisherPrefix, CancellationToken cancellationToken = default)
    {
        var solutionName = Path.GetFileName(slnFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var pattern = publisherPrefix != null ? $"{publisherPrefix}_{solutionName}" : $"*_{solutionName}";
        var publisherRoot = Directory.EnumerateDirectories(srcWebFolder, pattern, SearchOption.TopDirectoryOnly)
            .FirstOrDefault();

        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(srcWebFolder, "*.*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.EndsWith(".data.xml", StringComparison.OrdinalIgnoreCase)) continue;

            var sourceRoot = publisherRoot != null && file.StartsWith(publisherRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? publisherRoot
                : srcWebFolder;

            var relPath = Path.GetRelativePath(sourceRoot, file);
            using var fs = File.OpenRead(file);
            result[relPath] = SHA256.HashData(fs);
        }
        return result;
    }

    static IEnumerable<DriftWarning> CheckPlugins(IReadOnlyList<string> releaseFolders, string dataverseSolutionFolder)
    {
        var pluginAssembliesFolder = Path.Combine(dataverseSolutionFolder, "src", "PluginAssemblies");

        if (releaseFolders.Count == 0 || !Directory.Exists(pluginAssembliesFolder))
            yield break;

        var srcDlls = Directory.EnumerateFiles(pluginAssembliesFolder, "*.dll", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetFileName(f), f => new FileInfo(f).Length, StringComparer.OrdinalIgnoreCase);

        foreach (var releaseDll in EnumerateReleaseDlls(releaseFolders))
        {
            var name = Path.GetFileName(releaseDll);
            if (!srcDlls.TryGetValue(name, out var srcSize))
                continue;

            var releaseSize = new FileInfo(releaseDll).Length;
            if (Math.Abs(releaseSize - srcSize) > PluginSizeThresholdBytes)
                yield return new DriftWarning(DriftCategory.PluginSizeMismatch, name);
        }
    }

    static IEnumerable<DriftWarning> CheckOrphanAssemblies(IReadOnlyList<string> releaseFolders, string dataverseSolutionFolder)
    {
        var pluginAssembliesFolder = Path.Combine(dataverseSolutionFolder, "src", "PluginAssemblies");

        if (!Directory.Exists(pluginAssembliesFolder) || releaseFolders.Count == 0)
            yield break;

        // Every discovered project's output, not one folder's — an assembly is only an orphan when no
        // plugin project in the solution produces it. The old code also exempted the literal name
        // "Plugins.dll" from ever being reported; that exemption is gone, because the assembly a project
        // actually builds is now known. It was the wrong shape twice over: it exempted nothing for a
        // project with its own <AssemblyName>, and it hid the one case worth reporting — a stale
        // Plugins.dll left in the unpacked solution after the project was renamed.
        var releaseDlls = EnumerateReleaseDlls(releaseFolders)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var dll in Directory.EnumerateFiles(pluginAssembliesFolder, "*.dll", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(dll);
            if (!releaseDlls.Contains(name))
                yield return new DriftWarning(DriftCategory.OrphanAssembly, name);
        }
    }

    static IEnumerable<string> EnumerateReleaseDlls(IReadOnlyList<string> releaseFolders) =>
        releaseFolders.SelectMany(f => Directory.EnumerateFiles(f, "*.dll", SearchOption.AllDirectories));

    static Dictionary<string, byte[]> GetFileHashes(string baseFolder, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(baseFolder, "*.*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relPath = Path.GetRelativePath(baseFolder, file);
            using var fs = File.OpenRead(file);
            result[relPath] = SHA256.HashData(fs);
        }
        return result;
    }
}
