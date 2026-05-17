using System.Security.Cryptography;

namespace Flowline.Utils;

public record DriftWarning(DriftCategory Category, string RelativePath);

public enum DriftCategory { ContentDiffers, NewInDataverse, OnlyLocal, PluginSizeMismatch }

public static class DriftChecker
{
    private const long PluginSizeThresholdBytes = 10 * 1024; // 10 KB

    public static Task<List<DriftWarning>> CheckAsync(string slnFolder, CancellationToken cancellationToken = default)
    {
        var warnings = new List<DriftWarning>();
        warnings.AddRange(CheckWebResources(slnFolder));
        warnings.AddRange(CheckPlugins(slnFolder));
        return Task.FromResult(warnings);
    }

    static IEnumerable<DriftWarning> CheckWebResources(string slnFolder)
    {
        var distFolder = Path.Combine(slnFolder, "WebResources", "dist");
        var srcWebFolder = Path.Combine(slnFolder, "src", "WebResources");

        if (!Directory.Exists(distFolder) || !Directory.EnumerateFiles(distFolder, "*.*", SearchOption.AllDirectories).Any())
            yield break;

        var distHashes = GetFileHashes(distFolder);
        var srcHashes = Directory.Exists(srcWebFolder) ? GetFileHashes(srcWebFolder) : new Dictionary<string, byte[]>();

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

    static IEnumerable<DriftWarning> CheckPlugins(string slnFolder)
    {
        var releaseFolder = Path.Combine(slnFolder, "Plugins", "bin", "Release");
        var pluginAssembliesFolder = Path.Combine(slnFolder, "src", "PluginAssemblies");

        if (!Directory.Exists(releaseFolder) || !Directory.Exists(pluginAssembliesFolder))
            yield break;

        var srcDlls = Directory.EnumerateFiles(pluginAssembliesFolder, "*.dll", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetFileName(f), f => new FileInfo(f).Length, StringComparer.OrdinalIgnoreCase);

        foreach (var releaseDll in Directory.EnumerateFiles(releaseFolder, "*.dll", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(releaseDll);
            if (!srcDlls.TryGetValue(name, out var srcSize))
                continue;

            var releaseSize = new FileInfo(releaseDll).Length;
            if (Math.Abs(releaseSize - srcSize) > PluginSizeThresholdBytes)
                yield return new DriftWarning(DriftCategory.PluginSizeMismatch, name);
        }
    }

    static Dictionary<string, byte[]> GetFileHashes(string baseFolder)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(baseFolder, "*.*", SearchOption.AllDirectories))
        {
            var relPath = Path.GetRelativePath(baseFolder, file);
            result[relPath] = SHA256.HashData(File.ReadAllBytes(file));
        }
        return result;
    }
}
