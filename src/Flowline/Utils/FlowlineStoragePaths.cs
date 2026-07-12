namespace Flowline.Utils;

static class FlowlineStoragePaths
{
    public static string GetStorageRoot()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");

        if (string.IsNullOrWhiteSpace(root))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            root = string.IsNullOrWhiteSpace(home)
                ? Path.GetTempPath()
                : Path.Combine(home, ".cache");
        }

        return Path.Combine(root, "Flowline");
    }

    public static string GetLogsPath(DateTimeOffset runTime, string? command = null)
    {
        var suffix = string.IsNullOrWhiteSpace(command) ? "" : $"-{command}";
        return Path.Combine(GetStorageRoot(), "logs", $"{runTime.UtcDateTime:yyyy-MM-ddTHHmmss}Z{suffix}.log");
    }

    // U2: one form-event identity cache file per environment, so a rename lookup on one environment never
    // suggests a formId that only exists on another. Sanitized so the environment URL is filesystem-safe
    // on every platform (Path.GetInvalidFileNameChars() alone still leaves '.' and '/' in an http(s) URL).
    public static string GetFormEventCachePath(string environmentUrl)
    {
        var sanitized = SanitizeForFileName(environmentUrl);
        return Path.Combine(GetStorageRoot(), "form-events", $"{sanitized}.json");
    }

    static string SanitizeForFileName(string value)
    {
        // Canonicalize before sanitizing: a trailing slash or casing difference (e.g. a `--dev` flag vs.
        // a CI script's URL) must map to the same cache file, not silently fragment the rename cache.
        var canonical = value.TrimEnd('/').ToLowerInvariant();
        var withoutScheme = canonical
            .Replace("https://", "")
            .Replace("http://", "");

        var invalidChars = Path.GetInvalidFileNameChars();
        var chars = new char[withoutScheme.Length];
        for (var i = 0; i < withoutScheme.Length; i++)
        {
            var c = withoutScheme[i];
            chars[i] = c is '.' or ':' or '/' || Array.IndexOf(invalidChars, c) >= 0 ? '_' : c;
        }

        return new string(chars);
    }
}
