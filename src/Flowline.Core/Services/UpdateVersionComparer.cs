using NuGet.Versioning;

namespace Flowline.Core.Services;

/// <summary>
/// Compares the running version against a set of published versions and names the newest one worth
/// telling the user about, channel-matched: a stable running version only looks at stable published
/// versions, a prerelease running version looks at all of them.
/// </summary>
public static class UpdateVersionComparer
{
    /// <summary>Newest published version strictly ahead of <paramref name="runningVersion"/>, or
    /// <c>null</c> when nothing published is newer, the running version can't be parsed, or the
    /// published list has no valid entries.</summary>
    public static string? GetNewerVersion(string runningVersion, IReadOnlyCollection<string> publishedVersions)
    {
        if (!NuGetVersion.TryParse(runningVersion, out var running)) return null;

        var candidates = publishedVersions
            .Select(v => NuGetVersion.TryParse(v, out var parsed) ? parsed : null)
            .Where(v => v != null && (running.IsPrerelease || !v.IsPrerelease))
            .Select(v => v!);

        var newest = candidates.OrderBy(v => v, VersionComparer.VersionRelease).LastOrDefault();

        return newest != null && newest > running ? newest.ToString() : null;
    }
}
